using Bivium.Models;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Bivium.Services
{
    public sealed partial class BiviumWorkspaceService
    {
        #region Costanti

        /// <summary>Response to the editor close: saves, then closes</summary>
        internal const string EDITOR_CLOSE_SAVE = "save";

        /// <summary>Response to the editor close: discards the changes and closes</summary>
        internal const string EDITOR_CLOSE_DISCARD = "discard";

        #endregion

        #region Variabili di classe

        /// <summary>Form and question runtime with no circuit ownership</summary>
        private readonly WorkspaceWorkflowRuntime _workflowRuntime = new WorkspaceWorkflowRuntime();

        #endregion

        #region Metodi pubblici

        /// <summary>Reads the workflow only for the authoritative lease, also during the drain</summary>
        /// <param name="token">Reader lease</param>
        /// <returns>Immutable workflow or null</returns>
        internal WorkspaceWorkflowSnapshot GetWorkflow(WorkspaceClientToken token)
        {
            lock (this._lock)
                return !this.IsStopped && this.ValidateLeaseLocked(token) ? this._workflowRuntime.Current : null;
        }

        /// <summary>Opens an Input question with already captured paths and the existing path validation</summary>
        /// <param name="token">Lease that invokes the command</param>
        /// <param name="kind">Closed command</param>
        /// <param name="parameters">Arguments captured by the invocation</param>
        /// <param name="draft">Initial value</param>
        /// <returns>Admitted workflow</returns>
        internal WorkspaceWorkflowSnapshot BeginInputWorkflow(WorkspaceClientToken token, WorkspaceWorkflowKind kind, WorkspaceWorkflowInvocation parameters, string draft)
        {
            if (parameters == null || kind is not (WorkspaceWorkflowKind.CreateFile or WorkspaceWorkflowKind.CreateDirectory or WorkspaceWorkflowKind.RenameEntry) || !this._workflowSecurity.IsPathSafe(parameters.ParentPath) || (kind == WorkspaceWorkflowKind.RenameEntry && !this._workflowSecurity.IsPathSafe(parameters.SourcePath)))
                throw new ArgumentException("Invalid workflow invocation");
            BiviumWorkspaceSnapshot snapshot;
            Action<BiviumWorkspaceSnapshot>[] subscribers;
            WorkspaceWorkflowSnapshot workflow;
            lock (this._lock)
            {
                this.ThrowIfStopped();
                if (!this.ValidateMutationLocked(token))
                    throw new UnauthorizedAccessException("This browser no longer controls the workspace");
                if (this._uploadRuntime?.Snapshot.Visible == true || this._workflowRuntime.Current?.IsActive == true || this._operationRuntime?.Snapshot.IsRunning == true)
                    throw new InvalidOperationException("A workspace workflow is already active");
                workflow = new WorkspaceWorkflowSnapshot(Guid.NewGuid(), 0, kind, parameters, WorkspaceWorkflowPhase.AwaitingInput, draft ?? "", Guid.NewGuid(), Guid.Empty, "");
                this._workflowRuntime.Current = workflow;
                this._workflowRuntime.Responses.Clear();
                snapshot = this.CommitWorkflowStateLocked();
                subscribers = this.GetSubscribers();
            }
            this.NotifySubscribers(subscribers, snapshot);
            return workflow;
        }

        /// <summary>Opens the delete confirmation on the immutable selection, without touching the filesystem</summary>
        /// <param name="token">Lease that invokes the command</param>
        /// <param name="parameters">Captured selection and presentation</param>
        /// <returns>Admitted server-owned question</returns>
        internal WorkspaceWorkflowSnapshot BeginDeleteWorkflow(WorkspaceClientToken token, WorkspaceWorkflowInvocation parameters)
        {
            BiviumWorkspaceSnapshot snapshot;
            Action<BiviumWorkspaceSnapshot>[] subscribers;
            WorkspaceWorkflowSnapshot workflow;
            lock (this._lock)
            {
                this.ThrowIfStopped();
                if (!this.ValidateMutationLocked(token))
                    throw new UnauthorizedAccessException("This browser no longer controls the workspace");
                if (this._uploadRuntime?.Snapshot.Visible == true || this._workflowRuntime.Current?.IsActive == true || this._operationRuntime?.Snapshot.IsRunning == true)
                    throw new InvalidOperationException("A workspace workflow is already active");
                if (parameters == null || parameters.SourcePaths.IsDefaultOrEmpty)
                    throw new ArgumentException("Invalid workflow invocation");
                foreach (string path in parameters.SourcePaths)
                    if (!this._workflowSecurity.IsPathSafe(path))
                        throw new ArgumentException("Invalid workflow invocation");
                workflow = new WorkspaceWorkflowSnapshot(Guid.NewGuid(), 0, WorkspaceWorkflowKind.DeleteEntries, parameters, WorkspaceWorkflowPhase.AwaitingConfirmation, "", Guid.NewGuid(), Guid.Empty, "");
                this._workflowRuntime.Current = workflow;
                this._workflowRuntime.Responses.Clear();
                snapshot = this.CommitWorkflowStateLocked();
                subscribers = this.GetSubscribers();
            }
            this.NotifySubscribers(subscribers, snapshot);
            return workflow;
        }

        /// <summary>Captures the editor alert, the editor close with changes or the reset intent, with no reads or effects on hydration</summary>
        /// <param name="token">Lease that opens the question</param>
        /// <param name="kind">Only editor alert, editor close or workspace reset</param>
        /// <param name="title">Existing immutable title</param>
        /// <param name="message">Already materialized immutable text</param>
        /// <returns>Transferable question with a stable identity</returns>
        internal WorkspaceWorkflowSnapshot BeginConfirmationWorkflow(WorkspaceClientToken token, WorkspaceWorkflowKind kind, string title, string message)
        {
            if (kind is not (WorkspaceWorkflowKind.EditorAlert or WorkspaceWorkflowKind.ResetWorkspace or WorkspaceWorkflowKind.EditorClose))
                throw new ArgumentException("Unsupported confirmation workflow");
            BiviumWorkspaceSnapshot snapshot;
            Action<BiviumWorkspaceSnapshot>[] subscribers;
            WorkspaceWorkflowSnapshot workflow;
            lock (this._lock)
            {
                this.RequireDesktopLeaseLocked(token);
                if (this._uploadRuntime?.Snapshot.Visible == true || this._workflowRuntime.Current?.IsActive == true || this._operationRuntime?.Snapshot.IsRunning == true)
                    throw new InvalidOperationException("A workspace workflow is already active");
                WorkspaceWorkflowInvocation invocation = new WorkspaceWorkflowInvocation("", "", -1, title, message, -1);
                workflow = new WorkspaceWorkflowSnapshot(Guid.NewGuid(), 0, kind, invocation, WorkspaceWorkflowPhase.AwaitingConfirmation, "", Guid.NewGuid(), Guid.Empty, "");
                this._workflowRuntime.Current = workflow;
                this._workflowRuntime.Responses.Clear();
                snapshot = this.CommitWorkflowStateLocked();
                subscribers = this.GetSubscribers();
            }
            this.NotifySubscribers(subscribers, snapshot);
            return workflow;
        }

        /// <summary>Publishes the draft only; neither answers the question nor executes commands</summary>
        /// <param name="token">Publisher lease</param>
        /// <param name="id">Mounted workflow</param>
        /// <param name="expectedRevision">Revision of the previous draft</param>
        /// <param name="draft">Current value</param>
        /// <param name="workflow">Workflow after the attempt</param>
        /// <returns>True only for the acknowledged checkpoint</returns>
        internal bool TryUpdateWorkflowDraft(WorkspaceClientToken token, Guid id, long expectedRevision, string draft, out WorkspaceWorkflowSnapshot workflow)
        {
            BiviumWorkspaceSnapshot snapshot;
            Action<BiviumWorkspaceSnapshot>[] subscribers = Array.Empty<Action<BiviumWorkspaceSnapshot>>();
            lock (this._lock)
            {
                workflow = this._workflowRuntime.Current;
                if (this.IsStopped || !this.ValidateLeaseLocked(token) || workflow == null || workflow.Id != id || workflow.Revision != expectedRevision || (workflow.Phase != WorkspaceWorkflowPhase.AwaitingInput && !(workflow.Phase == WorkspaceWorkflowPhase.Failed && IsEditableFormKind(workflow.Kind))))
                    return false;
                if (IsFormKind(workflow.Kind))
                {
                    if (workflow.Kind is WorkspaceWorkflowKind.Properties or WorkspaceWorkflowKind.About or WorkspaceWorkflowKind.Extract or WorkspaceWorkflowKind.TerminalClose or WorkspaceWorkflowKind.TerminalClipboard)
                        return workflow.Draft == draft;
                    draft = NormalizeFormDraft(workflow.Kind, draft);
                }
                if (workflow.Draft != (draft ?? ""))
                {
                    // The inline name validation error describes the rejected value, not the modified one
                    workflow = workflow with { Revision = workflow.Revision + 1, Draft = draft ?? "", ErrorMessage = IsFormKind(workflow.Kind) ? workflow.ErrorMessage : "" };
                    this._workflowRuntime.Current = workflow;
                    this.CommitWorkflowStateLocked();
                    subscribers = this.GetSubscribers();
                }
                snapshot = this._snapshot;
            }
            this.NotifySubscribers(subscribers, snapshot);
            return true;
        }

        /// <summary>Consumes the response and admits a closed plan atomically with the lease</summary>
        /// <param name="token">Responding lease</param>
        /// <param name="response">Typed response with a consume-once identity</param>
        /// <returns>Outcome; a duplicate response does not start the task again</returns>
        internal WorkspaceWorkflowResponseResult RespondToWorkflow(WorkspaceClientToken token, WorkspaceWorkflowResponse response)
        {
            BiviumWorkspaceSnapshot snapshot;
            Action<BiviumWorkspaceSnapshot>[] subscribers;
            WorkspaceOperationRuntime admitted = null;
            Action completeReset = null;
            WorkspaceUploadRuntime resetUpload = null;
            TerminalRuntimeService resetTerminal = null;
            // Resolution outside the lock: the terminal singleton depends on the workspace
            if (response?.Cancelled == false)
            {
                WorkspaceWorkflowSnapshot observed = this.GetWorkflow(token);
                if (observed?.Id == response.WorkflowId && observed.Kind == WorkspaceWorkflowKind.ResetWorkspace)
                    resetTerminal = this._workflowServices.GetRequiredService<TerminalRuntimeService>();
            }
            lock (this._lock)
            {
                if (this.IsStopped || !this.ValidateMutationLocked(token))
                    return WorkspaceWorkflowResponseResult.Denied;
                if (response == null || response.ResponseId == Guid.Empty)
                    return WorkspaceWorkflowResponseResult.Stale;
                if (this._workflowRuntime.Responses.TryGetValue(response.ResponseId, out WorkspaceWorkflowResponse consumed))
                    return consumed == response ? WorkspaceWorkflowResponseResult.Duplicate : WorkspaceWorkflowResponseResult.Denied;
                WorkspaceWorkflowSnapshot workflow = this._workflowRuntime.Current;
                if (workflow == null || workflow.Id != response.WorkflowId || workflow.Revision != response.Revision || workflow.QuestionId != response.QuestionId || (workflow.Phase != WorkspaceWorkflowPhase.AwaitingInput && workflow.Phase != WorkspaceWorkflowPhase.AwaitingConfirmation && workflow.Phase != WorkspaceWorkflowPhase.AwaitingOverwrite && workflow.Phase != WorkspaceWorkflowPhase.Failed && !(workflow.IsActive && workflow.Phase == WorkspaceWorkflowPhase.Cancelled) && !(workflow.Kind == WorkspaceWorkflowKind.Properties && workflow.Phase == WorkspaceWorkflowPhase.Running && response.Cancelled)))
                    return WorkspaceWorkflowResponseResult.Stale;
                if (workflow.Phase != WorkspaceWorkflowPhase.AwaitingOverwrite && response.Overwrite != null)
                    return WorkspaceWorkflowResponseResult.Stale;
                if (workflow.Kind == WorkspaceWorkflowKind.Properties && workflow.Phase == WorkspaceWorkflowPhase.Running && response.Cancelled)
                {
                    // Closing the form does not cancel the already admitted calculation
                    this._workflowRuntime.Current = workflow with { Revision = workflow.Revision + 1, Phase = WorkspaceWorkflowPhase.Dismissed };
                }
                else if ((workflow.Phase == WorkspaceWorkflowPhase.Failed && (!IsEditableFormKind(workflow.Kind) || response.Cancelled)) || workflow.Phase == WorkspaceWorkflowPhase.Cancelled)
                {
                    if (!response.Cancelled)
                        return WorkspaceWorkflowResponseResult.Stale;
                    this._workflowRuntime.Current = workflow with { Revision = workflow.Revision + 1, Phase = WorkspaceWorkflowPhase.Dismissed };
                }
                else if (workflow.Phase == WorkspaceWorkflowPhase.AwaitingOverwrite)
                {
                    if (response.Cancelled || response.Overwrite == null || !Enum.IsDefined(response.Overwrite.Value) || !string.IsNullOrEmpty(response.Value))
                        return WorkspaceWorkflowResponseResult.Stale;
                    WorkspaceOverwriteQuestion question = workflow.Conflicts[workflow.ConflictIndex];
                    ImmutableArray<string> overwrite = workflow.OverwritePaths;
                    ImmutableArray<string> skipped = workflow.SkippedPaths;
                    int next = workflow.ConflictIndex + 1;
                    if (response.Overwrite == OverwriteChoice.No)
                        skipped = skipped.Add(question.SourcePath);
                    else if (response.Overwrite == OverwriteChoice.Yes)
                        overwrite = overwrite.Add(question.SourcePath);
                    else
                    {
                        for (int i = workflow.ConflictIndex; i < workflow.Conflicts.Length; i++)
                            overwrite = overwrite.Add(workflow.Conflicts[i].SourcePath);
                        next = workflow.Conflicts.Length;
                    }
                    workflow = workflow with { Revision = workflow.Revision + 1, ConflictIndex = next, OverwritePaths = overwrite, SkippedPaths = skipped, QuestionId = Guid.NewGuid() };
                    this._workflowRuntime.Current = workflow;
                    if (next == workflow.Conflicts.Length)
                    {
                        admitted = this.AdmitWorkflowOperationLocked(workflow.Id, this.CreateTransferPlan(workflow));
                        this._workflowRuntime.Current = workflow with { Phase = WorkspaceWorkflowPhase.Running, OperationId = admitted.Snapshot.Id };
                    }
                }
                else if (workflow.Phase == WorkspaceWorkflowPhase.AwaitingConfirmation)
                {
                    if (workflow.Kind is not (WorkspaceWorkflowKind.DeleteEntries or WorkspaceWorkflowKind.BatchRename or WorkspaceWorkflowKind.EditorAlert or WorkspaceWorkflowKind.ResetWorkspace or WorkspaceWorkflowKind.EditorClose))
                        return WorkspaceWorkflowResponseResult.Stale;
                    // Only the editor close carries a typed choice: save or discard
                    if (workflow.Kind == WorkspaceWorkflowKind.EditorClose ? !response.Cancelled && response.Value is not (EDITOR_CLOSE_SAVE or EDITOR_CLOSE_DISCARD) : !string.IsNullOrEmpty(response.Value))
                        return WorkspaceWorkflowResponseResult.Stale;
                    if (workflow.Kind is WorkspaceWorkflowKind.EditorAlert or WorkspaceWorkflowKind.EditorClose)
                    {
                        // Consume-once: save or discard are executed by the adapter that responded, never by the next browser
                        this._workflowRuntime.Current = workflow with { Revision = workflow.Revision + 1, Phase = WorkspaceWorkflowPhase.Dismissed };
                    }
                    else if (response.Cancelled)
                    {
                        this._workflowRuntime.Current = workflow with { Revision = workflow.Revision + 1, Phase = workflow.Kind == WorkspaceWorkflowKind.BatchRename ? WorkspaceWorkflowPhase.Dismissed : WorkspaceWorkflowPhase.Cancelled };
                    }
                    else if (workflow.Kind == WorkspaceWorkflowKind.ResetWorkspace)
                    {
                        if (this._operationRuntime?.Snapshot.IsRunning == true || resetTerminal == null)
                            return WorkspaceWorkflowResponseResult.Stale;
                        // Consume-once, PTY registry and desktop are linearized with the current lease.
                        // No wait, disposal or callback is executed under the workspace lock.
                        completeReset = resetTerminal.DetachAllSessionsForWorkspaceReset();
                        this.ResetWorkspaceLocked(out resetUpload);
                    }
                    else
                    {
                        WorkspaceWorkflowInvocation invocation = workflow.InvocationParameters;
                        WorkspaceOperationPlan plan = new WorkspaceOperationPlan(workflow.Kind, "", "", "", invocation.SourcePaths, RenamerSessionId: invocation.RenamerSessionId, RenameItems: invocation.RenameItems);
                        admitted = this.AdmitWorkflowOperationLocked(workflow.Id, plan);
                        this._workflowRuntime.Current = workflow with { Revision = workflow.Revision + 1, Phase = WorkspaceWorkflowPhase.Running, OperationId = admitted.Snapshot.Id };
                    }
                }
                else if (IsFormKind(workflow.Kind))
                {
                    if (response.Value != workflow.Draft)
                        return WorkspaceWorkflowResponseResult.Stale;
                    if (response.Cancelled || workflow.Kind == WorkspaceWorkflowKind.About || (workflow.Kind == WorkspaceWorkflowKind.TerminalRename && string.IsNullOrWhiteSpace(workflow.Draft)))
                    {
                        this._workflowRuntime.Current = workflow with { Revision = workflow.Revision + 1, Phase = WorkspaceWorkflowPhase.Dismissed };
                    }
                    else if (workflow.Kind == WorkspaceWorkflowKind.Authentication)
                    {
                        // Auth mutations stay on the endpoints with the existing authority
                        return WorkspaceWorkflowResponseResult.Stale;
                    }
                    else if (workflow.Kind == WorkspaceWorkflowKind.TerminalClipboard)
                    {
                        // Consume-once before the OS gesture; no execution on the next browser
                        this._workflowRuntime.Current = workflow with { Revision = workflow.Revision + 1, Phase = WorkspaceWorkflowPhase.Succeeded };
                    }
                    else
                    {
                        try
                        {
                            admitted = this.AdmitWorkflowOperationLocked(workflow.Id, this.CreateFormPlan(workflow));
                            this._workflowRuntime.Current = workflow with { Revision = workflow.Revision + 1, Phase = WorkspaceWorkflowPhase.Running, OperationId = admitted.Snapshot.Id, QuestionId = workflow.Kind == WorkspaceWorkflowKind.Properties ? Guid.NewGuid() : workflow.QuestionId, ErrorMessage = "" };
                        }
                        catch (ArgumentException ex)
                        {
                            this._workflowRuntime.Current = workflow with { Revision = workflow.Revision + 1, Phase = WorkspaceWorkflowPhase.Failed, QuestionId = Guid.NewGuid(), ErrorMessage = ex.Message };
                        }
                    }
                }
                else if (!response.Cancelled && !string.IsNullOrEmpty(response.Value))
                {
                    // Same checks as the existing services; OS permissions remain verified by the real I/O
                    FileOperationResult validation = this._workflowFiles.ValidateNameOperation(workflow.Kind, workflow.InvocationParameters.SourcePath, workflow.InvocationParameters.ParentPath, response.Value);
                    if (!validation.Success)
                    {
                        // The invalid name stays on the same question: the field keeps its text and focus and shows the inline error
                        this._workflowRuntime.Current = workflow with { Revision = workflow.Revision + 1, Draft = response.Value, ErrorMessage = validation.ErrorMessage };
                    }
                    else
                    {
                        WorkspaceOperationPlan plan = new WorkspaceOperationPlan(workflow.Kind, workflow.InvocationParameters.SourcePath, workflow.InvocationParameters.ParentPath, response.Value);
                        admitted = this.AdmitWorkflowOperationLocked(workflow.Id, plan);
                        this._workflowRuntime.Current = workflow with { Revision = workflow.Revision + 1, Phase = WorkspaceWorkflowPhase.Running, Draft = response.Value, OperationId = admitted.Snapshot.Id, ErrorMessage = "" };
                    }
                }
                else
                {
                    // The inline error belonged to the cancelled question: it must not survive in the status bar
                    this._workflowRuntime.Current = workflow with { Revision = workflow.Revision + 1, Phase = WorkspaceWorkflowPhase.Cancelled, ErrorMessage = "" };
                }
                this._workflowRuntime.Responses.Add(response.ResponseId, response);
                this.ActivateNextTerminalClipboardLocked();
                snapshot = this.CommitWorkflowStateLocked();
                subscribers = this.GetSubscribers();
                // The task may start, but cannot publish before this commit releases the lock
                if (admitted != null)
                    admitted.Work = Task.Run(() => this.RunWorkspaceOperation(admitted));
            }
            this.NotifySubscribers(subscribers, snapshot);
            try { completeReset?.Invoke(); }
            finally
            {
                if (resetUpload != null)
                    this.CleanupUploadAsync(resetUpload).GetAwaiter().GetResult();
            }
            return WorkspaceWorkflowResponseResult.Accepted;
        }

        #endregion

        #region Metodi privati

        /// <summary>Captures the authoritative clipboard and invokes the existing transfer path</summary>
        /// <param name="token">Invocation lease</param>
        /// <param name="destinationDir">Captured directory</param>
        /// <param name="panelIndex">Destination panel</param>
        /// <returns>Workflow or already admitted operation with no conflicts</returns>
        internal WorkspaceWorkflowSnapshot BeginPasteWorkflow(WorkspaceClientToken token, string destinationDir, int panelIndex)
        {
            BiviumWorkspaceSnapshot snapshot = this.GetSnapshot();
            List<FileTransferEntry> entries = new List<FileTransferEntry>();
            foreach (string path in snapshot.Desktop.ClipboardPaths)
                entries.Add(new FileTransferEntry(path, snapshot.Desktop.ClipboardIsCut ? FileTransferMode.Move : FileTransferMode.Copy));
            return this.BeginTransferWorkflow(token, entries, destinationDir, panelIndex, true);
        }

        /// <summary>Captures sources, mode and conflicts once; hydration does not invoke this method</summary>
        /// <param name="token">Invocation lease</param>
        /// <param name="entries">Server-side entries, copied into immutable records</param>
        /// <param name="destinationDir">Captured destination</param>
        /// <param name="panelIndex">Destination panel</param>
        /// <param name="fromClipboard">True only for a paste whose clipboard still matches</param>
        /// <returns>Workflow with server-owned decisions</returns>
        internal WorkspaceWorkflowSnapshot BeginTransferWorkflow(WorkspaceClientToken token, IReadOnlyList<FileTransferEntry> entries, string destinationDir, int panelIndex, bool fromClipboard = false)
        {
            BiviumWorkspaceSnapshot snapshot;
            Action<BiviumWorkspaceSnapshot>[] subscribers;
            WorkspaceWorkflowSnapshot workflow;
            lock (this._lock)
            {
                this.RequireDesktopLeaseLocked(token);
                if (this._uploadRuntime?.Snapshot.Visible == true || this._workflowRuntime.Current?.IsActive == true || this._operationRuntime?.Snapshot.IsRunning == true)
                    throw new InvalidOperationException("A workspace workflow is already active");
                if (entries == null || entries.Count == 0 || !this._workflowSecurity.IsPathSafe(destinationDir))
                    throw new ArgumentException("Invalid transfer invocation");
                ImmutableArray<WorkspaceTransferEntry>.Builder captured = ImmutableArray.CreateBuilder<WorkspaceTransferEntry>();
                ImmutableArray<WorkspaceOverwriteQuestion>.Builder conflicts = ImmutableArray.CreateBuilder<WorkspaceOverwriteQuestion>();
                foreach (FileTransferEntry entry in entries)
                {
                    if (entry == null || !this._workflowSecurity.IsPathSafe(entry.SourcePath))
                        throw new ArgumentException("Invalid transfer invocation");
                    FileTransferMode mode = entry.Mode ?? this._workflowFiles.ResolveDefaultTransferMode(entry.SourcePath, destinationDir);
                    if (!Enum.IsDefined(mode))
                        throw new ArgumentException("Invalid transfer mode");
                    captured.Add(new WorkspaceTransferEntry(entry.SourcePath, mode));
                    bool directory = Directory.Exists(entry.SourcePath);
                    bool file = File.Exists(entry.SourcePath);
                    string target = Path.Combine(destinationDir, Path.GetFileName(entry.SourcePath));
                    if ((directory || file) && !this.SameWorkflowPath(entry.SourcePath, target) && (directory ? Directory.Exists(target) : File.Exists(target)))
                    {
                        string type = directory ? "directory" : "file";
                        string message = "A " + type + " named '" + Path.GetFileName(entry.SourcePath) + "' already exists in the destination.\n\nSource: " + entry.SourcePath + "\nDestination: " + target + "\n\n" + (directory ? "Merge it and overwrite conflicting contents?" : "Overwrite it?");
                        conflicts.Add(new WorkspaceOverwriteQuestion(entry.SourcePath, target, directory, "Overwrite " + type, message));
                    }
                }
                ImmutableArray<WorkspaceTransferEntry> transfers = captured.ToImmutable();
                ImmutableArray<string> sources = transfers.Select(entry => entry.SourcePath).ToImmutableArray();
                WorkspaceWorkflowKind kind = transfers.All(entry => entry.Mode == FileTransferMode.Copy) ? WorkspaceWorkflowKind.CopyEntries : transfers.All(entry => entry.Mode == FileTransferMode.Move) ? WorkspaceWorkflowKind.MoveEntries : WorkspaceWorkflowKind.TransferEntries;
                if (fromClipboard && (!sources.SequenceEqual(this._snapshot.Desktop.ClipboardPaths) || this._snapshot.Desktop.ClipboardIsCut != (kind == WorkspaceWorkflowKind.MoveEntries)))
                    throw new InvalidOperationException("The clipboard changed before paste was admitted");
                WorkspaceWorkflowInvocation invocation = new WorkspaceWorkflowInvocation("", destinationDir, panelIndex, kind.ToString(), "", -1, sources, transfers, fromClipboard);
                workflow = new WorkspaceWorkflowSnapshot(Guid.NewGuid(), 0, kind, invocation, conflicts.Count > 0 ? WorkspaceWorkflowPhase.AwaitingOverwrite : WorkspaceWorkflowPhase.Running, "", Guid.NewGuid(), Guid.Empty, "", conflicts.ToImmutable(), 0, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty);
                this._workflowRuntime.Responses.Clear();
                this._workflowRuntime.Current = workflow;
                WorkspaceOperationRuntime admitted = null;
                if (conflicts.Count == 0)
                {
                    admitted = this.AdmitWorkflowOperationLocked(workflow.Id, this.CreateTransferPlan(workflow));
                    workflow = workflow with { OperationId = admitted.Snapshot.Id };
                    this._workflowRuntime.Current = workflow;
                }
                snapshot = this.CommitWorkflowStateLocked();
                subscribers = this.GetSubscribers();
                if (admitted != null)
                    admitted.Work = Task.Run(() => this.RunWorkspaceOperation(admitted));
            }
            this.NotifySubscribers(subscribers, snapshot);
            return workflow;
        }

        /// <summary>Admits the acknowledged preview without regenerating Rand or the methods</summary>
        /// <param name="token">Invocation lease</param>
        /// <param name="sessionId">Mounted renamer session</param>
        /// <param name="expectedRevision">Revision of the displayed preview</param>
        /// <returns>Question ready for the explicit response of the Rename button</returns>
        internal WorkspaceWorkflowSnapshot BeginBatchRenameWorkflow(WorkspaceClientToken token, Guid sessionId, long expectedRevision)
        {
            BiviumWorkspaceSnapshot snapshot;
            Action<BiviumWorkspaceSnapshot>[] subscribers;
            WorkspaceWorkflowSnapshot workflow;
            lock (this._lock)
            {
                this.RequireDesktopLeaseLocked(token);
                if (this._uploadRuntime?.Snapshot.Visible == true || this._workflowRuntime.Current?.IsActive == true || this._operationRuntime?.Snapshot.IsRunning == true)
                    throw new InvalidOperationException("A workspace workflow is already active");
                RenamerSessionSnapshot session = this._desktopRuntime.Renamer;
                if (session == null || session.Id != sessionId || session.Revision != expectedRevision)
                    throw new InvalidOperationException("The rename preview changed before admission");
                RenamerDraft draft = JsonSerializer.Deserialize<RenamerDraft>(session.Draft);
                if (draft.Methods.Count == 0 || draft.Preview.Any(item => item.HasConflict || item.HasError))
                    throw new InvalidOperationException("The rename preview is not executable");
                string suffix = ".bivium_rename_temp_" + Guid.NewGuid().ToString("N");
                ImmutableArray<WorkspaceRenameItem>.Builder items = ImmutableArray.CreateBuilder<WorkspaceRenameItem>();
                foreach (RenamePreviewItem item in draft.Preview)
                {
                    if (item.OriginalName == item.NewName)
                        continue;
                    FileOperationResult validation = this._workflowFiles.ValidateNameOperation(WorkspaceWorkflowKind.RenameEntry, item.OriginalFullPath, "", item.NewName);
                    if (!validation.Success)
                        throw new InvalidOperationException(validation.ErrorMessage);
                    items.Add(new WorkspaceRenameItem(item.OriginalFullPath, item.OriginalName, item.NewName, Path.Combine(Path.GetDirectoryName(item.OriginalFullPath), item.NewName + suffix)));
                }
                if (items.Count == 0)
                    throw new InvalidOperationException("The rename preview has no changed names");
                WorkspaceWorkflowInvocation invocation = new WorkspaceWorkflowInvocation("", "", -1, "Advanced Rename", "", -1, RenamerSessionId: sessionId, RenameItems: items.ToImmutable());
                workflow = new WorkspaceWorkflowSnapshot(Guid.NewGuid(), 0, WorkspaceWorkflowKind.BatchRename, invocation, WorkspaceWorkflowPhase.AwaitingConfirmation, session.Draft, Guid.NewGuid(), Guid.Empty, "");
                this._workflowRuntime.Current = workflow;
                this._workflowRuntime.Responses.Clear();
                snapshot = this.CommitWorkflowStateLocked();
                subscribers = this.GetSubscribers();
            }
            this.NotifySubscribers(subscribers, snapshot);
            return workflow;
        }

        /// <summary>Derives the plan only from the already consumed decisions</summary>
        /// <param name="workflow">Authoritative workflow</param>
        /// <returns>Plan with no skipped entries and only approved overwrites</returns>
        private WorkspaceOperationPlan CreateTransferPlan(WorkspaceWorkflowSnapshot workflow)
        {
            WorkspaceWorkflowInvocation invocation = workflow.InvocationParameters;
            ImmutableArray<WorkspaceTransferEntry> transfers = invocation.Transfers.Where(entry => !workflow.SkippedPaths.Any(path => this.SameWorkflowPath(path, entry.SourcePath))).ToImmutableArray();
            return new WorkspaceOperationPlan(workflow.Kind, "", invocation.ParentPath, "", invocation.SourcePaths, transfers, workflow.OverwritePaths, invocation.FromClipboard);
        }

        /// <summary>Same filesystem comparison rules used by the existing Commander</summary>
        /// <param name="left">First path</param>
        /// <param name="right">Second path</param>
        /// <returns>True if the paths resolve to the same entry</returns>
        private bool SameWorkflowPath(string left, string right)
        {
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }

        /// <summary>Publishes only runtime references and lightweight progress</summary>
        /// <returns>Snapshot committed under the workspace lock</returns>
        private BiviumWorkspaceSnapshot CommitWorkflowStateLocked()
        {
            this._snapshot = new BiviumWorkspaceSnapshot(this._snapshot.Revision + 1, this._snapshot.Panels, this._snapshot.FloatingWindows, this._snapshot.ActiveClientLease, this._snapshot.Desktop, this._snapshot.Handoff, this._workflowRuntime.Reference, this._operationRuntime?.Snapshot, this._snapshot.Upload);
            return this._snapshot;
        }

        #endregion
    }
}
