using System;
using System.Collections.Immutable;

namespace Bivium.Models
{
    /// <summary>Observable state of the admitted operation</summary>
    public enum WorkspaceOperationPhase { Running, CancellationRequested, Succeeded, Failed, Cancelled }

    /// <summary>Closed and immutable plan: contains no browser tokens or arbitrary executable actions</summary>
    /// <param name="Kind">Admitted operation</param>
    /// <param name="SourcePath">Captured source</param>
    /// <param name="ParentPath">Captured destination</param>
    /// <param name="Name">Confirmed and validated name</param>
    /// <param name="SourcePaths">Immutable delete selection captured at invocation</param>
    /// <param name="Transfers">Selection filtered only by the consumed decisions</param>
    /// <param name="OverwritePaths">Approved overwrites only</param>
    /// <param name="FromClipboard">Ownership of the cut reconciliation</param>
    /// <param name="RenamerSessionId">Session owning the batch</param>
    /// <param name="RenameItems">Materialized preview and temporary names</param>
    /// <param name="FormDraft">Acknowledged draft frozen at save time</param>
    /// <param name="FormContext">Original values for ownership and terminal identities</param>
    /// <param name="ExtractToOwnFolder">Extraction mode of the plan</param>
    internal sealed record WorkspaceOperationPlan(WorkspaceWorkflowKind Kind, string SourcePath, string ParentPath, string Name, ImmutableArray<string> SourcePaths = default, ImmutableArray<WorkspaceTransferEntry> Transfers = default, ImmutableArray<string> OverwritePaths = default, bool FromClipboard = false, Guid RenamerSessionId = default, ImmutableArray<WorkspaceRenameItem> RenameItems = default, string FormDraft = "", string FormContext = "", bool ExtractToOwnFolder = false);

    /// <summary>Lightweight projection of the workspace-owned task, without paths or execution plan</summary>
    /// <param name="Id">Stable identity of the task</param>
    /// <param name="Revision">CAS for cancellation requests</param>
    /// <param name="WorkflowId">Owning workflow</param>
    /// <param name="Kind">Admitted type</param>
    /// <param name="Phase">Current phase</param>
    /// <param name="FilesProcessed">Completed items</param>
    /// <param name="FilesFailed">Failed items</param>
    /// <param name="ErrorMessage">Final error</param>
    /// <param name="ProgressCurrent">Position published in the current step</param>
    /// <param name="ProgressTotal">Total of the current step</param>
    /// <param name="Stage">Label of the server-owned step</param>
    public sealed record WorkspaceOperationSnapshot(Guid Id, long Revision, Guid WorkflowId, WorkspaceWorkflowKind Kind, WorkspaceOperationPhase Phase, int FilesProcessed, int FilesFailed, string ErrorMessage, int ProgressCurrent = 0, int ProgressTotal = 0, string Stage = "")
    {
        /// <summary>A requested cancellation is not equivalent to task completion</summary>
        public bool IsRunning => this.Phase is WorkspaceOperationPhase.Running or WorkspaceOperationPhase.CancellationRequested;
    }

    /// <summary>Actual position of the item in the two passes, including a failed rollback</summary>
    public enum WorkspaceRenameItemPhase { Original, Temporary, Finalized, RolledBack, RollbackFailed }

    /// <summary>Immutable state read only by the authorized adapter, never in global notifications</summary>
    /// <param name="Item">Captured row of the plan</param>
    /// <param name="Phase">Last outcome of the step</param>
    /// <param name="CurrentPath">Position resulting from the file service</param>
    /// <param name="ErrorMessage">Row error, including rollback</param>
    public sealed record WorkspaceRenameItemState(WorkspaceRenameItem Item, WorkspaceRenameItemPhase Phase, string CurrentPath, string ErrorMessage);
}
