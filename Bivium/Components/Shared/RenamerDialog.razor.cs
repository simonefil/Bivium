using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Bivium.Models;
using Bivium.Services;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Text.Json;

namespace Bivium.Components.Shared
{
    /// <summary>
    /// Floating window for advanced batch file renaming
    /// </summary>
    public partial class RenamerDialog : ComponentBase, IAsyncDisposable
    {
        #region Injected Services

        [Inject]
        private BiviumWorkspaceService _workspaceService { get; set; }

        #endregion

        #region Parameters

        /// <summary>
        /// Callback when the dialog is closed (true if files were renamed)
        /// </summary>
        [Parameter]
        public EventCallback<bool> OnClose { get; set; }

        /// <summary>Visual state received from the JS stacking</summary>
        [Parameter]
        public bool IsActive { get; set; }

        /// <summary>Notifies the window lifecycle</summary>
        [Parameter]
        public EventCallback OnStateChanged { get; set; }

        [Parameter]
        public string AttachmentId { get; set; } = "";

        [Parameter]
        public long LeaseGeneration { get; set; }

        /// <summary>Current workflow: each new revision renders the window, the authoritative state is re-read from the workspace</summary>
        [Parameter]
        public WorkspaceWorkflowSnapshot Workflow { get; set; }

        /// <summary>Current operation: batch progress and outcome arrive with the render</summary>
        [Parameter]
        public WorkspaceOperationSnapshot Operation { get; set; }

        #endregion

        #region Class Variables

        /// <summary>
        /// Whether the dialog is visible
        /// </summary>
        private bool _isVisible = false;

        /// <summary>Window open but temporarily hidden</summary>
        private bool _isMinimized;

        /// <summary>Activation request that the JS manager can invalidate</summary>
        private long _pendingActivation;

        /// <summary>Revision owning the post-render ticket</summary>
        private long _pendingActivationRevision;

        /// <summary>Provisional restore awaiting the JS outcome</summary>
        private bool _restorePending;

        /// <summary>Origin preserved across repeated restores</summary>
        private bool _restoreWasMinimized;

        /// <summary>Local lifecycle order used to discard stale continuations</summary>
        private long _lifecycleRevision;

        /// <summary>
        /// Source file entries to rename
        /// </summary>
        private List<FileSystemEntry> _entries = new List<FileSystemEntry>();

        /// <summary>
        /// Ordered stack of rename methods to apply
        /// </summary>
        private List<RenameMethod> _methodStack = new List<RenameMethod>();

        /// <summary>
        /// Preview items with computed new names
        /// </summary>
        private List<RenamePreviewItem> _previewItems = new List<RenamePreviewItem>();

        /// <summary>
        /// Current method being configured before adding to stack
        /// </summary>
        private RenameMethod _editingMethod = new RenameMethod();

        /// <summary>
        /// Currently selected method type in the dropdown
        /// </summary>
        private RenameMethodType _selectedMethodType = RenameMethodType.Replace;

        /// <summary>
        /// Error message for invalid parameters
        /// </summary>
        private string _paramsError = "";

        /// <summary>
        /// Status bar text
        /// </summary>
        private string _statusText = "";

        /// <summary>
        /// JS module reference for drag/resize interop
        /// </summary>
        private IJSObjectReference _interopModule = null;

        /// <summary>
        /// Whether a rename operation was performed
        /// </summary>
        private bool _didRename = false;

        /// <summary>
        /// Whether component-owned callbacks have been released
        /// </summary>
        private bool _isDisposed;

        /// <summary>Workflow and response of the adapter only, never execution of the two passes</summary>
        private WorkspaceWorkflowSnapshot _batchWorkflow;
        /// <summary>Stable identity of the answer to the current question</summary>
        private WorkspaceWorkflowResponse _batchResponse;
        /// <summary>Actual positions read from the authorized runtime, without regenerating the preview</summary>
        private readonly Dictionary<string, WorkspaceRenameItemState> _renameState = new Dictionary<string, WorkspaceRenameItemState>(StringComparer.Ordinal);
        /// <summary>The accepted plan cannot be replaced by form changes</summary>
        private bool IsBatchFormLocked => this._batchWorkflow != null;
        /// <summary>Close does not equal cancelling an accepted task</summary>
        private bool IsBatchRunning => this._batchWorkflow?.Phase == WorkspaceWorkflowPhase.Running;
        /// <summary>Batch operation of the current workflow, for progress and stop requests</summary>
        private WorkspaceOperationSnapshot _batchOperation;
        /// <summary>Window-local Escape module</summary>
        private IJSObjectReference _renamerModule;
        /// <summary>Draft modified and not yet published by the debounce</summary>
        private bool _draftPending;
        /// <summary>Debounce delay: continuous typing does not serialize the form and preview on every keystroke</summary>
        private const int DRAFT_PERSIST_DELAY_MS = 300;
        /// <summary>Debounce generation: a timer superseded by a later publication does not publish</summary>
        private long _draftPersistGeneration;

        /// <summary>Session owned by the workspace, not by the circuit</summary>
        private RenamerSessionSnapshot _session;

        /// <summary>Authoritative window, distinct from the bulky draft</summary>
        private FloatingWindowSnapshot _window = new FloatingWindowsSnapshot().Renamer;

        /// <summary>JS callback belonging exclusively to the adapter</summary>
        private DotNetObjectReference<RenamerDialog> _dotNetRef;

        /// <summary>Last geometry notification accepted for this session</summary>
        private long _geometrySequence;

        /// <summary>Lease generation for which the adapter was rebuilt</summary>
        private long _adapterLeaseGeneration;
        /// <summary>Official window root, without recreating markup or skin</summary>
        private Radzen.Blazor.RadzenCard _surfaceRoot;
        /// <summary>Visual publisher separate from the preview draft</summary>
        private IJSObjectReference _surfaceModule;
        /// <summary>Mounted visual owner</summary>
        private Guid _surfaceSessionId;
        /// <summary>Lease of the mounted visual publisher</summary>
        private long _surfaceGeneration;

        /// <summary>Geometry serialized with invariant culture</summary>
        private string WindowStyle => this._window.Width > 0 ? FormattableString.Invariant($"left:{this._window.Left}px;top:{this._window.Top}px;width:{this._window.Width}px;height:{this._window.Height}px") : "";

        #endregion

        #region Public Methods

        /// <summary>
        /// Shows the renamer dialog with the specified file entries
        /// </summary>
        /// <param name="entries">File entries to rename (files only, no directories)</param>
        public void Show(List<FileSystemEntry> entries)
        {
            if (this._isDisposed || this.IsOpen())
                return;
            this._lifecycleRevision++;
            this._batchWorkflow = null;
            this._batchResponse = null;
            this._renameState.Clear();
            this._entries = entries;
            this._methodStack.Clear();
            this._editingMethod = new RenameMethod();
            this._selectedMethodType = RenameMethodType.Replace;
            this._paramsError = "";
            this._didRename = false;
            this._isVisible = true;

            // Generate initial preview (no methods, names unchanged)
            this.RecalculatePreview();
            try
            {
                this._geometrySequence = 0;
                this._session = this._workspaceService.OpenRenamerSession(this.GetClientToken(), this.SerializeDraft());
                this.ApplyDraft(this._session);
                this.ApplyWindow(this._workspaceService.GetSnapshot().FloatingWindows.Renamer);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is ObjectDisposedException)
            {
                this._isVisible = false;
                return;
            }
            this.StateHasChanged();
            _ = this.RestoreAsync();
        }

        /// <summary>Indicates whether the renamer session is still open</summary>
        public bool IsOpen() => this._isVisible || this._isMinimized;

        /// <summary>Window title</summary>
        public string GetTitle() => "Advanced Rename";

        /// <summary>Preserves stack, configuration, preview and geometry</summary>
        public async Task MinimizeAsync()
        {
            if (this._isDisposed || !this._isVisible)
                return;
            this._isVisible = false;
            this._isMinimized = true;
            this._lifecycleRevision++;
            this._pendingActivation = 0;
            this._restorePending = false;
            this.PersistVisibility();
            if (this._interopModule != null)
                await this._interopModule.InvokeVoidAsync("cancelFloatingWindowActivation", "renamer-window");
            this.StateHasChanged();
            await this.OnStateChanged.InvokeAsync();
        }

        /// <summary>Restores and activates the same window after the render</summary>
        public async Task RestoreAsync()
        {
            if (this._isDisposed || !this.IsOpen())
                return;
            long revision = ++this._lifecycleRevision;
            if (this._interopModule == null)
                this._interopModule = await this.JSRuntime.InvokeAsync<IJSObjectReference>("import", "./js/interop.js");
            if (this._isDisposed || !this.IsOpen() || revision != this._lifecycleRevision)
                return;
            long activation = await this._interopModule.InvokeAsync<long>("requestFloatingWindowActivation", "renamer-window");
            if (this._isDisposed || !this.IsOpen() || revision != this._lifecycleRevision)
            {
                await this._interopModule.InvokeVoidAsync("cancelFloatingWindowActivation", "renamer-window", activation);
                return;
            }
            if (activation == 0)
            {
                await this.CompleteRestoreAsync(revision, "blocked");
                return;
            }
            if (!this._restorePending)
                this._restoreWasMinimized = this._isMinimized;
            this._restorePending = true;
            this._pendingActivationRevision = revision;
            this._pendingActivation = activation;
            this._isVisible = true;
            this._isMinimized = false;
            this.PersistVisibility();
            this.StateHasChanged();
            await this.OnStateChanged.InvokeAsync();
        }

        #endregion

        #region Private Methods

        /// <summary>Rebuilds the draft only on the new mount, without calling Show</summary>
        protected override void OnParametersSet()
        {
            if (this._isDisposed)
                return;
            RenamerSessionSnapshot session = this._workspaceService.GetRenamerSession(this.GetClientToken());
            if (session == null)
            {
                if (this._session != null)
                {
                    this._lifecycleRevision++;
                    this._pendingActivation = 0;
                    this._restorePending = false;
                }
                this._session = null;
                this._batchWorkflow = null;
                this._batchOperation = null;
                this._renameState.Clear();
                this._isVisible = false;
                this._isMinimized = false;
                return;
            }
            if (this._session?.Id != session.Id || this._adapterLeaseGeneration != this.LeaseGeneration || session.Revision > this._session.Revision)
                this.ApplyDraft(session);
            this._session = session;
            WorkspaceWorkflowSnapshot workflow = this._workspaceService.GetWorkflow(this.GetClientToken());
            this._batchWorkflow = workflow?.Kind == WorkspaceWorkflowKind.BatchRename && workflow.InvocationParameters.RenamerSessionId == session.Id ? workflow : null;
            this._batchOperation = null;
            if (this._batchResponse?.QuestionId != this._batchWorkflow?.QuestionId)
                this._batchResponse = null;
            this._renameState.Clear();
            foreach (WorkspaceRenameItemState item in this._workspaceService.GetBatchRenameState(this.GetClientToken(), session.Id))
                this._renameState.TryAdd(item.Item.OriginalPath, item);
            if (this._batchWorkflow != null)
            {
                WorkspaceOperationSnapshot operation = this._workspaceService.GetSnapshot().Operation;
                if (operation?.WorkflowId == this._batchWorkflow.Id)
                {
                    this._batchOperation = operation;
                    this._statusText = !string.IsNullOrEmpty(operation.ErrorMessage) ? operation.ErrorMessage : operation.Phase == WorkspaceOperationPhase.CancellationRequested ? "Stopping..." : operation.Stage + " " + operation.ProgressCurrent + "/" + operation.ProgressTotal;
                    this._didRename |= operation.FilesProcessed > 0;
                }
            }
            this.ApplyWindow(this._workspaceService.GetSnapshot().FloatingWindows.Renamer);
        }

        /// <summary>Serializes a copy of the form and preview, without keeping mutable references</summary>
        /// <returns>Materialized draft</returns>
        private string SerializeDraft()
        {
            return JsonSerializer.Serialize(new RenamerDraft { Entries = this._entries, Methods = this._methodStack, Preview = this._previewItems, EditingMethod = this._editingMethod, SelectedMethodType = this._selectedMethodType, ParamsError = this._paramsError, StatusText = this._statusText, DidRename = this._didRename });
        }

        /// <summary>Hydrates without invoking RenameEngine or the input callbacks</summary>
        /// <param name="session">Authoritative draft</param>
        private void ApplyDraft(RenamerSessionSnapshot session)
        {
            if (this._session?.Id != session.Id || this._adapterLeaseGeneration != this.LeaseGeneration)
                this._geometrySequence = 0;
            this._adapterLeaseGeneration = this.LeaseGeneration;
            RenamerDraft draft = JsonSerializer.Deserialize<RenamerDraft>(session.Draft);
            this._entries = draft.Entries;
            this._methodStack = draft.Methods;
            this._previewItems = draft.Preview;
            this._editingMethod = draft.EditingMethod;
            this._selectedMethodType = draft.SelectedMethodType;
            this._paramsError = draft.ParamsError;
            this._statusText = draft.StatusText;
            this._didRename = draft.DidRename;
            this._session = session;
        }

        /// <summary>Also preserves the inputs not yet added to the stack</summary>
        private void PersistDraft()
        {
            this._draftPending = false;
            this._draftPersistGeneration++;
            if (this._session == null || this._isDisposed)
                return;
            if (this._workspaceService.TryUpdateRenamerDraft(this.GetClientToken(), this._session.Id, this._session.Revision, this.SerializeDraft(), out RenamerSessionSnapshot session))
                this._session = session;
            else if (session != null && this._workspaceService.ValidateMutation(this.GetClientToken()))
                this.ApplyDraft(session);
        }

        /// <summary>Coalesces draft publications during typing; handoff and explicit actions publish immediately</summary>
        private void SchedulePersistDraft()
        {
            if (this._session == null || this._isDisposed)
                return;
            this._draftPending = true;
            long generation = ++this._draftPersistGeneration;
            _ = this.PersistDraftAfterDelayAsync(generation);
        }

        /// <summary>Publishes the draft after the delay, unless a later publication has already superseded it</summary>
        /// <param name="generation">Captured debounce generation</param>
        private async Task PersistDraftAfterDelayAsync(long generation)
        {
            await Task.Delay(DRAFT_PERSIST_DELAY_MS);
            await this.InvokeAsync(() =>
            {
                if (!this._isDisposed && this._draftPending && generation == this._draftPersistGeneration)
                    this.PersistDraft();
            });
        }

        /// <summary>Token of the current mount</summary>
        /// <returns>Lease to revalidate in the workspace</returns>
        private WorkspaceClientToken GetClientToken() => new WorkspaceClientToken(this.AttachmentId, this.LeaseGeneration);

        /// <summary>Projects the server window state</summary>
        /// <param name="window">Authoritative window</param>
        private void ApplyWindow(FloatingWindowSnapshot window)
        {
            this._window = window;
            this._isVisible = window.Visible;
            this._isMinimized = window.Minimized;
        }

        /// <summary>Publishes open and minimized state before interop</summary>
        private void PersistVisibility()
        {
            this.PersistWindow(this._window with { Visible = this._isVisible, Minimized = this._isMinimized });
        }

        /// <summary>Window commit with global CAS and source state comparison</summary>
        /// <param name="window">New state of the renamer window only</param>
        private void PersistWindow(FloatingWindowSnapshot window)
        {
            if (this._session == null || this._isDisposed)
                return;
            Guid id = this._session.Id;
            FloatingWindowSnapshot expectedWindow = this._window;
            BiviumWorkspaceSnapshot observed = this._workspaceService.GetSnapshot();
            bool updated = this._workspaceService.TryUpdateDesktopWindow(this.GetClientToken(), observed.Revision, id, false, expectedWindow, window, out BiviumWorkspaceSnapshot snapshot);
            if (!updated && snapshot.Revision != observed.Revision && snapshot.Desktop.RenamerId == id && snapshot.FloatingWindows.Renamer == expectedWindow)
                this._workspaceService.TryUpdateDesktopWindow(this.GetClientToken(), snapshot.Revision, id, false, expectedWindow, window, out snapshot);
            this.ApplyWindow(snapshot.FloatingWindows.Renamer);
        }

        /// <summary>Preserves geometry, stacking and focused control</summary>
        /// <param name="update">Current DOM measurements</param>
        /// <returns>Authoritative window, also used to reconcile a rejected CAS</returns>
        [JSInvokable]
        public FloatingWindowSnapshot OnWindowGeometryChanged(FloatingWindowGeometryUpdate update)
        {
            if (this._isDisposed || update == null || !update.IsValid || !this.IsOpen() || this._session == null || update.SessionId != this._session.Id.ToString() || update.LeaseGeneration != this.LeaseGeneration || update.Sequence <= this._geometrySequence)
                return null;
            this._geometrySequence = update.Sequence;
            this.PersistWindow(this._window with { Left = update.Left, Top = update.Top, Width = update.Width, Height = update.Height, ViewportWidth = update.ViewportWidth, ViewportHeight = update.ViewportHeight, MruOrder = update.MruOrder, FocusTarget = update.FocusTarget });
            return this._window;
        }

        /// <summary>Cancels only the restore rejected by the modal that is still current</summary>
        /// <param name="revision">Revision owning the request</param>
        /// <param name="result">JS outcome, distinct from a stale ticket</param>
        /// <returns>Asynchronous notification of any rollback</returns>
        private async Task CompleteRestoreAsync(long revision, string result)
        {
            if (this._isDisposed || revision != this._lifecycleRevision || !this._restorePending)
                return;
            this._restorePending = false;
            this._pendingActivation = 0;
            if (result == "blocked" && this._restoreWasMinimized)
            {
                this._isVisible = false;
                this._isMinimized = true;
                this._lifecycleRevision++;
                this.PersistVisibility();
                this.StateHasChanged();
                await this.OnStateChanged.InvokeAsync();
            }
        }

        /// <summary>
        /// Registers drag and resize after the actual render, without an unanchored time window
        /// </summary>
        /// <param name="firstRender">Indicates the first render of the component</param>
        protected override async System.Threading.Tasks.Task OnAfterRenderAsync(bool firstRender)
        {
            if (this._isDisposed)
                return;

            if (this._interopModule == null)
            {
                this._interopModule = await this.JSRuntime.InvokeAsync<IJSObjectReference>("import", "./js/interop.js");
            }
            if (this._isDisposed)
                return;

            if (this._dotNetRef == null)
                this._dotNetRef = DotNetObjectReference.Create(this);
            await this._interopModule.InvokeVoidAsync("initWindowDrag", "renamer-window", "renamer-titlebar", "renamer-resize-handle", this._dotNetRef, this._session?.Id.ToString() ?? "", this.LeaseGeneration);
            if (this._renamerModule == null)
            {
                IJSObjectReference renamerModule = await this.JSRuntime.InvokeAsync<IJSObjectReference>("import", "./js/renamer.js");
                if (this._isDisposed)
                {
                    await renamerModule.DisposeAsync();
                    return;
                }
                this._renamerModule = renamerModule;
                await this._renamerModule.InvokeVoidAsync("registerRenamerEscape", "renamer-window", this._dotNetRef);
            }
            if (this._session == null && this._surfaceSessionId != Guid.Empty && this._surfaceModule != null)
            {
                await this._surfaceModule.InvokeVoidAsync("disposeSurface", this._surfaceRoot.Element);
                this._surfaceSessionId = Guid.Empty;
            }
            if (this._session != null && (this._surfaceSessionId != this._session.Id || this._surfaceGeneration != this.LeaseGeneration))
            {
                WorkspaceClientToken token = this.GetClientToken();
                WorkspaceRenamerViewState view = this._workspaceService.GetRenamerViewState(token, this._session.Id);
                if (view != null)
                {
                    IJSObjectReference module = this._surfaceModule ?? await this.JSRuntime.InvokeAsync<IJSObjectReference>("import", "./js/surface-adapters.js");
                    if (this._isDisposed || token != this.GetClientToken() || this._session?.Id != view.SessionId)
                    {
                        if (!ReferenceEquals(this._surfaceModule, module)) await module.DisposeAsync();
                        return;
                    }
                    if (this._surfaceModule != null && !ReferenceEquals(this._surfaceModule, module))
                    {
                        await module.DisposeAsync();
                        module = this._surfaceModule;
                    }
                    this._surfaceModule = module;
                    long generation = token.Generation;
                    if (await module.InvokeAsync<bool>("installRenamerSurface", this._surfaceRoot.Element, this._dotNetRef, generation, view) && !this._isDisposed && token == this.GetClientToken() && this._session?.Id == view.SessionId)
                    {
                        this._surfaceSessionId = view.SessionId;
                        this._surfaceGeneration = generation;
                    }
                }
            }
            if (this._isVisible && this._pendingActivation > 0)
            {
                long activation = this._pendingActivation;
                long revision = this._pendingActivationRevision;
                this._pendingActivation = 0;
                string result = await this._interopModule.InvokeAsync<string>("activateFloatingWindowWithResult", "renamer-window", activation);
                await this.CompleteRestoreAsync(revision, result);
            }
        }

        /// <summary>
        /// Handles method type dropdown change - resets editing method and recalculates preview
        /// </summary>
        private void HandleMethodTypeChanged()
        {
            if (this.IsBatchFormLocked)
                return;
            // Reset editing method when switching type
            this._editingMethod = new RenameMethod();
            this._paramsError = "";
            this.RecalculatePreview();
        }

        /// <summary>
        /// Recalculates the preview by applying all stack methods to entries
        /// </summary>
        private void RecalculatePreview()
        {
            if (this.IsBatchFormLocked)
                return;
            // Build a temporary stack including the editing method for live preview
            List<RenameMethod> previewStack = new List<RenameMethod>(this._methodStack);

            // Add the editing method to preview if it has meaningful input
            if (this.HasEditingMethodInput())
            {
                RenameMethod editCopy = this._editingMethod.Clone();
                editCopy.MethodType = this._selectedMethodType;
                previewStack.Add(editCopy);
            }

            this._previewItems = RenameEngine.GeneratePreview(this._entries, previewStack);

            // Validate regex if applicable
            this._paramsError = this.ValidateEditingMethod();

            // Update status text
            this.UpdateStatusText();
        }

        /// <summary>
        /// Returns the removal modes for the Radzen control
        /// </summary>
        /// <returns>Labels indexed by boolean value</returns>
        private Dictionary<bool, string> GetRemoveModes()
        {
            return new Dictionary<bool, string> { { false, "By position" }, { true, "By pattern" } };
        }

        /// <summary>
        /// Returns the uppercase and lowercase conversion modes
        /// </summary>
        /// <returns>Labels indexed by mode</returns>
        private Dictionary<int, string> GetCaseModes()
        {
            return new Dictionary<int, string> { { 0, "lowercase" }, { 1, "UPPERCASE" }, { 2, "Title Case" } };
        }

        /// <summary>
        /// Returns the name and extension scopes
        /// </summary>
        /// <returns>Labels indexed by scope</returns>
        private Dictionary<int, string> GetNameScopes()
        {
            return new Dictionary<int, string> { { 0, "Name only" }, { 1, "Extension only" }, { 2, "Full name" } };
        }

        /// <summary>
        /// Returns the positions available for trim
        /// </summary>
        /// <returns>Labels indexed by position</returns>
        private Dictionary<int, string> GetTrimLocations()
        {
            return new Dictionary<int, string> { { 0, "Start" }, { 1, "End" }, { 2, "Both" } };
        }

        /// <summary>
        /// Checks if the editing method has meaningful input for preview
        /// </summary>
        /// <returns>True if the editing method has input worth previewing</returns>
        private bool HasEditingMethodInput()
        {
            bool result = false;

            if (this._selectedMethodType == RenameMethodType.Replace)
            {
                result = !string.IsNullOrEmpty(this._editingMethod.SearchText);
            }
            else if (this._selectedMethodType == RenameMethodType.Add)
            {
                result = !string.IsNullOrEmpty(this._editingMethod.InsertText);
            }
            else if (this._selectedMethodType == RenameMethodType.Remove)
            {
                if (this._editingMethod.RemoveByPattern)
                {
                    result = !string.IsNullOrEmpty(this._editingMethod.RemovePattern);
                }
                else
                {
                    result = this._editingMethod.RemoveCount > 0;
                }
            }
            else if (this._selectedMethodType == RenameMethodType.NewCase)
            {
                result = true;
            }
            else if (this._selectedMethodType == RenameMethodType.NewName)
            {
                result = !string.IsNullOrEmpty(this._editingMethod.NamePattern);
            }
            else if (this._selectedMethodType == RenameMethodType.Trim)
            {
                result = !string.IsNullOrEmpty(this._editingMethod.TrimCharacters);
            }

            return result;
        }

        /// <summary>
        /// Validates the editing method for regex errors
        /// </summary>
        /// <returns>Error message or empty string</returns>
        private string ValidateEditingMethod()
        {
            string error = "";

            if (this._selectedMethodType == RenameMethodType.Replace && this._editingMethod.UseRegex && !string.IsNullOrEmpty(this._editingMethod.SearchText))
            {
                try
                {
                    System.Text.RegularExpressions.Regex.Match("", this._editingMethod.SearchText);
                }
                catch (System.ArgumentException ex)
                {
                    error = "Invalid regex: " + ex.Message;
                }
            }
            else if (this._selectedMethodType == RenameMethodType.Remove && this._editingMethod.RemoveByPattern && this._editingMethod.RemovePatternUseRegex && !string.IsNullOrEmpty(this._editingMethod.RemovePattern))
            {
                try
                {
                    System.Text.RegularExpressions.Regex.Match("", this._editingMethod.RemovePattern);
                }
                catch (System.ArgumentException ex)
                {
                    error = "Invalid regex: " + ex.Message;
                }
            }

            return error;
        }

        /// <summary>
        /// Updates the status bar text with file count and conflict info
        /// </summary>
        private void UpdateStatusText()
        {
            int fileCount = this._previewItems.Count;
            int conflictCount = 0;
            int errorCount = 0;
            int changedCount = 0;

            for (int i = 0; i < this._previewItems.Count; i++)
            {
                if (this._previewItems[i].HasConflict)
                {
                    conflictCount++;
                }
                if (this._previewItems[i].HasError)
                {
                    errorCount++;
                }
                if (this._previewItems[i].OriginalName != this._previewItems[i].NewName)
                {
                    changedCount++;
                }
            }

            this._statusText = fileCount + " files, " + changedCount + " changed";

            if (conflictCount > 0)
            {
                this._statusText = this._statusText + ", " + conflictCount + " conflicts";
            }

            if (errorCount > 0)
            {
                this._statusText = this._statusText + ", " + errorCount + " errors";
            }
            this.SchedulePersistDraft();
        }

        /// <summary>
        /// Checks if the rename button should be enabled
        /// </summary>
        /// <returns>True if rename can be executed</returns>
        private bool CanRename()
        {
            if (this._batchWorkflow != null)
                return this._batchWorkflow.Phase == WorkspaceWorkflowPhase.AwaitingConfirmation;
            if (this._workspaceService.GetWorkflow(this.GetClientToken())?.IsActive == true)
                return false;
            // Must have methods in the stack
            if (this._methodStack.Count == 0)
            {
                return false;
            }

            // Must have no conflicts or errors
            for (int i = 0; i < this._previewItems.Count; i++)
            {
                if (this._previewItems[i].HasConflict || this._previewItems[i].HasError)
                {
                    return false;
                }
            }

            // Must have at least one changed name
            bool hasChanged = false;
            for (int i = 0; i < this._previewItems.Count; i++)
            {
                if (this._previewItems[i].OriginalName != this._previewItems[i].NewName)
                {
                    hasChanged = true;
                    break;
                }
            }

            return hasChanged;
        }

        /// <summary>
        /// Adds the current editing method to the stack
        /// </summary>
        private void HandleAddMethod()
        {
            if (this.IsBatchFormLocked)
                return;
            // Don't add empty methods to the stack
            if (!this.HasEditingMethodInput())
            {
                return;
            }

            // Validate before adding
            string error = this.ValidateEditingMethod();
            if (!string.IsNullOrEmpty(error))
            {
                this._paramsError = error;
                this.PersistDraft();
                return;
            }

            // Clone the editing method and set its type
            RenameMethod method = this._editingMethod.Clone();
            method.MethodType = this._selectedMethodType;
            this._methodStack.Add(method);

            // Reset editing method
            this._editingMethod = new RenameMethod();
            this._paramsError = "";

            // Recalculate preview with stack only (no editing method input)
            this._previewItems = RenameEngine.GeneratePreview(this._entries, this._methodStack);
            this.UpdateStatusText();
            this.PersistDraft();
        }

        /// <summary>
        /// Removes a method from the stack by index
        /// </summary>
        /// <param name="index">Index of the method to remove</param>
        private void HandleRemoveMethod(int index)
        {
            if (this.IsBatchFormLocked)
                return;
            if (index >= 0 && index < this._methodStack.Count)
            {
                this._methodStack.RemoveAt(index);
                this.RecalculatePreview();
            }
        }

        /// <summary>
        /// Moves a method up in the stack
        /// </summary>
        /// <param name="index">Index of the method to move up</param>
        private void HandleMoveUp(int index)
        {
            if (this.IsBatchFormLocked)
                return;
            if (index > 0 && index < this._methodStack.Count)
            {
                RenameMethod temp = this._methodStack[index];
                this._methodStack[index] = this._methodStack[index - 1];
                this._methodStack[index - 1] = temp;
                this.RecalculatePreview();
            }
        }

        /// <summary>
        /// Moves a method down in the stack
        /// </summary>
        /// <param name="index">Index of the method to move down</param>
        private void HandleMoveDown(int index)
        {
            if (this.IsBatchFormLocked)
                return;
            if (index >= 0 && index < this._methodStack.Count - 1)
            {
                RenameMethod temp = this._methodStack[index];
                this._methodStack[index] = this._methodStack[index + 1];
                this._methodStack[index + 1] = temp;
                this.RecalculatePreview();
            }
        }

        /// <summary>
        /// Executes the rename operation using two-pass strategy
        /// </summary>
        private System.Threading.Tasks.Task HandleRename()
        {
            if (this._isDisposed || this._session == null || !this.CanRename())
                return Task.CompletedTask;
            try
            {
                if (this._batchWorkflow == null)
                {
                    if (!this._workspaceService.TryUpdateRenamerDraft(this.GetClientToken(), this._session.Id, this._session.Revision, this.SerializeDraft(), out RenamerSessionSnapshot acknowledged))
                    {
                        this._statusText = "The rename preview was not acknowledged.";
                        return Task.CompletedTask;
                    }
                    this._session = acknowledged;
                    this._batchWorkflow = this._workspaceService.BeginBatchRenameWorkflow(this.GetClientToken(), acknowledged.Id, acknowledged.Revision);
                }
                this._batchResponse ??= new WorkspaceWorkflowResponse(this._batchWorkflow.Id, this._batchWorkflow.Revision, this._batchWorkflow.QuestionId, Guid.NewGuid(), false, "");
                WorkspaceWorkflowResponseResult result = this._workspaceService.RespondToWorkflow(this.GetClientToken(), this._batchResponse);
                if (result is WorkspaceWorkflowResponseResult.Stale or WorkspaceWorkflowResponseResult.Denied)
                    this._statusText = "The rename response was not accepted. The authoritative workflow is unchanged.";
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is UnauthorizedAccessException || ex is ObjectDisposedException)
            {
                this._statusText = ex.Message;
            }
            return Task.CompletedTask;
        }

        /// <summary>
        /// Closes the dialog without renaming
        /// </summary>
        private async System.Threading.Tasks.Task HandleClose()
        {
            WorkspaceWorkflowSnapshot workflow = this._workspaceService.GetWorkflow(this.GetClientToken());
            if (workflow?.Kind == WorkspaceWorkflowKind.BatchRename && workflow.InvocationParameters.RenamerSessionId == this._session?.Id)
            {
                if (workflow.Phase == WorkspaceWorkflowPhase.Running)
                    return;
                if (workflow.Phase is WorkspaceWorkflowPhase.AwaitingConfirmation or WorkspaceWorkflowPhase.Failed or WorkspaceWorkflowPhase.Cancelled)
                {
                    if (this._batchResponse?.QuestionId != workflow.QuestionId || !this._batchResponse.Cancelled)
                        this._batchResponse = new WorkspaceWorkflowResponse(workflow.Id, workflow.Revision, workflow.QuestionId, Guid.NewGuid(), true, "");
                    WorkspaceWorkflowResponseResult response = this._workspaceService.RespondToWorkflow(this.GetClientToken(), this._batchResponse);
                    if (response is WorkspaceWorkflowResponseResult.Stale or WorkspaceWorkflowResponseResult.Denied)
                        return;
                }
            }
            if (this._session != null && !this._workspaceService.TryCloseDesktopSession(this.GetClientToken(), this._session.Id, this._session.Revision, false))
                return;
            this._session = null;
            this._isVisible = false;
            this._isMinimized = false;
            this._lifecycleRevision++;
            this._pendingActivation = 0;
            this._restorePending = false;
            if (this._interopModule != null)
                await this._interopModule.InvokeVoidAsync("cancelFloatingWindowActivation", "renamer-window");
            await this.OnStateChanged.InvokeAsync();
            await this.OnClose.InvokeAsync(this._didRename);
        }

        /// <summary>Escape with focus in the window: closes like Cancel, without reaching the panels</summary>
        /// <returns>Completion of the close</returns>
        [JSInvokable]
        public Task OnRenamerEscape()
        {
            return this.InvokeAsync(async () =>
            {
                if (this._isDisposed || !this._isVisible || this.IsBatchRunning)
                    return;
                await this.HandleClose();
            });
        }

        /// <summary>Requests cancellation of the running batch; completed renames remain as documented by the service</summary>
        private void HandleStopRename()
        {
            WorkspaceOperationSnapshot operation = this._batchOperation;
            if (this._isDisposed || operation == null || operation.Phase != WorkspaceOperationPhase.Running)
                return;
            if (!this._workspaceService.TryCancelWorkspaceOperation(this.GetClientToken(), operation.Id, operation.Revision))
                this._statusText = "Stop was not accepted. The rename operation is unchanged.";
        }

        /// <summary>Colors the preview cells: original struck through if it changes, conflict in red, error struck through in gray</summary>
        /// <param name="args">Preview cell</param>
        private void HandlePreviewCellRender(Radzen.DataGridCellRenderEventArgs<RenamePreviewItem> args)
        {
            RenamePreviewItem item = args.Data;
            if (item == null || args.Column == null)
                return;
            bool changed = item.OriginalName != item.NewName;
            string cssClass = "";
            if (args.Column.Property == nameof(RenamePreviewItem.OriginalName))
                cssClass = changed ? "renamer-cell-original-changed" : "";
            else if (args.Column.Property == nameof(RenamePreviewItem.NewName))
                cssClass = item.HasConflict ? "renamer-cell-conflict" : item.HasError ? "renamer-cell-error" : changed ? "renamer-cell-changed" : "";
            if (!string.IsNullOrEmpty(cssClass))
                args.Attributes["class"] = cssClass;
        }

        #endregion

        /// <summary>CAS of the visual session, without recalculating the preview or confirming input</summary>
        /// <param name="generation">Captured lease</param>
        /// <param name="view">Popup of the captured session</param>
        /// <returns>Acknowledged revision, or -1</returns>
        [JSInvokable]
        public long OnRenamerViewChanged(long generation, WorkspaceRenamerViewState view)
        {
            return !this._isDisposed && generation == this.LeaseGeneration && view?.SessionId == this._session?.Id ? this._workspaceService.PublishRenamerViewState(this.GetClientToken(), view) : -1;
        }

        /// <summary>Confirms the form and the already materialized preview, without regenerating random tokens</summary>
        /// <param name="cancellationToken">Attempt limit</param>
        /// <returns>True only for the confirmed draft and geometry</returns>
        internal async Task<bool> FlushForHandoffAsync(CancellationToken cancellationToken)
        {
            if (this._isDisposed || this._restorePending)
                return false;
            if (this._session == null)
                return !this.IsOpen();
            Guid id = this._session.Id;
            RenamerSessionSnapshot current = this._workspaceService.GetRenamerSession(this.GetClientToken());
            if (current == null)
                return true;
            if (this._interopModule == null || !await this._interopModule.InvokeAsync<bool>("flushWindowGeometry", cancellationToken, "renamer-window"))
                return false;
            cancellationToken.ThrowIfCancellationRequested();
            if (this._isDisposed || this._session?.Id != id)
                return false;
            current = this._workspaceService.GetRenamerSession(this.GetClientToken());
            if (current == null)
                return true;
            WorkspaceWorkflowSnapshot workflow = this._workspaceService.GetWorkflow(this.GetClientToken());
            if (workflow?.Kind == WorkspaceWorkflowKind.BatchRename && workflow.InvocationParameters.RenamerSessionId == id)
                return current.Id == id && current.Revision == this._session.Revision;
            if (!this._workspaceService.TryUpdateRenamerDraft(this.GetClientToken(), id, this._session.Revision, this.SerializeDraft(), out RenamerSessionSnapshot session))
                return false;
            this._session = session;
            return true;
        }

        /// <summary>State of the captured row, read from the server and not inferred from UI names</summary>
        /// <param name="item">Materialized preview row</param>
        /// <returns>Outcome and actual position of the step, or empty</returns>
        private string GetRenameItemStatus(RenamePreviewItem item)
        {
            return !this._renameState.TryGetValue(item.OriginalFullPath, out WorkspaceRenameItemState state) ? "" : state.Phase + ": " + state.CurrentPath + (string.IsNullOrEmpty(state.ErrorMessage) ? "" : " — " + state.ErrorMessage);
        }

        #region IAsyncDisposable

        /// <summary>
        /// Removes global listeners and drag callbacks
        /// </summary>
        /// <returns>Asynchronous release operation</returns>
        public async ValueTask DisposeAsync()
        {
            if (this._isDisposed)
                return;
            this._isDisposed = true;

            if (this._surfaceModule != null)
            {
                try
                {
                    try { await this._surfaceModule.InvokeVoidAsync("disposeSurface", this._surfaceRoot.Element); }
                    finally { await this._surfaceModule.DisposeAsync(); }
                }
                catch (Exception ex) when (ex is JSDisconnectedException || ex is OperationCanceledException) { }
            }

            if (this._renamerModule != null)
            {
                try
                {
                    try { await this._renamerModule.InvokeVoidAsync("disposeRenamerEscape", "renamer-window"); }
                    finally { await this._renamerModule.DisposeAsync(); }
                }
                catch (Exception ex) when (ex is JSDisconnectedException || ex is OperationCanceledException) { }
            }

            this._dotNetRef?.Dispose();

            if (this._interopModule == null)
                return;

            try
            {
                try
                {
                    await this._interopModule.InvokeVoidAsync("disposeWindowDrag", "renamer-window");
                }
                finally
                {
                    await this._interopModule.DisposeAsync();
                }
            }
            catch (Exception ex) when (ex is JSDisconnectedException || ex is OperationCanceledException)
            {
            }
        }

        #endregion
    }
}
