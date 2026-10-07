using Bivium.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace Bivium.Services
{
    public sealed partial class BiviumWorkspaceService
    {
        #region Variabili di classe

        /// <summary>Existing singleton service, with no callbacks to the circuit</summary>
        private readonly IFileOperationService _workflowFiles;
        /// <summary>Existing path validation, with no new policy</summary>
        private readonly SecurityService _workflowSecurity;
        /// <summary>Existing singleton services, never circuit adapters</summary>
        private readonly IArchiveService _workflowArchives;
        /// <summary>Existing permissions and ownership service</summary>
        private readonly IPermissionService _workflowPermissions;
        /// <summary>Existing size calculation and filesystem reads</summary>
        private readonly IFileSystemService _workflowFileSystem;
        /// <summary>Settings commit environment shared with the endpoints</summary>
        private readonly IWebHostEnvironment _workflowEnvironment;
        /// <summary>Deferred resolution of the terminal singleton to avoid circular DI dependencies</summary>
        private readonly IServiceProvider _workflowServices;
        /// <summary>Lifetime of the admitted operations, independent of browser revocation</summary>
        private readonly CancellationTokenSource _operationLifetimeCancellation = new CancellationTokenSource();
        /// <summary>Last admitted task and its projection</summary>
        private WorkspaceOperationRuntime _operationRuntime;

        #endregion

        #region Metodi pubblici

        /// <summary>Only the current lease can request cancel of the same operation and revision</summary>
        /// <param name="token">Requester lease</param>
        /// <param name="id">Observed operation</param>
        /// <param name="expectedRevision">Observed revision</param>
        /// <returns>True if the request was admitted</returns>
        internal bool TryCancelWorkspaceOperation(WorkspaceClientToken token, Guid id, long expectedRevision)
        {
            CancellationTokenSource cancellation;
            BiviumWorkspaceSnapshot snapshot;
            Action<BiviumWorkspaceSnapshot>[] subscribers;
            lock (this._lock)
            {
                WorkspaceOperationSnapshot operation = this._operationRuntime?.Snapshot;
                if (this.IsStopped || !this.ValidateMutationLocked(token) || operation == null || operation.Id != id || operation.Revision != expectedRevision || !operation.IsRunning)
                    return false;
                cancellation = this._operationRuntime.Cancellation;
                this._operationRuntime.Snapshot = operation with { Revision = operation.Revision + 1, Phase = WorkspaceOperationPhase.CancellationRequested };
                snapshot = this.CommitWorkflowStateLocked();
                subscribers = this.GetSubscribers();
            }
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The task has already completed its effect and released the resource
            }
            this.NotifySubscribers(subscribers, snapshot);
            return true;
        }

        #endregion

        #region Metodi privati

        /// <summary>Admits exclusively the plan derived from the response validated under lock</summary>
        /// <param name="workflowId">Owning workflow</param>
        /// <param name="plan">Immutable plan</param>
        /// <returns>Runtime with no references to the browser</returns>
        private WorkspaceOperationRuntime AdmitWorkflowOperationLocked(Guid workflowId, WorkspaceOperationPlan plan)
        {
            WorkspaceOperationRuntime operation = new WorkspaceOperationRuntime
            {
                Plan = plan,
                Snapshot = new WorkspaceOperationSnapshot(Guid.NewGuid(), 0, workflowId, plan.Kind, WorkspaceOperationPhase.Running, 0, 0, ""),
                Cancellation = CancellationTokenSource.CreateLinkedTokenSource(this._operationLifetimeCancellation.Token)
            };
            this._operationRuntime = operation;
            if (plan.Kind == WorkspaceWorkflowKind.BatchRename)
                operation.RenameState = plan.RenameItems.Select(item => new WorkspaceRenameItemState(item, WorkspaceRenameItemPhase.Original, item.OriginalPath, "")).ToList();
            return operation;
        }

        /// <summary>Executes only the admitted plan using the existing services and permission checks</summary>
        /// <param name="operation">Runtime admitted exactly once</param>
        private void RunWorkspaceOperation(WorkspaceOperationRuntime operation)
        {
            FileOperationResult result;
            bool cancelled = false;
            try
            {
                operation.Cancellation.Token.ThrowIfCancellationRequested();
                WorkspaceOperationPlan plan = operation.Plan;
                result = plan.Kind switch
                {
                    WorkspaceWorkflowKind.CreateFile => this._workflowFiles.CreateFile(plan.ParentPath, plan.Name),
                    WorkspaceWorkflowKind.CreateDirectory => this._workflowFiles.CreateDirectory(plan.ParentPath, plan.Name),
                    WorkspaceWorkflowKind.RenameEntry => this._workflowFiles.RenameEntry(plan.SourcePath, plan.Name),
                    WorkspaceWorkflowKind.DeleteEntries => this._workflowFiles.DeleteEntriesWithProgress(new List<string>(plan.SourcePaths), (processed, failed) => this.PublishWorkspaceOperationProgress(operation, processed, failed), operation.Cancellation.Token),
                    WorkspaceWorkflowKind.CopyEntries or WorkspaceWorkflowKind.MoveEntries or WorkspaceWorkflowKind.TransferEntries => this.RunWorkspaceTransfer(operation),
                    WorkspaceWorkflowKind.BatchRename => this.RunWorkspaceBatchRename(operation),
                    WorkspaceWorkflowKind.Compress or WorkspaceWorkflowKind.Extract => this.RunWorkspaceArchive(operation),
                    WorkspaceWorkflowKind.Permissions => this.RunWorkspacePermissions(operation),
                    WorkspaceWorkflowKind.EditorExtensions or WorkspaceWorkflowKind.CreationPermissions => this.RunWorkspaceSettings(operation),
                    WorkspaceWorkflowKind.Properties => this.RunWorkspaceProperties(operation),
                    WorkspaceWorkflowKind.TerminalRename or WorkspaceWorkflowKind.TerminalClose => this.RunWorkspaceTerminal(operation),
                    _ => FileOperationResult.Fail("Unsupported admitted operation")
                };
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                result = FileOperationResult.Fail(operation.Plan.Kind == WorkspaceWorkflowKind.DeleteEntries ? "Deletion cancelled. Items already deleted were not restored." : operation.Plan.Kind == WorkspaceWorkflowKind.BatchRename ? "Rename cancelled. Completed renames and temporary names were left unchanged." : operation.Plan.Kind == WorkspaceWorkflowKind.Compress ? "Compression cancelled. Incomplete archive kept." : operation.Plan.Kind == WorkspaceWorkflowKind.Properties ? "Size calculation cancelled." : "Operation cancelled. Completed items were kept.");
            }
            catch (Exception ex)
            {
                result = FileOperationResult.Fail(ex.Message);
                this._logger.LogWarning(ex, "Admitted workspace operation failed");
            }
            try
            {
                Action<BiviumWorkspaceSnapshot>[] subscribers;
                BiviumWorkspaceSnapshot snapshot;
                lock (this._lock)
                {
                    if (this.IsStopped || !ReferenceEquals(this._operationRuntime, operation))
                        return;
                    // Even an exception or cancel during a root preserves the counts already recorded
                    result.FilesProcessed = Math.Max(result.FilesProcessed, operation.FilesProcessed);
                    result.FilesFailed = Math.Max(result.FilesFailed, operation.FilesFailed);
                    if (operation.Plan.Kind == WorkspaceWorkflowKind.BatchRename)
                    {
                        result.FilesProcessed = Math.Max(result.FilesProcessed, operation.RenameState.Count(item => item.Phase == WorkspaceRenameItemPhase.Finalized));
                        result.FilesFailed = Math.Max(result.FilesFailed, operation.RenameState.Count(item => !string.IsNullOrEmpty(item.ErrorMessage)));
                        if (!cancelled && !result.Success)
                            result.ErrorMessage = DescribeBatchRenameOutcome(operation, result);
                    }
                    WorkspaceOperationPhase phase = cancelled ? WorkspaceOperationPhase.Cancelled : result.Success ? WorkspaceOperationPhase.Succeeded : WorkspaceOperationPhase.Failed;
                    operation.Snapshot = operation.Snapshot with { Revision = operation.Snapshot.Revision + 1, Phase = phase, FilesProcessed = result.FilesProcessed, FilesFailed = result.FilesFailed, ErrorMessage = result.ErrorMessage, ProgressCurrent = operation.ProgressCurrent, ProgressTotal = operation.ProgressTotal, Stage = operation.Stage };
                    WorkspaceWorkflowSnapshot workflow = this._workflowRuntime.Current;
                    if (workflow?.OperationId == operation.Snapshot.Id && workflow.Phase != WorkspaceWorkflowPhase.Dismissed)
                        this._workflowRuntime.Current = workflow with { Revision = workflow.Revision + 1, Phase = cancelled ? WorkspaceWorkflowPhase.Cancelled : result.Success ? WorkspaceWorkflowPhase.Succeeded : WorkspaceWorkflowPhase.Failed, QuestionId = !cancelled && !result.Success ? Guid.NewGuid() : workflow.QuestionId, ErrorMessage = result.ErrorMessage, Draft = operation.ResultDraft ?? workflow.Draft };
                    if (workflow?.OperationId == operation.Snapshot.Id && workflow.Phase != WorkspaceWorkflowPhase.Dismissed && operation.Plan.Kind == WorkspaceWorkflowKind.Properties)
                        this._workflowRuntime.Current = this._workflowRuntime.Current with { Phase = WorkspaceWorkflowPhase.AwaitingInput, QuestionId = Guid.NewGuid() };
                    this.ReconcileWorkspaceOperationLocked(operation, result);
                    this.ActivateNextTerminalClipboardLocked();
                    snapshot = this.CommitWorkflowStateLocked();
                    subscribers = this.GetSubscribers();
                }
                this.NotifySubscribers(subscribers, snapshot);
            }
            finally
            {
                operation.Cancellation.Dispose();
            }
        }

        /// <summary>Publishes progress of the admitted task without depending on the browser lease</summary>
        /// <param name="operation">Runtime that owns the counts</param>
        /// <param name="processed">Completed roots</param>
        /// <param name="failed">Failed roots</param>
        private void PublishWorkspaceOperationProgress(WorkspaceOperationRuntime operation, int processed, int failed)
        {
            Action<BiviumWorkspaceSnapshot>[] subscribers;
            BiviumWorkspaceSnapshot snapshot;
            lock (this._lock)
            {
                if (this.IsStopped || !ReferenceEquals(this._operationRuntime, operation) || !operation.Snapshot.IsRunning)
                    return;
                operation.FilesProcessed = processed;
                operation.FilesFailed = failed;
                if (!this.TryPublishWorkspaceProgressLocked(operation, out snapshot))
                    return;
                subscribers = this.GetSubscribers();
            }
            this.NotifySubscribers(subscribers, snapshot);
        }

        /// <summary>Reads the two-pass state only for the current lease and the owning session</summary>
        /// <param name="token">Adapter lease</param>
        /// <param name="sessionId">Mounted renamer session</param>
        /// <returns>Immutable state, never the plan of another workflow</returns>
        internal ImmutableArray<WorkspaceRenameItemState> GetBatchRenameState(WorkspaceClientToken token, Guid sessionId)
        {
            lock (this._lock)
                return !this.IsStopped && this.ValidateLeaseLocked(token) && this._operationRuntime?.Plan.RenamerSessionId == sessionId ? this._operationRuntime.RenameState.ToImmutableArray() : ImmutableArray<WorkspaceRenameItemState>.Empty;
        }

        /// <summary>Executes the captured transfer plan through the existing service, with no additional threads</summary>
        /// <param name="operation">Task already admitted by the workspace runner</param>
        /// <returns>Root counts, separate from step progress</returns>
        private FileOperationResult RunWorkspaceTransfer(WorkspaceOperationRuntime operation)
        {
            WorkspaceOperationPlan plan = operation.Plan;
            FileOperationResult aggregate = FileOperationResult.Ok(0);
            List<string> overwrite = new List<string>(plan.OverwritePaths);
            for (int i = 0; i < plan.Transfers.Length; i++)
            {
                operation.Cancellation.Token.ThrowIfCancellationRequested();
                WorkspaceTransferEntry entry = plan.Transfers[i];
                this.PublishWorkspaceExecutionState(operation, i, plan.Transfers.Length, entry.Mode.ToString());
                List<string> source = new List<string> { entry.SourcePath };
                FileOperationResult result = plan.Kind switch
                {
                    WorkspaceWorkflowKind.CopyEntries => this._workflowFiles.CopyEntriesWithProgress(source, plan.ParentPath, (current, total, name) => this.PublishWorkspaceExecutionState(operation, current, total, "Copy contents"), overwrite, operation.Cancellation.Token),
                    WorkspaceWorkflowKind.MoveEntries => this._workflowFiles.MoveEntriesWithProgress(source, plan.ParentPath, (current, total, name) => { }, overwrite, operation.Cancellation.Token),
                    _ => this._workflowFiles.TransferEntriesWithProgress(new List<FileTransferEntry> { new FileTransferEntry(entry.SourcePath, entry.Mode) }, plan.ParentPath, (current, total, name) => { }, null, overwrite, operation.Cancellation.Token)
                };
                aggregate.FilesProcessed += result.FilesProcessed;
                aggregate.FilesFailed += result.FilesFailed;
                aggregate.Success &= result.Success;
                if (!result.Success)
                    aggregate.ErrorMessage = result.ErrorMessage;
                this.PublishWorkspaceOperationProgress(operation, aggregate.FilesProcessed, aggregate.FilesFailed);
                this.PublishWorkspaceExecutionState(operation, i + 1, plan.Transfers.Length, entry.Mode.ToString());
            }
            return aggregate;
        }

        /// <summary>Preserves the existing local two-step flow and rollback, now in the server-owned task</summary>
        /// <param name="operation">Runtime with immutable temporary names</param>
        /// <returns>Outcome of the finalized names, with failed rollbacks made explicit</returns>
        private FileOperationResult RunWorkspaceBatchRename(WorkspaceOperationRuntime operation)
        {
            ImmutableArray<WorkspaceRenameItem> items = operation.Plan.RenameItems;
            for (int i = 0; i < items.Length; i++)
            {
                operation.Cancellation.Token.ThrowIfCancellationRequested();
                WorkspaceRenameItem item = items[i];
                this.PublishWorkspaceExecutionState(operation, i, items.Length, "Temporary names");
                FileOperationResult result = this._workflowFiles.RenameEntry(item.OriginalPath, Path.GetFileName(item.TemporaryPath));
                if (result.Success)
                {
                    this.SetWorkspaceRenameItemState(operation, i, WorkspaceRenameItemPhase.Temporary, item.TemporaryPath, "");
                    this.PublishWorkspaceExecutionState(operation, i + 1, items.Length, "Temporary names");
                    continue;
                }
                string error = "Failed to rename '" + item.OriginalName + "': " + result.ErrorMessage;
                this.SetWorkspaceRenameItemState(operation, i, WorkspaceRenameItemPhase.Original, item.OriginalPath, result.ErrorMessage);
                for (int r = 0; r < i; r++)
                {
                    operation.Cancellation.Token.ThrowIfCancellationRequested();
                    this.PublishWorkspaceExecutionState(operation, r, i, "Rollback");
                    FileOperationResult rollback = this._workflowFiles.RenameEntry(items[r].TemporaryPath, items[r].OriginalName);
                    this.SetWorkspaceRenameItemState(operation, r, rollback.Success ? WorkspaceRenameItemPhase.RolledBack : WorkspaceRenameItemPhase.RollbackFailed, rollback.Success ? items[r].OriginalPath : items[r].TemporaryPath, rollback.ErrorMessage);
                    this.PublishWorkspaceExecutionState(operation, r + 1, i, "Rollback");
                    if (!rollback.Success)
                        error = "Rollback failed for '" + items[r].OriginalName + "': " + rollback.ErrorMessage;
                }
                FileOperationResult failure = FileOperationResult.Fail(error);
                failure.FilesFailed = 1;
                return failure;
            }
            FileOperationResult aggregate = FileOperationResult.Ok(0);
            for (int i = 0; i < items.Length; i++)
            {
                operation.Cancellation.Token.ThrowIfCancellationRequested();
                WorkspaceRenameItem item = items[i];
                this.PublishWorkspaceExecutionState(operation, i, items.Length, "Final names");
                FileOperationResult result = this._workflowFiles.RenameEntry(item.TemporaryPath, item.NewName);
                if (result.Success)
                {
                    aggregate.FilesProcessed++;
                    this.SetWorkspaceRenameItemState(operation, i, WorkspaceRenameItemPhase.Finalized, Path.Combine(Path.GetDirectoryName(item.OriginalPath), item.NewName), "");
                }
                else
                {
                    aggregate.Success = false;
                    aggregate.FilesFailed++;
                    aggregate.ErrorMessage = "Failed to finalize '" + item.NewName + "': " + result.ErrorMessage;
                    // Records the temporary name before the possible cancel: B also sees the actual location
                    this.SetWorkspaceRenameItemState(operation, i, WorkspaceRenameItemPhase.Temporary, item.TemporaryPath, result.ErrorMessage);
                    operation.Cancellation.Token.ThrowIfCancellationRequested();
                    FileOperationResult rollback = this._workflowFiles.RenameEntry(item.TemporaryPath, item.OriginalName);
                    this.SetWorkspaceRenameItemState(operation, i, rollback.Success ? WorkspaceRenameItemPhase.RolledBack : WorkspaceRenameItemPhase.RollbackFailed, rollback.Success ? item.OriginalPath : item.TemporaryPath, rollback.Success ? result.ErrorMessage : result.ErrorMessage + "\nRollback: " + rollback.ErrorMessage);
                    if (!rollback.Success)
                        aggregate.ErrorMessage = "Rollback failed for '" + item.OriginalName + "': " + rollback.ErrorMessage;
                }
                this.PublishWorkspaceOperationProgress(operation, aggregate.FilesProcessed, aggregate.FilesFailed);
                this.PublishWorkspaceExecutionState(operation, i + 1, items.Length, "Final names");
            }
            return aggregate;
        }

        /// <summary>Summarizes the outcome of the failed batch: renamed, failed and restored to the original name</summary>
        /// <param name="operation">Runtime with the actual state of the rows</param>
        /// <param name="result">Outcome with the already consolidated counts</param>
        /// <returns>Text shown by the dialog and the status bar</returns>
        private static string DescribeBatchRenameOutcome(WorkspaceOperationRuntime operation, FileOperationResult result)
        {
            int rolledBack = operation.RenameState.Count(item => item.Phase == WorkspaceRenameItemPhase.RolledBack);
            int rollbackFailed = operation.RenameState.Count(item => item.Phase == WorkspaceRenameItemPhase.RollbackFailed);
            string outcome = result.FilesProcessed + " renamed, " + result.FilesFailed + " failed, " + rolledBack + " rolled back to original names";
            if (rollbackFailed > 0)
                outcome = outcome + ", " + rollbackFailed + " left with temporary names";
            return string.IsNullOrEmpty(result.ErrorMessage) ? outcome : outcome + ". " + result.ErrorMessage;
        }

        /// <summary>Records the actual location after each filesystem effect, not after a render</summary>
        /// <param name="operation">Owning runtime</param>
        /// <param name="index">Captured row</param>
        /// <param name="phase">Step outcome</param>
        /// <param name="path">Actual location</param>
        /// <param name="error">Step error</param>
        private void SetWorkspaceRenameItemState(WorkspaceOperationRuntime operation, int index, WorkspaceRenameItemPhase phase, string path, string error)
        {
            lock (this._lock)
                operation.RenameState[index] = new WorkspaceRenameItemState(operation.Plan.RenameItems[index], phase, path, error);
        }

        /// <summary>Lightweight step progress; does not transmit the plan or the temporary names</summary>
        /// <param name="operation">Owning runtime</param>
        /// <param name="current">Position within the step</param>
        /// <param name="total">Step total</param>
        /// <param name="stage">Current step</param>
        private void PublishWorkspaceExecutionState(WorkspaceOperationRuntime operation, int current, int total, string stage)
        {
            Action<BiviumWorkspaceSnapshot>[] subscribers;
            BiviumWorkspaceSnapshot snapshot;
            lock (this._lock)
            {
                if (this.IsStopped || !ReferenceEquals(this._operationRuntime, operation))
                    return;
                operation.ProgressCurrent = current;
                operation.ProgressTotal = total;
                operation.Stage = stage;
                if (!this.TryPublishWorkspaceProgressLocked(operation, out snapshot))
                    return;
                subscribers = this.GetSubscribers();
            }
            this.NotifySubscribers(subscribers, snapshot);
        }

        /// <summary>Coalesces publications to 100 ms; the final result always publishes all counts</summary>
        /// <param name="operation">Progress runtime</param>
        /// <param name="snapshot">Published projection or null</param>
        /// <returns>True when the published revision was advanced</returns>
        private bool TryPublishWorkspaceProgressLocked(WorkspaceOperationRuntime operation, out BiviumWorkspaceSnapshot snapshot)
        {
            DateTime now = DateTime.UtcNow;
            snapshot = null;
            if ((now - operation.LastProgressPublicationUtc).TotalMilliseconds < 100)
                return false;
            operation.LastProgressPublicationUtc = now;
            operation.Snapshot = operation.Snapshot with { Revision = operation.Snapshot.Revision + 1, FilesProcessed = operation.FilesProcessed, FilesFailed = operation.FilesFailed, ProgressCurrent = operation.ProgressCurrent, ProgressTotal = operation.ProgressTotal, Stage = operation.Stage };
            snapshot = this.CommitWorkflowStateLocked();
            return true;
        }

        /// <summary>Reconciles results into the workspace without callbacks retained by the circuit</summary>
        /// <param name="operation">Completed operation</param>
        /// <param name="result">Actual outcome</param>
        private void ReconcileWorkspaceOperationLocked(WorkspaceOperationRuntime operation, FileOperationResult result)
        {
            WorkspaceOperationPlan plan = operation.Plan;
            DesktopSessionsSnapshot desktop = this._snapshot.Desktop;
            if (result.Success && plan.Kind == WorkspaceWorkflowKind.TerminalClose && JsonSerializer.Deserialize<WorkspaceTerminalContext>(plan.FormContext).CloseWindow)
            {
                FloatingWindowsSnapshot windows = this._snapshot.FloatingWindows;
                this.CommitDesktopLocked(new FloatingWindowsSnapshot(windows.Terminal with { Visible = false, Minimized = false }, windows.Editor, windows.Renamer));
            }
            if (result.Success && plan.FromClipboard && plan.Kind == WorkspaceWorkflowKind.MoveEntries && plan.Transfers.Length > 0 && desktop.ClipboardIsCut && plan.SourcePaths.SequenceEqual(desktop.ClipboardPaths))
            {
                desktop = new DesktopSessionsSnapshot(desktop.EditorId, desktop.EditorTitle, desktop.EditorDirty, desktop.RenamerId);
                this._snapshot = new BiviumWorkspaceSnapshot(this._snapshot.Revision, this._snapshot.Panels, this._snapshot.FloatingWindows, this._snapshot.ActiveClientLease, desktop, this._snapshot.Handoff, this._snapshot.Workflow, this._snapshot.Operation, this._snapshot.Upload);
            }
            if (plan.Kind != WorkspaceWorkflowKind.BatchRename || this._desktopRuntime.Renamer?.Id != plan.RenamerSessionId)
                return;
            if (result.Success)
            {
                this._desktopRuntime.Renamer = null;
                FloatingWindowsSnapshot windows = this._snapshot.FloatingWindows;
                this.CommitDesktopLocked(new FloatingWindowsSnapshot(windows.Terminal, windows.Editor, windows.Renamer with { Visible = false, Minimized = false }));
            }
            else
            {
                RenamerSessionSnapshot session = this._desktopRuntime.Renamer;
                RenamerDraft draft = JsonSerializer.Deserialize<RenamerDraft>(session.Draft);
                draft.DidRename |= result.FilesProcessed > 0;
                draft.StatusText = operation.Snapshot.ErrorMessage;
                this._desktopRuntime.Renamer = session with { Revision = session.Revision + 1, Draft = JsonSerializer.Serialize(draft) };
            }
        }

        #endregion
    }
}
