using Bivium.Models;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Bivium.Services
{
    /// <summary>Task and cancellation owned by the workspace, never by the browser lease</summary>
    internal sealed class WorkspaceOperationRuntime
    {
        /// <summary>Admitted immutable plan</summary>
        internal WorkspaceOperationPlan Plan { get; init; }
        /// <summary>State protected by the workspace lock</summary>
        internal WorkspaceOperationSnapshot Snapshot { get; set; }
        /// <summary>Explicit cancellation or workspace stop, not browser revocation</summary>
        internal CancellationTokenSource Cancellation { get; init; }
        /// <summary>Task started only once after admission</summary>
        internal Task Work { get; set; }

        /// <summary>Two-pass state protected by the workspace lock and independent of the adapter</summary>
        internal List<WorkspaceRenameItemState> RenameState { get; set; } = new List<WorkspaceRenameItemState>();
        /// <summary>Same progress notification interval used by the pre-existing Commander</summary>
        internal DateTime LastProgressPublicationUtc { get; set; }
        /// <summary>Actual counts between two coalesced publications</summary>
        internal int FilesProcessed { get; set; }
        /// <summary>Failed roots preserved between publications</summary>
        internal int FilesFailed { get; set; }
        /// <summary>Actual step between two coalesced publications</summary>
        internal int ProgressCurrent { get; set; }
        /// <summary>Total of the actual step</summary>
        internal int ProgressTotal { get; set; }
        /// <summary>Label of the actual step</summary>
        internal string Stage { get; set; } = "";
        /// <summary>Private detailed result of the task, published atomically on completion</summary>
        internal string ResultDraft { get; set; }
    }
}
