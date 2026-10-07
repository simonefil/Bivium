using Bivium.Models;
using Bivium.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Bivium.Components.Shared
{
    /// <summary>Local checkpoint adapter; it is not kept by the server runtime</summary>
    internal sealed class WorkspaceFormBinding
    {
        #region Adapter State

        /// <summary>Last acknowledged checkpoint</summary>
        internal WorkspaceWorkflowSnapshot Current { get; private set; }
        /// <summary>A rejected CAS stops the publisher without implicit retry</summary>
        internal bool Rejected { get; private set; }
        /// <summary>The new lease recreates the logical adapter, without repeating the continuation</summary>
        internal long Generation { get; private set; }
        /// <summary>Explicit rejection, without changing the authoritative state or blindly retrying</summary>
        internal string Error { get; private set; } = "";
        /// <summary>Captured response, stable across duplicate events or retransmission</summary>
        private WorkspaceWorkflowResponse _response;

        #endregion

        #region Public Methods

        /// <summary>Adopts only revisions not older than the local checkpoint</summary>
        /// <param name="workflow">Authorized projection</param>
        /// <param name="generation">Generation of the adapter lease</param>
        /// <returns>True if the component must apply the projection</returns>
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

        /// <summary>Publishes the draft without advancing the continuation</summary>
        /// <param name="service">Workspace authority</param>
        /// <param name="token">Publisher lease</param>
        /// <param name="draft">Current value</param>
        /// <returns>True only for the acknowledged checkpoint</returns>
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

        /// <summary>Consume-once on the observed checkpoint, never from dispose or hydration</summary>
        /// <param name="service">Workspace authority</param>
        /// <param name="token">Responding lease</param>
        /// <param name="cancelled">Explicit close or cancel gesture</param>
        /// <returns>Result without implicit retry</returns>
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

        /// <summary>The barrier compares the state, it does not wait for long operations</summary>
        /// <param name="service">Workspace authority</param>
        /// <param name="token">Drain lease</param>
        /// <param name="draft">Displayed value</param>
        /// <param name="cancellationToken">Drain deadline</param>
        /// <returns>True only for the same authoritative checkpoint</returns>
        internal Task<bool> FlushAsync(BiviumWorkspaceService service, WorkspaceClientToken token, string draft, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WorkspaceWorkflowSnapshot current = service.GetWorkflow(token);
            return Task.FromResult(!this.Rejected && current != null && current.Id == this.Current?.Id && current.Revision == this.Current.Revision && current.Draft == draft);
        }

        #endregion
    }
}
