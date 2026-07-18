using System;
using System.Collections.Generic;
using System.Threading;

namespace Bivium.Services
{
    /// <summary>
    /// Centralizes the thread-safe lifecycle of temporary runtime service subscribers
    /// </summary>
    /// <typeparam name="TNotification">Type of notification published by the service</typeparam>
    public abstract class SubscriptionServiceBase<TNotification>
    {
        #region Class Variables

        /// <summary>
        /// Synchronizes authoritative state and the subscriber registry in derived classes
        /// </summary>
        protected readonly object _lock = new object();

        /// <summary>
        /// Subscribers registered by identifier
        /// </summary>
        private readonly Dictionary<long, Action<TNotification>> _subscribers = new Dictionary<long, Action<TNotification>>();

        /// <summary>
        /// Next subscription identifier
        /// </summary>
        private long _nextSubscriptionId = 1;

        /// <summary>
        /// Whether the service has started its final shutdown
        /// </summary>
        private bool _stopped = false;

        #endregion

        #region Protected Methods

        /// <summary>
        /// Registers a subscriber and optionally links its lifecycle to a token
        /// </summary>
        /// <param name="subscriber">Callback to register</param>
        /// <param name="cancellationToken">Token that automatically detaches the subscriber</param>
        /// <returns>Idempotent subscription</returns>
        protected IDisposable RegisterSubscriber(Action<TNotification> subscriber, CancellationToken cancellationToken = default)
        {
            if (subscriber == null)
                throw new ArgumentNullException(nameof(subscriber));

            ServiceSubscription result;
            lock (this._lock)
            {
                this.ThrowIfStopped();

                // The subscriber and its release object become visible as one operation
                long subscriptionId = this._nextSubscriptionId++;
                this._subscribers.Add(subscriptionId, subscriber);
                result = new ServiceSubscription(this, subscriptionId, cancellationToken);
            }

            return result;
        }

        /// <summary>
        /// Returns a bounded copy of the subscribers to notify outside the lock
        /// </summary>
        /// <returns>Current subscribers</returns>
        protected Action<TNotification>[] GetSubscribers()
        {
            lock (this._lock)
            {
                // Dispatch uses a snapshot to avoid running application code under the service lock
                Action<TNotification>[] result = new Action<TNotification>[this._subscribers.Count];
                this._subscribers.Values.CopyTo(result, 0);
                return result;
            }
        }

        /// <summary>
        /// Notifies current subscribers without holding the shared lock
        /// </summary>
        /// <param name="notification">Notification to publish</param>
        protected void NotifySubscribers(TNotification notification)
        {
            this.NotifySubscribers(this.GetSubscribers(), notification);
        }

        /// <summary>
        /// Notifies a previously captured subscriber copy
        /// </summary>
        /// <param name="subscribers">Subscribers captured during the state commit</param>
        /// <param name="notification">Notification to publish</param>
        protected void NotifySubscribers(Action<TNotification>[] subscribers, TNotification notification)
        {
            for (int i = 0; i < subscribers.Length; i++)
            {
                try
                {
                    subscribers[i](notification);
                }
                catch (Exception ex)
                {
                    this.HandleSubscriberException(ex);
                }
            }
        }

        /// <summary>
        /// Starts shutdown once and atomically removes every subscriber
        /// </summary>
        /// <returns>True when the caller must complete shutdown of its domain state</returns>
        protected bool TryBeginStop()
        {
            lock (this._lock)
            {
                if (this._stopped)
                    return false;

                // No new subscription can be accepted after clearing the registry
                this._stopped = true;
                this._subscribers.Clear();
                return true;
            }
        }

        /// <summary>
        /// Prevents operations on a service that has started shutting down
        /// </summary>
        protected void ThrowIfStopped()
        {
            lock (this._lock)
            {
                if (this._stopped)
                    throw new ObjectDisposedException(this.GetType().Name);
            }
        }

        /// <summary>
        /// Handles a subscriber exception without interrupting the remaining dispatch
        /// </summary>
        /// <param name="exception">Exception raised by the callback</param>
        protected abstract void HandleSubscriberException(Exception exception);

        #endregion

        #region Properties

        /// <summary>
        /// Current number of registered subscribers
        /// </summary>
        protected int SubscriberCount
        {
            get
            {
                lock (this._lock)
                {
                    return this._subscribers.Count;
                }
            }
        }

        /// <summary>
        /// Whether the service has started shutting down
        /// </summary>
        protected bool IsStopped
        {
            get
            {
                lock (this._lock)
                {
                    return this._stopped;
                }
            }
        }

        #endregion

        #region Nested Classes

        /// <summary>
        /// Idempotent subscription owned by the base class
        /// </summary>
        private sealed class ServiceSubscription : IDisposable
        {
            #region Class Variables

            /// <summary>
            /// Synchronizes disposal and token registration
            /// </summary>
            private readonly object _syncRoot = new object();

            /// <summary>
            /// Service from which the subscription must be removed
            /// </summary>
            private SubscriptionServiceBase<TNotification> _owner;

            /// <summary>
            /// Subscription identifier
            /// </summary>
            private readonly long _subscriptionId;

            /// <summary>
            /// Registration owned for automatic detach
            /// </summary>
            private CancellationTokenRegistration _cancellationRegistration;

            #endregion

            #region Constructor

            /// <summary>
            /// Creates a subscription linked to its service
            /// </summary>
            /// <param name="owner">Owning service</param>
            /// <param name="subscriptionId">Subscription identifier</param>
            /// <param name="cancellationToken">Optional automatic-detach token</param>
            public ServiceSubscription(SubscriptionServiceBase<TNotification> owner, long subscriptionId, CancellationToken cancellationToken)
            {
                this._owner = owner ?? throw new ArgumentNullException(nameof(owner));
                this._subscriptionId = subscriptionId;

                if (cancellationToken.CanBeCanceled)
                {
                    // An already-cancelled token can invoke Dispose synchronously during Register
                    CancellationTokenRegistration registration = cancellationToken.Register(this.Dispose);
                    lock (this._syncRoot)
                    {
                        // If the callback already won the race, the local registration must not remain active
                        if (this._owner == null)
                            registration.Unregister();
                        else
                            this._cancellationRegistration = registration;
                    }
                }
            }

            #endregion

            #region Public Methods

            /// <summary>
            /// Detaches the subscriber and its registration idempotently
            /// </summary>
            public void Dispose()
            {
                SubscriptionServiceBase<TNotification> owner;
                CancellationTokenRegistration registration;
                lock (this._syncRoot)
                {
                    // Transferring ownership makes manual disposal and concurrent cancellation idempotent
                    owner = this._owner;
                    if (owner == null)
                        return;
                    this._owner = null;
                    registration = this._cancellationRegistration;
                    this._cancellationRegistration = default;
                }

                registration.Unregister();
                lock (owner._lock)
                {
                    owner._subscribers.Remove(this._subscriptionId);
                }
                GC.SuppressFinalize(this);
            }

            #endregion
        }

        #endregion
    }
}
