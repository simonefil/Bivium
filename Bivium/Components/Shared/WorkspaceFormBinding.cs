using Bivium.Models;
using Bivium.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Bivium.Components.Shared
{
    /// <summary>Adapter locale dei checkpoint; non viene conservato dal runtime server</summary>
    internal sealed class WorkspaceFormBinding
    {
        #region Stato adapter

        /// <summary>Ultimo checkpoint acknowledged</summary>
        internal WorkspaceWorkflowSnapshot Current { get; private set; }
        /// <summary>Un CAS rifiutato ferma il publisher senza retry implicito</summary>
        internal bool Rejected { get; private set; }
        /// <summary>La nuova lease ricrea l'adapter logico, senza ripetere la continuation</summary>
        internal long Generation { get; private set; }
        /// <summary>Rifiuto esplicito, senza modificare lo stato autorevole o riprovare alla cieca</summary>
        internal string Error { get; private set; } = "";
        /// <summary>Risposta catturata, stabile per doppio evento o ritrasmissione</summary>
        private WorkspaceWorkflowResponse _response;

        #endregion

        #region Metodi pubblici

        /// <summary>Adotta soltanto revisioni non anteriori al checkpoint locale</summary>
        /// <param name="workflow">Proiezione autorizzata</param>
        /// <param name="generation">Generazione della lease dell'adapter</param>
        /// <returns>True se il componente deve applicare la proiezione</returns>
        internal bool Adopt(WorkspaceWorkflowSnapshot workflow, long generation = 0)
        {
            if (this.Generation != generation)
            {
                this.Generation = generation;
                this.Current = null;
                this._response = null;
                this.Rejected = false;
                this.Error = "";
            }
            if (workflow == null || (this.Current?.Id == workflow.Id && (this.Rejected || workflow.Revision <= this.Current.Revision)))
                return false;
            if (this.Current?.Id != workflow.Id || this.Current.QuestionId != workflow.QuestionId)
            {
                this.Rejected = false;
                this._response = null;
                this.Error = "";
            }
            this.Current = workflow;
            return true;
        }

        /// <summary>Pubblica il draft senza avanzare la continuation</summary>
        /// <param name="service">Autorità workspace</param>
        /// <param name="token">Lease del publisher</param>
        /// <param name="draft">Valore corrente</param>
        /// <returns>True solo per il checkpoint acknowledged</returns>
        internal bool Publish(BiviumWorkspaceService service, WorkspaceClientToken token, string draft)
        {
            if (this.Rejected || this.Current == null || (this.Current.Phase != WorkspaceWorkflowPhase.AwaitingInput && !(this.Current.Phase == WorkspaceWorkflowPhase.Failed && BiviumWorkspaceService.IsEditableFormKind(this.Current.Kind))))
                return false;
            if (!service.TryUpdateWorkflowDraft(token, this.Current.Id, this.Current.Revision, draft, out WorkspaceWorkflowSnapshot acknowledged))
            {
                this.Rejected = true;
                this.Error = "The draft was not acknowledged. This browser cannot confirm the workflow.";
                return false;
            }
            this.Current = acknowledged;
            this._response = null;
            return true;
        }

        /// <summary>Consume-once sul checkpoint osservato, mai da dispose o hydration</summary>
        /// <param name="service">Autorità workspace</param>
        /// <param name="token">Lease che risponde</param>
        /// <param name="cancelled">Gesto esplicito di chiusura o annullamento</param>
        /// <returns>Esito senza retry implicito</returns>
        internal WorkspaceWorkflowResponseResult Respond(BiviumWorkspaceService service, WorkspaceClientToken token, bool cancelled)
        {
            if (this.Current == null || this.Rejected)
                return WorkspaceWorkflowResponseResult.Denied;
            if (this._response != null && this._response.Cancelled != cancelled)
                return WorkspaceWorkflowResponseResult.Denied;
            this._response ??= new WorkspaceWorkflowResponse(this.Current.Id, this.Current.Revision, this.Current.QuestionId, Guid.NewGuid(), cancelled, this.Current.Draft);
            WorkspaceWorkflowResponseResult result = service.RespondToWorkflow(token, this._response);
            if (result is WorkspaceWorkflowResponseResult.Stale or WorkspaceWorkflowResponseResult.Denied)
                this.Error = "This response was not accepted. The authoritative workflow is unchanged.";
            return result;
        }

        /// <summary>La barriera confronta lo stato, non attende operazioni lunghe</summary>
        /// <param name="service">Autorità workspace</param>
        /// <param name="token">Lease del drain</param>
        /// <param name="draft">Valore visualizzato</param>
        /// <param name="cancellationToken">Deadline del drain</param>
        /// <returns>True solo per lo stesso checkpoint autorevole</returns>
        internal Task<bool> FlushAsync(BiviumWorkspaceService service, WorkspaceClientToken token, string draft, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WorkspaceWorkflowSnapshot current = service.GetWorkflow(token);
            return Task.FromResult(!this.Rejected && current != null && current.Id == this.Current?.Id && current.Revision == this.Current.Revision && current.Draft == draft);
        }

        #endregion
    }
}
