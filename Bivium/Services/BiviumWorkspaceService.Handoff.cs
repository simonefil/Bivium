using Bivium.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Bivium.Services
{
    public sealed partial class BiviumWorkspaceService
    {
        #region Variabili di classe

        /// <summary>Un solo tentativo per generazione; le continuazioni non eseguono sotto lock</summary>
        private TaskCompletionSource<WorkspaceAttachResult> _handoffCompletion;

        /// <summary>Cancellazione del richiedente verificata anche nel commit Ready</summary>
        private CancellationToken _handoffRequesterCancellation;

        #endregion

        #region Metodi pubblici

        /// <summary>Richiede il drain live o acquisisce lo snapshot acknowledged di un owner disconnesso</summary>
        /// <param name="attachmentId">Attachment del richiedente</param>
        /// <param name="expectedGeneration">Generazione osservata nella conferma</param>
        /// <param name="cancellationToken">Lifecycle del richiedente</param>
        /// <returns>Esito senza revoca in caso di timeout o drain non confermato</returns>
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
                    // Un Ready già committato vince sulla scadenza dell'attesa locale
                    if (ReferenceEquals(this._handoffCompletion, completion))
                        this.CancelHandoffLocked(ex is TimeoutException ? "The active browser did not confirm the desktop handoff. Retry activation." : "Activation cancelled. Retry when connected.");
                    snapshot = this._snapshot;
                    subscribers = this.GetSubscribers();
                }
                this.NotifySubscribers(subscribers, snapshot);
                return await completion.Task.ConfigureAwait(false);
            }
        }

        /// <summary>Legge il watermark solo per l'owner del tentativo ancora valido</summary>
        /// <param name="token">Lease dell'owner, catturato dal componente server</param>
        /// <param name="id">Tentativo da drenare</param>
        /// <returns>Stamp oppure null per richieste obsolete</returns>
        internal WorkspaceHandoffStamp GetHandoffStamp(WorkspaceClientToken token, Guid id)
        {
            lock (this._lock)
            {
                if (!this.ValidateHandoffOwnerLocked(token, id))
                    return null;
                return new WorkspaceHandoffStamp(this._snapshot.Revision, this._desktopRuntime.Editor?.Id ?? Guid.Empty, this._desktopRuntime.Editor?.Revision ?? -1, this._desktopRuntime.Renamer?.Id ?? Guid.Empty, this._desktopRuntime.Renamer?.Revision ?? -1);
            }
        }

        /// <summary>Registra l'ack del freeze soltanto per owner e generazione del tentativo</summary>
        /// <param name="token">Lease catturato dall'owner</param>
        /// <param name="id">Tentativo congelato nel browser</param>
        /// <returns>True se il freeze è ancora pertinente</returns>
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

        /// <summary>Conferma Ready e trasferisce lease e snapshot nella stessa sezione critica</summary>
        /// <param name="token">Owner del drain</param>
        /// <param name="id">Tentativo confermato</param>
        /// <param name="stamp">Revisioni acknowledged, senza ricostruirle dal richiedente</param>
        /// <returns>True soltanto per il commit di questa richiesta</returns>
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

        /// <summary>Rifiuta il tentativo soltanto dall'owner autorizzato, conservando il suo lease</summary>
        /// <param name="token">Owner del tentativo</param>
        /// <param name="id">Richiesta rifiutata</param>
        /// <param name="message">Motivo riprovabile privo di dati del documento</param>
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

        /// <summary>Autorità del publisher dedicato, distinta dal permesso di avviare nuovi comandi</summary>
        /// <param name="token">Lease del publisher</param>
        /// <returns>True se l'owner è ancora autorevole durante il drain</returns>
        internal bool ValidatePublication(WorkspaceClientToken token)
        {
            lock (this._lock)
                return !this.IsStopped && this.ValidateLeaseLocked(token);
        }

        #endregion

        #region Metodi privati

        /// <summary>Valida identità, owner, generazione e scadenza prima di ogni Ready</summary>
        /// <param name="token">Lease dell'owner</param>
        /// <param name="id">Tentativo atteso</param>
        /// <returns>True solo per il tentativo attivo</returns>
        private bool ValidateHandoffOwnerLocked(WorkspaceClientToken token, Guid id)
        {
            WorkspaceHandoffSnapshot handoff = this._snapshot.Handoff;
            return !this.IsStopped && handoff != null && handoff.Id == id && handoff.OwnerAttachmentId == token?.AttachmentId && handoff.Generation == token.Generation && DateTime.UtcNow < handoff.DeadlineUtc && this.ValidateLeaseLocked(token);
        }

        /// <summary>Aggiorna il protocollo senza perdere lo stato delle altre superfici</summary>
        /// <param name="handoff">Richiesta oppure null per sbloccare l'owner</param>
        private void SetHandoffLocked(WorkspaceHandoffSnapshot handoff)
        {
            this._snapshot = new BiviumWorkspaceSnapshot(this._snapshot.Revision + 1, this._snapshot.Panels, this._snapshot.FloatingWindows, this._snapshot.ActiveClientLease, this._snapshot.Desktop, handoff, this._snapshot.Workflow, this._snapshot.Operation, this._snapshot.Upload);
        }

        /// <summary>Invalida il tentativo e completa l'attesa senza eseguire continuazioni sotto lock</summary>
        /// <param name="message">Motivo riprovabile</param>
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

        /// <summary>Costruisce l'esito usando solo lo stato autorevole sotto lock</summary>
        /// <param name="attachmentId">Richiedente</param>
        /// <param name="message">Errore riprovabile oppure vuoto</param>
        /// <returns>Esito corrente</returns>
        private WorkspaceAttachResult CreateHandoffResultLocked(string attachmentId, string message)
        {
            ActiveClientLeaseSnapshot lease = this._snapshot.ActiveClientLease;
            bool hasControl = lease != null && lease.AttachmentId == attachmentId && lease.Connected && this._attachments.ContainsKey(attachmentId ?? "");
            return new WorkspaceAttachResult { AttachmentId = attachmentId ?? "", ActiveLease = this._snapshot.ActiveClientLease, WorkspaceRevision = this._snapshot.Revision, HasControl = hasControl, RequiresTakeover = !hasControl, ErrorMessage = message };
        }

        #endregion
    }
}
