using Bivium.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Bivium.Services
{
    /// <summary>
    /// Owns the global Bivium workspace runtime state
    /// </summary>
    public sealed class BiviumWorkspaceService : SubscriptionServiceBase<BiviumWorkspaceSnapshot>, IDisposable
    {
        #region Class Variables

        /// <summary>
        /// Application logger without workspace content
        /// </summary>
        private readonly ILogger<BiviumWorkspaceService> _logger;

        /// <summary>
        /// Dynamically updated application settings
        /// </summary>
        private readonly IOptionsMonitor<CommanderSettings> _settings;

        /// <summary>
        /// Current authoritative snapshot
        /// </summary>
        private BiviumWorkspaceSnapshot _snapshot = new BiviumWorkspaceSnapshot(0, null, new FloatingWindowsSnapshot(), null);

        /// <summary>
        /// Attachments registered by identifier
        /// </summary>
        private readonly Dictionary<string, ClientAttachment> _attachments = new Dictionary<string, ClientAttachment>(StringComparer.Ordinal);

        /// <summary>
        /// Stops the periodic maintenance loop
        /// </summary>
        private readonly CancellationTokenSource _maintenanceCancellation = new CancellationTokenSource();

        /// <summary>
        /// Periodic attachment cleanup task
        /// </summary>
        private readonly Task _maintenanceTask;

        /// <summary>
        /// Last generation assigned to a lease
        /// </summary>
        private long _leaseGeneration = 0;

        #endregion

        #region Constructor

        /// <summary>
        /// Creates the global workspace service
        /// </summary>
        /// <param name="logger">Application logger</param>
        /// <param name="settings">Application settings</param>
        public BiviumWorkspaceService(ILogger<BiviumWorkspaceService> logger, IOptionsMonitor<CommanderSettings> settings)
        {
            this._logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this._settings = settings ?? throw new ArgumentNullException(nameof(settings));
            this._maintenanceTask = Task.Run(() => this.RunMaintenanceAsync(this._maintenanceCancellation.Token));
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Returns an immutable copy of the current state
        /// </summary>
        /// <returns>Current snapshot</returns>
        public BiviumWorkspaceSnapshot GetSnapshot()
        {
            BiviumWorkspaceSnapshot result;

            lock (this._lock)
            {
                result = this._snapshot;
            }

            return result;
        }

        /// <summary>
        /// Returns aggregate metrics without IPs, labels, paths or terminal content
        /// </summary>
        /// <returns>Current runtime metrics</returns>
        public WorkspaceRuntimeMetrics GetMetrics()
        {
            lock (this._lock)
            {
                return new WorkspaceRuntimeMetrics
                {
                    AttachmentCount = this._attachments.Count,
                    SubscriberCount = this.SubscriberCount,
                    HasActiveLease = this._snapshot.ActiveClientLease != null,
                    Revision = this._snapshot.Revision
                };
            }
        }

        /// <summary>
        /// Returns the token cancelled atomically when the lease is revoked
        /// </summary>
        /// <param name="token">Current lease</param>
        /// <returns>Revocation token or an already-cancelled token when the lease is invalid</returns>
        public CancellationToken GetRevocationToken(WorkspaceClientToken token)
        {
            lock (this._lock)
            {
                if (!this.ValidateMutationLocked(token))
                    return new CancellationToken(true);
                return this._attachments[token.AttachmentId].Revocation.Token;
            }
        }

        /// <summary>
        /// Executes a short mutation only while the client still owns the lease
        /// </summary>
        /// <param name="token">Current lease</param>
        /// <param name="mutation">Authoritative commit to execute without long I/O or notifications</param>
        internal void ExecuteMutation(WorkspaceClientToken token, Action mutation)
        {
            if (!this.TryExecuteMutation(token, mutation))
                throw new UnauthorizedAccessException("The browser attachment no longer owns the workspace lease");
        }

        /// <summary>
        /// Attempts a commit that is atomic with respect to lease transitions
        /// </summary>
        /// <param name="token">Current lease</param>
        /// <param name="mutation">Authoritative commit to execute without long I/O or notifications</param>
        /// <returns>True when attachment and generation are still authoritative</returns>
        internal bool TryExecuteMutation(WorkspaceClientToken token, Action mutation)
        {
            if (mutation == null)
                throw new ArgumentNullException(nameof(mutation));

            lock (this._lock)
            {
                this.ThrowIfStopped();
                if (!this.ValidateMutationLocked(token))
                    return false;

                ClientAttachment attachment = this._attachments[token.AttachmentId];
                attachment.LastActivityUtc = DateTime.UtcNow;
                this.RefreshActiveLeaseActivityLocked(attachment.LastActivityUtc);
                mutation();
                return true;
            }
        }

        /// <summary>
        /// Explicitly resets panels and windows while preserving the current lease
        /// </summary>
        /// <param name="token">Requesting client lease</param>
        /// <returns>Reset authoritative snapshot</returns>
        public BiviumWorkspaceSnapshot ResetWorkspace(WorkspaceClientToken token)
        {
            Action<BiviumWorkspaceSnapshot>[] subscribers;
            BiviumWorkspaceSnapshot snapshot;
            lock (this._lock)
            {
                this.ThrowIfStopped();
                if (!this.ValidateMutationLocked(token))
                    throw new UnauthorizedAccessException("The browser attachment no longer owns the workspace lease");

                snapshot = new BiviumWorkspaceSnapshot(this._snapshot.Revision + 1, null, new FloatingWindowsSnapshot(), this._snapshot.ActiveClientLease);
                this._snapshot = snapshot;
                subscribers = this.GetSubscribers();
            }

            this.NotifySubscribers(subscribers, snapshot);
            return snapshot;
        }

        /// <summary>
        /// Initializes panels only when the workspace does not already own state
        /// </summary>
        /// <param name="token">Requesting client lease</param>
        /// <param name="panels">Initial panel state</param>
        /// <returns>Authoritative snapshot after initialization</returns>
        public BiviumWorkspaceSnapshot InitializePanels(WorkspaceClientToken token, WorkspacePanelsSnapshot panels)
        {
            Action<BiviumWorkspaceSnapshot>[] subscribers = Array.Empty<Action<BiviumWorkspaceSnapshot>>();
            BiviumWorkspaceSnapshot result;

            if (panels == null)
                throw new ArgumentNullException(nameof(panels));

            lock (this._lock)
            {
                this.ThrowIfStopped();
                if (!this.ValidateMutationLocked(token))
                    throw new UnauthorizedAccessException("The browser attachment no longer owns the workspace lease");
                if (this._snapshot.Panels == null)
                {
                    this._snapshot = new BiviumWorkspaceSnapshot(this._snapshot.Revision + 1, panels, this._snapshot.FloatingWindows, this._snapshot.ActiveClientLease);
                    subscribers = this.GetSubscribers();
                }

                result = this._snapshot;
            }

            this.NotifySubscribers(subscribers, result);
            return result;
        }

        /// <summary>
        /// Updates panels when the expected revision is still authoritative
        /// </summary>
        /// <param name="token">Requesting client lease</param>
        /// <param name="expectedRevision">Revision on which the update is based</param>
        /// <param name="panels">New panel state</param>
        /// <param name="snapshot">Authoritative snapshot after the attempt</param>
        /// <returns>True when the update was accepted</returns>
        public bool TryUpdatePanels(WorkspaceClientToken token, long expectedRevision, WorkspacePanelsSnapshot panels, out BiviumWorkspaceSnapshot snapshot)
        {
            Action<BiviumWorkspaceSnapshot>[] subscribers = Array.Empty<Action<BiviumWorkspaceSnapshot>>();
            bool result = false;

            if (panels == null)
                throw new ArgumentNullException(nameof(panels));

            lock (this._lock)
            {
                this.ThrowIfStopped();
                if (!this.ValidateMutationLocked(token))
                    throw new UnauthorizedAccessException("The browser attachment no longer owns the workspace lease");
                if (expectedRevision == this._snapshot.Revision)
                {
                    result = true;
                    if (this._snapshot.Panels != panels)
                    {
                        this._snapshot = new BiviumWorkspaceSnapshot(this._snapshot.Revision + 1, panels, this._snapshot.FloatingWindows, this._snapshot.ActiveClientLease);
                        subscribers = this.GetSubscribers();
                    }
                }

                snapshot = this._snapshot;
            }

            this.NotifySubscribers(subscribers, snapshot);
            return result;
        }

        /// <summary>
        /// Updates the terminal window when the expected revision is still authoritative
        /// </summary>
        /// <param name="token">Requesting client lease</param>
        /// <param name="expectedRevision">Expected revision</param>
        /// <param name="terminalWindow">New window state</param>
        /// <param name="snapshot">Authoritative snapshot after the attempt</param>
        /// <returns>True when the update was accepted</returns>
        public bool TryUpdateTerminalWindow(WorkspaceClientToken token, long expectedRevision, FloatingWindowSnapshot terminalWindow, out BiviumWorkspaceSnapshot snapshot)
        {
            Action<BiviumWorkspaceSnapshot>[] subscribers = Array.Empty<Action<BiviumWorkspaceSnapshot>>();
            bool result = false;
            if (terminalWindow == null)
                throw new ArgumentNullException(nameof(terminalWindow));

            lock (this._lock)
            {
                this.ThrowIfStopped();
                if (!this.ValidateMutationLocked(token))
                    throw new UnauthorizedAccessException("The browser attachment no longer owns the workspace lease");
                if (expectedRevision == this._snapshot.Revision)
                {
                    result = true;
                    if (this._snapshot.FloatingWindows.Terminal != terminalWindow)
                    {
                        FloatingWindowsSnapshot windows = new FloatingWindowsSnapshot(terminalWindow);
                        this._snapshot = new BiviumWorkspaceSnapshot(this._snapshot.Revision + 1, this._snapshot.Panels, windows, this._snapshot.ActiveClientLease);
                        subscribers = this.GetSubscribers();
                    }
                }

                snapshot = this._snapshot;
            }

            this.NotifySubscribers(subscribers, snapshot);
            return result;
        }

        /// <summary>
        /// Registers a new attachment and acquires control when the lease is free or expired
        /// </summary>
        /// <param name="remoteIp">IP observed by the server</param>
        /// <param name="clientLabel">Short client label</param>
        /// <returns>Attachment result</returns>
        public WorkspaceAttachResult AttachClient(string remoteIp, string clientLabel)
        {
            Action<BiviumWorkspaceSnapshot>[] subscribers = Array.Empty<Action<BiviumWorkspaceSnapshot>>();
            List<ClientAttachment> expiredAttachments = new List<ClientAttachment>();
            List<CancellationTokenSource> revocations = new List<CancellationTokenSource>();
            WorkspaceAttachResult result = new WorkspaceAttachResult();
            BiviumWorkspaceSnapshot snapshot;
            string attachmentId = Guid.NewGuid().ToString("N");
            DateTime now = DateTime.UtcNow;

            lock (this._lock)
            {
                this.ThrowIfStopped();

                // Remove abandoned attachments first so the lease is evaluated against current state
                this.CleanupExpiredAttachmentsLocked(now, expiredAttachments, revocations);

                // Register the new circuit even when it must wait for an explicit takeover
                ClientAttachment attachment = new ClientAttachment();
                attachment.AttachmentId = attachmentId;
                attachment.RemoteIp = remoteIp ?? "";
                attachment.ClientLabel = string.IsNullOrWhiteSpace(clientLabel) ? "Browser" : clientLabel.Trim();
                if (attachment.ClientLabel.Length > 160)
                    attachment.ClientLabel = attachment.ClientLabel.Substring(0, 160);
                attachment.ConnectedAtUtc = now;
                attachment.LastActivityUtc = now;
                this._attachments.Add(attachmentId, attachment);

                if (this._snapshot.ActiveClientLease == null)
                {
                    CancellationTokenSource previousRevocation = this.AcquireLeaseLocked(attachment, now);
                    if (previousRevocation != null)
                        revocations.Add(previousRevocation);
                    subscribers = this.GetSubscribers();
                    result.HasControl = true;
                }
                else
                {
                    result.RequiresTakeover = true;
                }

                result.AttachmentId = attachmentId;
                result.ActiveLease = this._snapshot.ActiveClientLease;
                result.WorkspaceRevision = this._snapshot.Revision;
                snapshot = this._snapshot;
            }

            // Cancellation, disposal and callbacks always remain outside the workspace lock
            this.CancelRevocations(revocations);
            this.ReleaseAttachments(expiredAttachments);
            this.NotifySubscribers(subscribers, snapshot);
            return result;
        }

        /// <summary>
        /// Atomically transfers control to a waiting attachment
        /// </summary>
        /// <param name="attachmentId">Requesting attachment</param>
        /// <param name="expectedGeneration">Generation observed during the challenge</param>
        /// <returns>Updated result</returns>
        public WorkspaceAttachResult TryTakeover(string attachmentId, long expectedGeneration)
        {
            Action<BiviumWorkspaceSnapshot>[] subscribers = Array.Empty<Action<BiviumWorkspaceSnapshot>>();
            WorkspaceAttachResult result = new WorkspaceAttachResult();
            BiviumWorkspaceSnapshot snapshot;
            CancellationTokenSource previousRevocation = null;
            DateTime now = DateTime.UtcNow;

            lock (this._lock)
            {
                this.ThrowIfStopped();
                ClientAttachment attachment;
                if (!this._attachments.TryGetValue(attachmentId ?? "", out attachment))
                    return result;

                ActiveClientLeaseSnapshot activeLease = this._snapshot.ActiveClientLease;
                bool canAcquire = activeLease == null || activeLease.Generation == expectedGeneration;
                if (canAcquire)
                {
                    attachment.LastActivityUtc = now;
                    previousRevocation = this.AcquireLeaseLocked(attachment, now);
                    subscribers = this.GetSubscribers();
                    result.HasControl = true;
                }
                else
                {
                    result.RequiresTakeover = true;
                }

                result.AttachmentId = attachment.AttachmentId;
                result.ActiveLease = this._snapshot.ActiveClientLease;
                result.WorkspaceRevision = this._snapshot.Revision;
                snapshot = this._snapshot;
            }

            if (previousRevocation != null)
                this.CancelAndDisposeTokenSource(previousRevocation);
            this.NotifySubscribers(subscribers, snapshot);
            return result;
        }

        /// <summary>
        /// Automatically reacquires control for an existing attachment only when no client is connected
        /// </summary>
        /// <param name="attachmentId">Attachment that came back online</param>
        /// <returns>Updated reconnect result</returns>
        public WorkspaceAttachResult TryReconnectClient(string attachmentId)
        {
            Action<BiviumWorkspaceSnapshot>[] subscribers = Array.Empty<Action<BiviumWorkspaceSnapshot>>();
            List<ClientAttachment> expiredAttachments = new List<ClientAttachment>();
            List<CancellationTokenSource> revocations = new List<CancellationTokenSource>();
            WorkspaceAttachResult result = new WorkspaceAttachResult();
            BiviumWorkspaceSnapshot snapshot;
            DateTime now = DateTime.UtcNow;

            lock (this._lock)
            {
                this.ThrowIfStopped();

                // A waiting client heartbeat must keep its attachment valid without granting the lease
                ActiveClientLeaseSnapshot observedLease = this._snapshot.ActiveClientLease;
                ClientAttachment observedAttachment;
                bool isWaitingAttachment = this._attachments.TryGetValue(attachmentId ?? "", out observedAttachment) && (observedLease == null || observedLease.AttachmentId != observedAttachment.AttachmentId);
                if (isWaitingAttachment)
                    observedAttachment.LastActivityUtc = now;

                // After cleanup the lease may retain the same owner, be free or require takeover
                this.CleanupExpiredAttachmentsLocked(now, expiredAttachments, revocations);
                ClientAttachment attachment;
                if (this._attachments.TryGetValue(attachmentId ?? "", out attachment))
                {
                    ActiveClientLeaseSnapshot activeLease = this._snapshot.ActiveClientLease;
                    bool sameConnectedOwner = activeLease != null && activeLease.Connected && activeLease.AttachmentId == attachment.AttachmentId && !attachment.Revocation.IsCancellationRequested;
                    if (sameConnectedOwner)
                    {
                        attachment.LastActivityUtc = now;
                        this.RefreshActiveLeaseActivityLocked(now);
                        result.HasControl = true;
                    }
                    else if (activeLease == null)
                    {
                        attachment.LastActivityUtc = now;
                        CancellationTokenSource previousRevocation = this.AcquireLeaseLocked(attachment, now);
                        if (previousRevocation != null)
                            revocations.Add(previousRevocation);
                        subscribers = this.GetSubscribers();
                        result.HasControl = true;
                    }
                    else
                    {
                        result.RequiresTakeover = true;
                    }

                    result.AttachmentId = attachment.AttachmentId;
                    result.ActiveLease = this._snapshot.ActiveClientLease;
                    result.WorkspaceRevision = this._snapshot.Revision;
                }

                snapshot = this._snapshot;
            }

            // Revocation callbacks must never run while the attachment registry is locked
            this.CancelRevocations(revocations);
            this.ReleaseAttachments(expiredAttachments);
            this.NotifySubscribers(subscribers, snapshot);
            return result;
        }

        /// <summary>
        /// Marks the browser disconnected and immediately revokes its mutation authority
        /// </summary>
        /// <param name="token">Lease of the browser leaving the page</param>
        public void MarkClientDisconnected(WorkspaceClientToken token)
        {
            Action<BiviumWorkspaceSnapshot>[] subscribers = Array.Empty<Action<BiviumWorkspaceSnapshot>>();
            BiviumWorkspaceSnapshot snapshot;
            CancellationTokenSource revocation;
            lock (this._lock)
            {
                ActiveClientLeaseSnapshot lease = this._snapshot.ActiveClientLease;
                if (token == null || lease == null || !lease.Connected)
                    return;
                if (lease.AttachmentId != token.AttachmentId || lease.Generation != token.Generation)
                    return;

                ClientAttachment attachment;
                if (!this._attachments.TryGetValue(token.AttachmentId ?? "", out attachment))
                    return;

                // Replace the token under lock but invoke its callbacks only after committing the snapshot
                revocation = this.RevokeAttachmentLocked(attachment);
                ActiveClientLeaseSnapshot disconnected = new ActiveClientLeaseSnapshot(lease.AttachmentId, lease.Generation, lease.RemoteIp, lease.ClientLabel, lease.ConnectedAtUtc, DateTime.UtcNow, false);
                this._snapshot = new BiviumWorkspaceSnapshot(this._snapshot.Revision + 1, this._snapshot.Panels, this._snapshot.FloatingWindows, disconnected);
                subscribers = this.GetSubscribers();
                snapshot = this._snapshot;
            }

            this.CancelAndDisposeTokenSource(revocation);
            this.NotifySubscribers(subscribers, snapshot);
        }

        /// <summary>
        /// Keeps an attachment alive only while it still owns the current generation
        /// </summary>
        /// <param name="token">Client token</param>
        /// <returns>True when the lease is still valid</returns>
        public bool Heartbeat(WorkspaceClientToken token)
        {
            lock (this._lock)
            {
                if (!this.ValidateMutationLocked(token))
                    return false;

                ClientAttachment attachment = this._attachments[token.AttachmentId];
                attachment.LastActivityUtc = DateTime.UtcNow;
                this.RefreshActiveLeaseActivityLocked(attachment.LastActivityUtc);
                return true;
            }
        }

        /// <summary>
        /// Validates a token server-side before a mutation
        /// </summary>
        /// <param name="token">Client token</param>
        /// <returns>True when attachment and generation are authoritative</returns>
        public bool ValidateMutation(WorkspaceClientToken token)
        {
            lock (this._lock)
            {
                bool result = this.ValidateMutationLocked(token);
                if (result)
                {
                    this._attachments[token.AttachmentId].LastActivityUtc = DateTime.UtcNow;
                    this.RefreshActiveLeaseActivityLocked(this._attachments[token.AttachmentId].LastActivityUtc);
                }
                return result;
            }
        }

        /// <summary>
        /// Detaches an attachment without changing the workspace or PTYs
        /// </summary>
        /// <param name="attachmentId">Attachment to detach</param>
        public void DetachClient(string attachmentId)
        {
            Action<BiviumWorkspaceSnapshot>[] subscribers = Array.Empty<Action<BiviumWorkspaceSnapshot>>();
            BiviumWorkspaceSnapshot snapshot;
            ClientAttachment attachment;
            lock (this._lock)
            {
                if (!this._attachments.Remove(attachmentId ?? "", out attachment))
                    return;

                if (this._snapshot.ActiveClientLease != null && this._snapshot.ActiveClientLease.AttachmentId == attachment.AttachmentId)
                {
                    this._snapshot = new BiviumWorkspaceSnapshot(this._snapshot.Revision + 1, this._snapshot.Panels, this._snapshot.FloatingWindows, null);
                    subscribers = this.GetSubscribers();
                }

                snapshot = this._snapshot;
            }

            this.NotifySubscribers(subscribers, snapshot);
            this.ReleaseAttachment(attachment);
        }

        /// <summary>
        /// Registers a temporary subscriber and returns the atomically captured snapshot
        /// </summary>
        /// <param name="subscriber">Callback invoked after updates</param>
        /// <param name="snapshot">Snapshot consistent with the registration instant</param>
        /// <returns>Subscription to release when the client detaches</returns>
        public IDisposable Subscribe(Action<BiviumWorkspaceSnapshot> subscriber, out BiviumWorkspaceSnapshot snapshot)
        {
            IDisposable result;

            lock (this._lock)
            {
                result = this.RegisterSubscriber(subscriber);
                snapshot = this._snapshot;
            }

            return result;
        }

        /// <summary>
        /// Registers a subscriber linked to a specific attachment lifecycle
        /// </summary>
        /// <param name="attachmentId">Previously registered attachment</param>
        /// <param name="subscriber">Circuit callback</param>
        /// <param name="snapshot">Current atomic snapshot</param>
        /// <returns>Subscription automatically cancelled when the attachment is cleaned up</returns>
        public IDisposable SubscribeAttachment(string attachmentId, Action<BiviumWorkspaceSnapshot> subscriber, out BiviumWorkspaceSnapshot snapshot)
        {
            IDisposable result;

            lock (this._lock)
            {
                this.ThrowIfStopped();
                ClientAttachment attachment;
                if (!this._attachments.TryGetValue(attachmentId ?? "", out attachment))
                    throw new InvalidOperationException("Attachment is not registered");
                result = this.RegisterSubscriber(subscriber, attachment.Lifetime.Token);
                snapshot = this._snapshot;
            }

            return result;
        }

        /// <summary>
        /// Stops the workspace and detaches every UI subscriber
        /// </summary>
        public void Stop()
        {
            List<ClientAttachment> attachments;
            lock (this._lock)
            {
                if (!this.TryBeginStop())
                    return;

                attachments = new List<ClientAttachment>(this._attachments.Values);
                this._attachments.Clear();
            }

            this._maintenanceCancellation.Cancel();
            this._maintenanceTask.GetAwaiter().GetResult();
            this.ReleaseAttachments(attachments);
        }

        /// <summary>
        /// Releases the service idempotently
        /// </summary>
        public void Dispose()
        {
            this.Stop();
            this._maintenanceCancellation.Dispose();
            GC.SuppressFinalize(this);
        }

        #endregion

        #region Protected Methods

        /// <summary>
        /// Logs a workspace callback failure without sensitive data
        /// </summary>
        /// <param name="exception">Exception raised by the subscriber</param>
        protected override void HandleSubscriberException(Exception exception)
        {
            this._logger.LogWarning(exception, "Workspace subscriber notification failed");
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Replaces the revocation token without invoking callbacks under the lock
        /// </summary>
        /// <param name="attachment">Attachment already protected by the workspace lock</param>
        /// <returns>Previous token to cancel outside the lock</returns>
        private CancellationTokenSource RevokeAttachmentLocked(ClientAttachment attachment)
        {
            CancellationTokenSource result = attachment.Revocation;
            attachment.Revocation = new CancellationTokenSource();
            return result;
        }

        /// <summary>
        /// Cancels and releases a token list after the critical section
        /// </summary>
        /// <param name="revocations">Tokens replaced during a lease transition</param>
        private void CancelRevocations(List<CancellationTokenSource> revocations)
        {
            for (int i = 0; i < revocations.Count; i++)
            {
                this.CancelAndDisposeTokenSource(revocations[i]);
            }
        }

        /// <summary>
        /// Cancels resources of removed attachments after the critical section
        /// </summary>
        /// <param name="attachments">Attachments no longer registered</param>
        private void ReleaseAttachments(List<ClientAttachment> attachments)
        {
            for (int i = 0; i < attachments.Count; i++)
            {
                this.ReleaseAttachment(attachments[i]);
            }
        }

        /// <summary>
        /// Cancels and releases every resource of a removed attachment
        /// </summary>
        /// <param name="attachment">Attachment no longer registered</param>
        private void ReleaseAttachment(ClientAttachment attachment)
        {
            this.CancelAndDisposeTokenSource(attachment.Revocation);
            this.CancelAndDisposeTokenSource(attachment.Lifetime);
        }

        /// <summary>
        /// Cancels a token while preventing a faulty callback from blocking remaining cleanup
        /// </summary>
        /// <param name="cancellation">Token owned by the workspace</param>
        private void CancelAndDisposeTokenSource(CancellationTokenSource cancellation)
        {
            try
            {
                cancellation.Cancel();
            }
            catch (Exception ex)
            {
                this._logger.LogWarning(ex, "Workspace cancellation callback failed");
            }
            finally
            {
                cancellation.Dispose();
            }
        }

        /// <summary>
        /// Acquires the lease while updating generation and revision in one critical section
        /// </summary>
        /// <param name="attachment">Attachment acquiring control</param>
        /// <param name="now">Transition instant</param>
        /// <returns>Previous token to cancel outside the lock, when present</returns>
        private CancellationTokenSource AcquireLeaseLocked(ClientAttachment attachment, DateTime now)
        {
            CancellationTokenSource result = null;

            // When ownership changes, immediately replace the token authorizing the previous browser
            ActiveClientLeaseSnapshot previousLease = this._snapshot.ActiveClientLease;
            ClientAttachment previousAttachment;
            if (previousLease != null && this._attachments.TryGetValue(previousLease.AttachmentId, out previousAttachment) && previousAttachment.AttachmentId != attachment.AttachmentId)
                result = this.RevokeAttachmentLocked(previousAttachment);
            if (attachment.Revocation.IsCancellationRequested)
            {
                attachment.Revocation.Dispose();
                attachment.Revocation = new CancellationTokenSource();
            }

            // Generation, lease and revision advance in the same workspace-lock-protected commit
            this._leaseGeneration++;
            ActiveClientLeaseSnapshot lease = new ActiveClientLeaseSnapshot(attachment.AttachmentId, this._leaseGeneration, attachment.RemoteIp, attachment.ClientLabel, attachment.ConnectedAtUtc, now);
            this._snapshot = new BiviumWorkspaceSnapshot(this._snapshot.Revision + 1, this._snapshot.Panels, this._snapshot.FloatingWindows, lease);
            return result;
        }

        /// <summary>
        /// Validates the token without leaving the critical section
        /// </summary>
        /// <param name="token">Mutation token to validate</param>
        /// <returns>True when attachment and generation are still authoritative</returns>
        private bool ValidateMutationLocked(WorkspaceClientToken token)
        {
            if (token == null || this._snapshot.ActiveClientLease == null)
                return false;
            ClientAttachment attachment;
            if (!this._attachments.TryGetValue(token.AttachmentId ?? "", out attachment) || attachment.Revocation.IsCancellationRequested)
                return false;

            if (this.IsLeaseExpiredLocked(DateTime.UtcNow))
                return false;

            return this._snapshot.ActiveClientLease.AttachmentId == token.AttachmentId && this._snapshot.ActiveClientLease.Generation == token.Generation;
        }

        /// <summary>
        /// Updates the exposed timestamp without producing an event for every heartbeat
        /// </summary>
        /// <param name="now">New last-activity instant</param>
        private void RefreshActiveLeaseActivityLocked(DateTime now)
        {
            ActiveClientLeaseSnapshot lease = this._snapshot.ActiveClientLease;
            if (lease == null)
                return;
            ActiveClientLeaseSnapshot refreshed = new ActiveClientLeaseSnapshot(lease.AttachmentId, lease.Generation, lease.RemoteIp, lease.ClientLabel, lease.ConnectedAtUtc, now, true);
            this._snapshot = new BiviumWorkspaceSnapshot(this._snapshot.Revision, this._snapshot.Panels, this._snapshot.FloatingWindows, refreshed);
        }

        /// <summary>
        /// Removes expired attachments that did not detach
        /// </summary>
        /// <param name="now">Instant used to evaluate expiration</param>
        /// <param name="expiredAttachments">Attachments to release outside the lock</param>
        /// <param name="revocations">Tokens to cancel outside the lock</param>
        /// <returns>True when state changed</returns>
        private bool CleanupExpiredAttachmentsLocked(DateTime now, List<ClientAttachment> expiredAttachments, List<CancellationTokenSource> revocations)
        {
            bool changed = false;
            int timeoutSeconds = Math.Max(15, this._settings.CurrentValue.TerminalRuntime.ClientLeaseTimeoutSeconds);
            TimeSpan timeout = TimeSpan.FromSeconds(timeoutSeconds);

            // Revoke the expired owner first to prevent it from remaining authoritative during cleanup
            if (this.IsLeaseExpiredLocked(now) && this._snapshot.ActiveClientLease != null)
            {
                ActiveClientLeaseSnapshot lease = this._snapshot.ActiveClientLease;
                ClientAttachment activeAttachment;
                if (this._attachments.TryGetValue(lease.AttachmentId, out activeAttachment))
                    revocations.Add(this.RevokeAttachmentLocked(activeAttachment));
                this._snapshot = new BiviumWorkspaceSnapshot(this._snapshot.Revision + 1, this._snapshot.Panels, this._snapshot.FloatingWindows, null);
                changed = true;
            }

            // Then remove inactive attachments while disposing their resources outside the lock
            List<string> expired = new List<string>();
            foreach (KeyValuePair<string, ClientAttachment> pair in this._attachments)
            {
                bool isActive = this._snapshot.ActiveClientLease != null && this._snapshot.ActiveClientLease.AttachmentId == pair.Key;
                if (!isActive && now - pair.Value.LastActivityUtc > timeout)
                    expired.Add(pair.Key);
            }
            for (int i = 0; i < expired.Count; i++)
            {
                ClientAttachment attachment = this._attachments[expired[i]];
                this._attachments.Remove(expired[i]);
                expiredAttachments.Add(attachment);
                changed = true;
            }
            return changed;
        }

        /// <summary>
        /// Keeps the attachment registry bounded even without new circuits
        /// </summary>
        /// <param name="cancellationToken">Application shutdown token</param>
        private async Task RunMaintenanceAsync(CancellationToken cancellationToken)
        {
            try
            {
                using PeriodicTimer timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
                while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    Action<BiviumWorkspaceSnapshot>[] subscribers = Array.Empty<Action<BiviumWorkspaceSnapshot>>();
                    List<ClientAttachment> expiredAttachments = new List<ClientAttachment>();
                    List<CancellationTokenSource> revocations = new List<CancellationTokenSource>();
                    BiviumWorkspaceSnapshot snapshot;
                    lock (this._lock)
                    {
                        if (this.IsStopped)
                            return;

                        // Capture a consistent snapshot and defer callbacks and disposal outside the critical section
                        if (this.CleanupExpiredAttachmentsLocked(DateTime.UtcNow, expiredAttachments, revocations))
                            subscribers = this.GetSubscribers();
                        snapshot = this._snapshot;
                    }
                    this.CancelRevocations(revocations);
                    this.ReleaseAttachments(expiredAttachments);
                    this.NotifySubscribers(subscribers, snapshot);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        /// <summary>
        /// Whether the current lease exceeded the configured timeout
        /// </summary>
        /// <param name="now">Instant against which expiration is evaluated</param>
        /// <returns>True when the lease is missing, disconnected or expired</returns>
        private bool IsLeaseExpiredLocked(DateTime now)
        {
            ActiveClientLeaseSnapshot lease = this._snapshot.ActiveClientLease;
            if (lease == null)
                return true;
            if (!lease.Connected)
                return true;

            ClientAttachment attachment;
            if (!this._attachments.TryGetValue(lease.AttachmentId, out attachment))
                return true;

            int timeoutSeconds = Math.Max(15, this._settings.CurrentValue.TerminalRuntime.ClientLeaseTimeoutSeconds);
            return now - attachment.LastActivityUtc > TimeSpan.FromSeconds(timeoutSeconds);
        }

        #endregion

        #region Nested Classes

        /// <summary>
        /// Private state of a registered attachment
        /// </summary>
        private sealed class ClientAttachment
        {
            /// <summary>
            /// Unpredictable attachment identifier
            /// </summary>
            public string AttachmentId { get; set; } = "";

            /// <summary>
            /// IP observed by the server
            /// </summary>
            public string RemoteIp { get; set; } = "";

            /// <summary>
            /// Short client label
            /// </summary>
            public string ClientLabel { get; set; } = "";

            /// <summary>
            /// Connection UTC instant
            /// </summary>
            public DateTime ConnectedAtUtc { get; set; }

            /// <summary>
            /// Last observed activity UTC instant
            /// </summary>
            public DateTime LastActivityUtc { get; set; }

            /// <summary>
            /// Token revoked during takeover or disconnection
            /// </summary>
            public CancellationTokenSource Revocation { get; set; } = new CancellationTokenSource();

            /// <summary>
            /// Token representing the complete attachment lifecycle
            /// </summary>
            public CancellationTokenSource Lifetime { get; } = new CancellationTokenSource();
        }

        #endregion
    }
}
