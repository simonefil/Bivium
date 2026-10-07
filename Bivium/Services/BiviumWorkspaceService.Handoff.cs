using Bivium.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Bivium.Services
{
    public sealed partial class BiviumWorkspaceService
    {
        #region Variabili di classe

        /// <summary>A single attempt per generation; continuations do not run under lock</summary>
        private TaskCompletionSource<WorkspaceAttachResult> _handoffCompletion;

        /// <summary>Requester cancellation also verified in the Ready commit</summary>
        private CancellationToken _handoffRequesterCancellation;

        #endregion

        #region Metodi pubblici

        /// <summary>Requests the live drain or acquires the acknowledged snapshot of a disconnected owner</summary>
        /// <param name="attachmentId">Requester attachment</param>
        /// <param name="expectedGeneration">Generation observed in the confirmation</param>
        /// <param name="cancellationToken">Requester lifecycle</param>
        /// <returns>Outcome with no revocation on timeout or unconfirmed drain</returns>
        internal async Task<WorkspaceAttachResult> TryTakeoverAsync(string attachmentId, long expectedGeneration, CancellationToken cancellationToken)
        {
            BiviumWorkspaceSnapshot snapshot;
            Action<BiviumWorkspaceSnapshot>[] subscribers;
            TaskCompletionSource<WorkspaceAttachResult> completion;
            CancellationTokenSource revocation = null;
            lock (this._lock)
            {
                this.ThrowIfStopped();
                if (cancellationToken.IsCancellationRequested || !this._attachments.ContainsKey(attachmentId ?? ""))
                    return this.CreateHandoffResultLocked(attachmentId, "Activation cancelled. Retry when connected.");
                ActiveClientLeaseSnapshot lease = this._snapshot.ActiveClientLease;
                if (lease != null && lease.Generation != expectedGeneration)
                    return this.CreateHandoffResultLocked(attachmentId, "Workspace ownership changed. Retry activation.");
                if (this._snapshot.Handoff != null)
                    return this.CreateHandoffResultLocked(attachmentId, "Another activation is pending. Retry shortly.");
                this._attachments[attachmentId].LastActivityUtc = DateTime.UtcNow;
                if (lease == null || !lease.Connected)
                {
                    revocation = this.AcquireLeaseLocked(this._attachments[attachmentId], DateTime.UtcNow);
                    completion = null;
                }
                else
                {
                    WorkspaceHandoffSnapshot handoff = new WorkspaceHandoffSnapshot(Guid.NewGuid(), lease.AttachmentId, attachmentId, lease.Generation, DateTime.UtcNow.AddSeconds(10));
                    completion = new TaskCompletionSource<WorkspaceAttachResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                    this._handoffCompletion = completion;
                    this._handoffRequesterCancellation = cancellationToken;
                    this.SetHandoffLocked(handoff);
                }
                snapshot = this._snapshot;
                subscribers = this.GetSubscribers();
            }
            if (revocation != null)
                this.CancelAndDisposeTokenSource(revocation);
            this.NotifySubscribers(subscribers, snapshot);
            if (completion == null)
            {
                lock (this._lock)
                    return this.CreateHandoffResultLocked(attachmentId, "");
            }
            try
            {
                TimeSpan remaining = snapshot.Handoff.DeadlineUtc - DateTime.UtcNow;
                return await completion.Task.WaitAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException || ex is OperationCanceledException)
            {
                lock (this._lock)
                {
                    // An already committed Ready wins over the expiry of the local wait
                    if (ReferenceEquals(this._handoffCompletion, completion))
                        this.CancelHandoffLocked(ex is TimeoutException ? "The active browser did not confirm the desktop handoff. Retry activation." : "Activation cancelled. Retry when connected.");
                    snapshot = this._snapshot;
                    subscribers = this.GetSubscribers();
                }
                this.NotifySubscribers(subscribers, snapshot);
                return await completion.Task.ConfigureAwait(false);
            }
        }

        /// <summary>Reads the watermark only for the owner of the still valid attempt</summary>
        /// <param name="token">Owner lease, captured by the server component</param>
        /// <param name="id">Attempt to drain</param>
        /// <returns>Stamp or null for stale requests</returns>
        internal WorkspaceHandoffStamp GetHandoffStamp(WorkspaceClientToken token, Guid id)
        {
            lock (this._lock)
            {
                if (!this.ValidateHandoffOwnerLocked(token, id))
                    return null;
                return new WorkspaceHandoffStamp(this._snapshot.Revision, this._desktopRuntime.Editor?.Id ?? Guid.Empty, this._desktopRuntime.Editor?.Revision ?? -1, this._desktopRuntime.Renamer?.Id ?? Guid.Empty, this._desktopRuntime.Renamer?.Revision ?? -1);
            }
        }

        /// <summary>Records the freeze ack only for the owner and generation of the attempt</summary>
        /// <param name="token">Lease captured by the owner</param>
        /// <param name="id">Attempt frozen in the browser</param>
        /// <returns>True if the freeze is still relevant</returns>
        internal bool TryConfirmHandoffFreeze(WorkspaceClientToken token, Guid id)
        {
            Action<BiviumWorkspaceSnapshot>[] subscribers;
            BiviumWorkspaceSnapshot snapshot;
            lock (this._lock)
            {
                if (!this.ValidateHandoffOwnerLocked(token, id))
                    return false;
                this.SetHandoffLocked(this._snapshot.Handoff with { Frozen = true });
                snapshot = this._snapshot;
                subscribers = this.GetSubscribers();
            }
            this.NotifySubscribers(subscribers, snapshot);
            return true;
        }

        /// <summary>Confirms Ready and transfers lease and snapshot in the same critical section</summary>
        /// <param name="token">Drain owner</param>
        /// <param name="id">Confirmed attempt</param>
        /// <param name="stamp">Acknowledged revisions, without rebuilding them from the requester</param>
        /// <returns>True only for the commit of this request</returns>
        internal bool TryCompleteHandoff(WorkspaceClientToken token, Guid id, WorkspaceHandoffStamp stamp)
        {
            CancellationTokenSource revocation;
            Action<BiviumWorkspaceSnapshot>[] subscribers;
            BiviumWorkspaceSnapshot snapshot;
            lock (this._lock)
            {
                if (stamp == null || !this.ValidateHandoffOwnerLocked(token, id) || !this._snapshot.Handoff.Frozen || this._handoffRequesterCancellation.IsCancellationRequested)
                    return false;
                if (stamp.WorkspaceRevision != this._snapshot.Revision || stamp.EditorId != (this._desktopRuntime.Editor?.Id ?? Guid.Empty) || stamp.EditorRevision != (this._desktopRuntime.Editor?.Revision ?? -1) || stamp.RenamerId != (this._desktopRuntime.Renamer?.Id ?? Guid.Empty) || stamp.RenamerRevision != (this._desktopRuntime.Renamer?.Revision ?? -1))
                    return false;
                string requester = this._snapshot.Handoff.RequesterAttachmentId;
                if (!this._attachments.TryGetValue(requester, out ClientAttachment attachment))
                    return false;
                TaskCompletionSource<WorkspaceAttachResult> completion = this._handoffCompletion;
                this._handoffCompletion = null;
                this._handoffRequesterCancellation = default;
                this.SetHandoffLocked(null);
                attachment.LastActivityUtc = DateTime.UtcNow;
                revocation = this.AcquireLeaseLocked(attachment, DateTime.UtcNow);
                snapshot = this._snapshot;
                subscribers = this.GetSubscribers();
                completion.TrySetResult(this.CreateHandoffResultLocked(requester, ""));
            }
            if (revocation != null)
                this.CancelAndDisposeTokenSource(revocation);
            this.NotifySubscribers(subscribers, snapshot);
            return true;
        }

        /// <summary>Rejects the attempt only from the authorized owner, preserving its lease</summary>
        /// <param name="token">Owner of the attempt</param>
        /// <param name="id">Rejected request</param>
        /// <param name="message">Retryable reason with no document data</param>
        internal void RejectHandoff(WorkspaceClientToken token, Guid id, string message)
        {
            Action<BiviumWorkspaceSnapshot>[] subscribers;
            BiviumWorkspaceSnapshot snapshot;
            lock (this._lock)
            {
                if (!this.ValidateHandoffOwnerLocked(token, id))
                    return;
                this.CancelHandoffLocked(message);
                snapshot = this._snapshot;
                subscribers = this.GetSubscribers();
            }
            this.NotifySubscribers(subscribers, snapshot);
        }

        /// <summary>Authority of the dedicated publisher, distinct from the permission to start new commands</summary>
        /// <param name="token">Publisher lease</param>
        /// <returns>True if the owner is still authoritative during the drain</returns>
        internal bool ValidatePublication(WorkspaceClientToken token)
        {
            lock (this._lock)
                return !this.IsStopped && this.ValidateLeaseLocked(token);
        }

        #endregion

        #region Metodi privati

        /// <summary>Validates identity, owner, generation and expiry before every Ready</summary>
        /// <param name="token">Owner lease</param>
        /// <param name="id">Expected attempt</param>
        /// <returns>True only for the active attempt</returns>
        private bool ValidateHandoffOwnerLocked(WorkspaceClientToken token, Guid id)
        {
            WorkspaceHandoffSnapshot handoff = this._snapshot.Handoff;
            return !this.IsStopped && handoff != null && handoff.Id == id && handoff.OwnerAttachmentId == token?.AttachmentId && handoff.Generation == token.Generation && DateTime.UtcNow < handoff.DeadlineUtc && this.ValidateLeaseLocked(token);
        }

        /// <summary>Updates the protocol without losing the state of the other surfaces</summary>
        /// <param name="handoff">Request or null to unblock the owner</param>
        private void SetHandoffLocked(WorkspaceHandoffSnapshot handoff)
        {
            this._snapshot = new BiviumWorkspaceSnapshot(this._snapshot.Revision + 1, this._snapshot.Panels, this._snapshot.FloatingWindows, this._snapshot.ActiveClientLease, this._snapshot.Desktop, handoff, this._snapshot.Workflow, this._snapshot.Operation, this._snapshot.Upload);
        }

        /// <summary>Invalidates the attempt and completes the wait without running continuations under lock</summary>
        /// <param name="message">Retryable reason</param>
        private void CancelHandoffLocked(string message)
        {
            if (this._snapshot.Handoff == null)
                return;
            string requester = this._snapshot.Handoff.RequesterAttachmentId;
            TaskCompletionSource<WorkspaceAttachResult> completion = this._handoffCompletion;
            this._handoffCompletion = null;
            this._handoffRequesterCancellation = default;
            this.SetHandoffLocked(null);
            completion?.TrySetResult(this.CreateHandoffResultLocked(requester, message));
        }

        /// <summary>Builds the outcome using only the authoritative state under lock</summary>
        /// <param name="attachmentId">Requester</param>
        /// <param name="message">Retryable error or empty</param>
        /// <returns>Current outcome</returns>
        private WorkspaceAttachResult CreateHandoffResultLocked(string attachmentId, string message)
        {
            ActiveClientLeaseSnapshot lease = this._snapshot.ActiveClientLease;
            bool hasControl = lease != null && lease.AttachmentId == attachmentId && lease.Connected && this._attachments.ContainsKey(attachmentId ?? "");
            return new WorkspaceAttachResult { AttachmentId = attachmentId ?? "", ActiveLease = this._snapshot.ActiveClientLease, WorkspaceRevision = this._snapshot.Revision, HasControl = hasControl, RequiresTakeover = !hasControl, ErrorMessage = message };
        }

        #endregion
    }
}
