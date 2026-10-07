using System;
using System.Collections.Immutable;

namespace Bivium.Models
{
    /// <summary>Workflows actually migrated; no arbitrary command or serialized delegate</summary>
    public enum WorkspaceWorkflowKind { CreateFile, CreateDirectory, RenameEntry, DeleteEntries, CopyEntries, MoveEntries, TransferEntries, BatchRename, EditorExtensions, CreationPermissions, Permissions, Compress, Extract, Properties, About, Authentication, TerminalRename, TerminalClose, TerminalClipboard, EditorAlert, ResetWorkspace, EditorClose }

    /// <summary>Authoritative phase, never advanced by hydration alone</summary>
    public enum WorkspaceWorkflowPhase { AwaitingInput, Running, Succeeded, Failed, Cancelled, Dismissed, AwaitingConfirmation, AwaitingOverwrite }

    /// <summary>Arguments captured at invocation, independent of the panels of the following browser</summary>
    /// <param name="SourcePath">Rename source, or empty for creation</param>
    /// <param name="ParentPath">Captured target directory</param>
    /// <param name="PanelIndex">Origin panel for UI reconciliation only</param>
    /// <param name="Title">Title already used by the local workflow</param>
    /// <param name="Label">Field label</param>
    /// <param name="SelectionLength">Initial selection of the field</param>
    /// <param name="SourcePaths">Immutable selection captured for delete, absent for Input</param>
    /// <param name="Transfers">Captured transfer sources and mode</param>
    /// <param name="FromClipboard">Paste from the internal clipboard, not drag/drop</param>
    /// <param name="RenamerSessionId">Session owning the batch preview</param>
    /// <param name="RenameItems">Exact preview with temporary names assigned once</param>
    /// <param name="FormContext">Closed serialized context captured on open, never mutable references</param>
    /// <param name="ExtractToOwnFolder">Extraction mode captured from the existing command</param>
    public sealed record WorkspaceWorkflowInvocation(string SourcePath, string ParentPath, int PanelIndex, string Title, string Label, int SelectionLength, ImmutableArray<string> SourcePaths = default, ImmutableArray<WorkspaceTransferEntry> Transfers = default, bool FromClipboard = false, Guid RenamerSessionId = default, ImmutableArray<WorkspaceRenameItem> RenameItems = default, string FormContext = "", bool ExtractToOwnFolder = false);

    /// <summary>Immutable runtime read by the authorized adapter, not sent in global notifications</summary>
    /// <param name="Id">Stable identity of the workflow</param>
    /// <param name="Revision">CAS of the draft and the responses</param>
    /// <param name="Kind">Typed command</param>
    /// <param name="InvocationParameters">Captured parameters</param>
    /// <param name="Phase">Current phase</param>
    /// <param name="Draft">Current text of the Input form</param>
    /// <param name="QuestionId">Identity of the question, not regenerated on browser change</param>
    /// <param name="OperationId">Admitted operation, or empty</param>
    /// <param name="ErrorMessage">Workflow error</param>
    /// <param name="Conflicts">Materialized overwrite questions</param>
    /// <param name="ConflictIndex">Current question in the sequence</param>
    /// <param name="OverwritePaths">Explicitly approved sources</param>
    /// <param name="SkippedPaths">Explicitly skipped sources</param>
    public sealed record WorkspaceWorkflowSnapshot(Guid Id, long Revision, WorkspaceWorkflowKind Kind, WorkspaceWorkflowInvocation InvocationParameters, WorkspaceWorkflowPhase Phase, string Draft, Guid QuestionId, Guid OperationId, string ErrorMessage, ImmutableArray<WorkspaceOverwriteQuestion> Conflicts = default, int ConflictIndex = 0, ImmutableArray<string> OverwritePaths = default, ImmutableArray<string> SkippedPaths = default)
    {
        /// <summary>True while the question or operation belongs to the workspace</summary>
        public bool IsActive => this.Phase is WorkspaceWorkflowPhase.AwaitingInput or WorkspaceWorkflowPhase.AwaitingConfirmation or WorkspaceWorkflowPhase.AwaitingOverwrite or WorkspaceWorkflowPhase.Running or WorkspaceWorkflowPhase.Failed || (this.Kind is WorkspaceWorkflowKind.BatchRename or WorkspaceWorkflowKind.EditorExtensions or WorkspaceWorkflowKind.CreationPermissions or WorkspaceWorkflowKind.Permissions or WorkspaceWorkflowKind.Compress or WorkspaceWorkflowKind.Extract or WorkspaceWorkflowKind.TerminalRename or WorkspaceWorkflowKind.TerminalClose && this.Phase == WorkspaceWorkflowPhase.Cancelled);
    }

    /// <summary>Lightweight reference, without arguments and form drafts</summary>
    /// <param name="Id">Referenced workflow</param>
    /// <param name="Revision">Revision to read in the runtime</param>
    /// <param name="Kind">Workflow type</param>
    /// <param name="Phase">Current phase</param>
    public sealed record WorkspaceWorkflowReference(Guid Id, long Revision, WorkspaceWorkflowKind Kind, WorkspaceWorkflowPhase Phase);

    /// <summary>Consume-once response: identity and payload are not derived from the following UI state</summary>
    /// <param name="WorkflowId">Target workflow</param>
    /// <param name="Revision">Observed revision</param>
    /// <param name="QuestionId">Observed question</param>
    /// <param name="ResponseId">Stable identity also for a retransmission</param>
    /// <param name="Cancelled">Explicit cancellation by the user</param>
    /// <param name="Value">Confirmed value</param>
    /// <param name="Overwrite">Typed choice, present only for an overwrite question</param>
    public sealed record WorkspaceWorkflowResponse(Guid WorkflowId, long Revision, Guid QuestionId, Guid ResponseId, bool Cancelled, string Value, OverwriteChoice? Overwrite = null);

    /// <summary>Captured mode and source, without mutable panel entries</summary>
    /// <param name="SourcePath">Captured source</param>
    /// <param name="Mode">Mode resolved at invocation</param>
    public sealed record WorkspaceTransferEntry(string SourcePath, FileTransferMode Mode);

    /// <summary>Conflict materialized once; hydration does not query the filesystem again</summary>
    /// <param name="SourcePath">Source of the question</param>
    /// <param name="DestinationPath">Destination of the question</param>
    /// <param name="IsDirectory">Type captured for the pre-existing message</param>
    /// <param name="Title">Materialized title</param>
    /// <param name="Message">Materialized message</param>
    public sealed record WorkspaceOverwriteQuestion(string SourcePath, string DestinationPath, bool IsDirectory, string Title, string Message);

    /// <summary>One row of the exact preview with a temporary name assigned only once</summary>
    /// <param name="OriginalPath">Captured source</param>
    /// <param name="OriginalName">Original name for rollback</param>
    /// <param name="NewName">Exact name shown in the preview</param>
    /// <param name="TemporaryPath">Temporary path of the first pass</param>
    public sealed record WorkspaceRenameItem(string OriginalPath, string OriginalName, string NewName, string TemporaryPath);

    /// <summary>Duplicate does not perform any effect again; Denied includes revoked browsers</summary>
    public enum WorkspaceWorkflowResponseResult { Accepted, Duplicate, Stale, Denied }
}
