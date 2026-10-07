using Bivium.Models;
using System;
using System.Collections.Generic;

namespace Bivium.Services
{
    /// <summary>Workflow state protected exclusively by the workspace lock</summary>
    internal sealed class WorkspaceWorkflowRuntime
    {
        /// <summary>Current workflow; no circuit component or callback is retained</summary>
        internal WorkspaceWorkflowSnapshot Current { get; set; }

        /// <summary>Answers to the current workflow's questions, even after advancing to the next conflict</summary>
        internal Dictionary<Guid, WorkspaceWorkflowResponse> Responses { get; } = new Dictionary<Guid, WorkspaceWorkflowResponse>();

        /// <summary>Small reference for global notifications</summary>
        internal WorkspaceWorkflowReference Reference => this.Current == null ? null : new WorkspaceWorkflowReference(this.Current.Id, this.Current.Revision, this.Current.Kind, this.Current.Phase);
    }
}
