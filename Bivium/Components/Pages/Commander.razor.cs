using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using System.Text.Json;
using Bivium.Models;
using Bivium.Services;
using Bivium.Components.Shared;
using Bivium.Components.Panel;
using Bivium.Components.Tree;
using Radzen;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;

namespace Bivium.Components.Pages
{
    /// <summary>
    /// Main commander page - orchestrates dual-panel file manager
    /// </summary>
    public partial class Commander : ComponentBase, IAsyncDisposable
    {
        #region Injected Services

        /// <summary>
        /// Application settings with hot-reload support
        /// </summary>
        [Inject]
        private IOptionsMonitor<CommanderSettings> _settings { get; set; }

        /// <summary>
        /// File system service
        /// </summary>
        [Inject]
        private IFileSystemService _fileSystemService { get; set; }

        /// <summary>
        /// Global runtime workspace
        /// </summary>
        [Inject]
        private BiviumWorkspaceService _workspaceService { get; set; }

        /// <summary>
        /// Global terminal runtime used only by the explicit workspace reset
        /// </summary>
        [Inject]
        private TerminalRuntimeService _terminalRuntimeService { get; set; }

        /// <summary>
        /// Filesystem path validation
        /// </summary>
        [Inject]
        private SecurityService _securityService { get; set; }

        /// <summary>
        /// File operation service
        /// </summary>
        [Inject]
        private IFileOperationService _fileOperationService { get; set; }

        /// <summary>
        /// Archive service for compression/extraction
        /// </summary>
        [Inject]
        private IArchiveService _archiveService { get; set; }

        /// <summary>
        /// Local authentication service
        /// </summary>
        [Inject]
        private AuthenticationService _authenticationService { get; set; }

        /// <summary>
        /// Authentication state provider
        /// </summary>
        [Inject]
        private AuthenticationStateProvider _authenticationStateProvider { get; set; }

        /// <summary>
        /// HTTP context used only for server-observed client metadata
        /// </summary>
        [Inject]
        private IHttpContextAccessor _httpContextAccessor { get; set; }

        /// <summary>
        /// Theme rendering state for the current circuit
        /// </summary>
        [Inject]
        private Radzen.ThemeService _themeService { get; set; }

        /// <summary>Scoped service for native confirmations even without an active lease</summary>
        [Inject]
        private DialogService _dialogService { get; set; }

        #endregion

        #region Constants

        /// <summary>
        /// Maximum file size for editor (5 MB)
        /// </summary>
        private const long MAX_EDITOR_SIZE = 5L * 1024 * 1024;

        /// <summary>
        /// Maximum interval between characters in incremental entry search
        /// </summary>
        private const int TYPE_SEARCH_TIMEOUT_MS = 1000;

        /// <summary>
        /// Page jump size used until the browser reports the visible row count
        /// </summary>
        private const int DEFAULT_PAGE_SIZE = 20;

        /// <summary>
        /// Maximum number of destinations retained in each navigation direction
        /// </summary>
        private const int MAX_HISTORY_COUNT = 128;

        #endregion

        #region Class Variables

        /// <summary>
        /// Left panel state
        /// </summary>
        private PanelState _leftPanel = new PanelState();

        /// <summary>
        /// Right panel state
        /// </summary>
        private PanelState _rightPanel = new PanelState();

        /// <summary>
        /// Active panel index (0 = left, 1 = right)
        /// </summary>
        private int _activePanel = 0;

        /// <summary>
        /// Internal clipboard for copy/cut operations
        /// </summary>
        private ClipboardState _clipboard = new ClipboardState();

        /// <summary>Authoritative desktop projection, available even before the JS mount</summary>
        private BiviumWorkspaceSnapshot _desktopWorkspace;

        /// <summary>
        /// Context menu visibility
        /// </summary>
        private bool _contextMenuVisible = false;

        /// <summary>
        /// Context menu X coordinate
        /// </summary>
        private double _contextMenuX = 0;

        /// <summary>
        /// Context menu Y coordinate
        /// </summary>
        private double _contextMenuY = 0;

        /// <summary>
        /// Reference to confirm dialog component
        /// </summary>
        private ConfirmDialog _confirmDialog;

        /// <summary>Server-owned question about closing the editor with unsaved changes</summary>
        private EditorCloseDialog _editorCloseDialog;

        /// <summary>
        /// Reference to overwrite dialog component
        /// </summary>
        private OverwriteDialog _overwriteDialog;

        /// <summary>
        /// Reference to input dialog component
        /// </summary>
        private InputDialog _inputDialog;

        /// <summary>Authorized projection of the workflow runtime, never a continuation of the local command</summary>
        private WorkspaceWorkflowSnapshot _workspaceWorkflow;

        /// <summary>Upload projection read only by the browser that owns the lease</summary>
        private WorkspaceUploadSnapshot _workspaceUpload;
        private Guid _observedCompletedUploadId;
        /// <summary>Error of the migrated form workflows, recoverable without legacy callbacks</summary>
        private WorkspaceFormFailureDialog _formFailureDialog;
        private WorkspaceTerminalDialog _terminalWorkflowDialog;
        /// <summary>Last result reconciled in the adapter, without re-running the operation</summary>
        private Guid _observedCompletedOperationId;

        /// <summary>
        /// Reference to properties dialog component
        /// </summary>
        private PropertiesDialog _propertiesDialog;

        /// <summary>
        /// Reference to permissions dialog component
        /// </summary>
        private PermissionsDialog _permissionsDialog;

        /// <summary>
        /// Reference to about dialog component
        /// </summary>
        private AboutDialog _aboutDialog;

        /// <summary>
        /// Reference to editor dialog component
        /// </summary>
        private EditorDialog _editorDialog;

        /// <summary>
        /// Reference to upload dialog component
        /// </summary>
        private UploadDialog _uploadDialog;

        /// <summary>
        /// Reference to compress dialog component
        /// </summary>
        private CompressDialog _compressDialog;

        /// <summary>
        /// Reference to settings dialog component
        /// </summary>
        private SettingsDialog _settingsDialog;

        /// <summary>
        /// Reference to default creation permissions dialog component
        /// </summary>
        private CreationPermissionsDialog _creationPermissionsDialog;

        /// <summary>
        /// Reference to authentication settings dialog component
        /// </summary>
        private AuthSettingsDialog _authSettingsDialog;

        /// <summary>
        /// Reference to renamer dialog component
        /// </summary>
        private RenamerDialog _renamerDialog;

        /// <summary>
        /// Reference to terminal panel component
        /// </summary>
        private TerminalPanel _terminalPanel;

        /// <summary>Lifecycle of the takeover and of the drain, distinct from file operations</summary>
        private readonly CancellationTokenSource _handoffLifetimeCancellation = new CancellationTokenSource();

        /// <summary>Attempt owned by the current adapter</summary>
        private Guid _handoffDrainId;

        /// <summary>Bounded wait for the drain, also cancelled on a snapshot that invalidates the request</summary>
        private CancellationTokenSource _handoffDrainCancellation;

        /// <summary>Prevents duplicate requests from the same button during the wait</summary>
        private bool _takeoverInProgress;

        /// <summary>Active identity received from the single JS stack</summary>
        private string _activeWindowId = "";

        /// <summary>
        /// JS module reference for keyboard capture
        /// </summary>
        private IJSObjectReference _jsModule;

        /// <summary>
        /// .NET object reference for JS callbacks
        /// </summary>
        private DotNetObjectReference<Commander> _dotNetRef;

        /// <summary>
        /// Progress text displayed in the status bar during file operations
        /// </summary>
        private string _progressText = "";

        /// <summary>
        /// File rows visible in the left panel, used by page jumps
        /// </summary>
        private int _leftPageSize = DEFAULT_PAGE_SIZE;

        /// <summary>
        /// File rows visible in the right panel, used by page jumps
        /// </summary>
        private int _rightPageSize = DEFAULT_PAGE_SIZE;

        /// <summary>
        /// Whether the context menu cursor is on a directory
        /// </summary>
        private bool _contextMenuIsDirectory = false;

        /// <summary>
        /// Whether the context menu cursor is on an archive file
        /// </summary>
        private bool _contextMenuIsArchive = false;

        /// <summary>
        /// Base name of the archive file for "Extract to" display
        /// </summary>
        private string _contextMenuArchiveBaseName = "";

        /// <summary>
        /// Whether there are selected items for compression
        /// </summary>
        private bool _contextMenuHasSelection = false;

        /// <summary>
        /// Whether multiple items are selected (multi-selection)
        /// </summary>
        private bool _contextMenuIsMultiSelection = false;

        /// <summary>
        /// Whether the cursor file has an editable extension (for Monaco editor)
        /// </summary>
        private bool _contextMenuIsEditable = false;

        /// <summary>
        /// Single panel mode (hides right panel)
        /// </summary>
        private bool _singlePanelMode = false;

        /// <summary>
        /// Persisted percentage occupied by the left panel in the Radzen layout
        /// </summary>
        private double _outerPanelSizePercent = 50;

        /// <summary>
        /// Index of the collapsed panel in the Radzen layout, or -1
        /// </summary>
        private int _collapsedPanelIndex = -1;

        /// <summary>
        /// Narrow viewport: in dual mode only the active panel is shown
        /// </summary>
        private bool _compactLayout;

        /// <summary>
        /// Flag to scroll cursor into view after next render
        /// </summary>
        private bool _scrollAfterRender = false;

        /// <summary>
        /// Whether authentication state has been checked
        /// </summary>
        private bool _authReady = false;

        /// <summary>
        /// Whether the current user can access the commander
        /// </summary>
        private bool _canAccess = false;

        /// <summary>
        /// Current authentication status
        /// </summary>
        private AuthenticationStatus _authStatus = new AuthenticationStatus();

        /// <summary>
        /// Settings change subscription used to refresh authentication state
        /// </summary>
        private IDisposable _settingsChangeSubscription;

        /// <summary>
        /// Whether file panels have been initialized
        /// </summary>
        private bool _panelsInitialized = false;

        /// <summary>
        /// Whether client interop initialization has started
        /// </summary>
        private bool _clientInteropInitializationStarted = false;

        /// <summary>
        /// Whether the unload presence listener owns the current workspace token
        /// </summary>
        private bool _presenceInitialized = false;

        /// <summary>
        /// Prefix accumulated from recent character keys
        /// </summary>
        private string _typeSearchPrefix = "";

        /// <summary>
        /// Time of the last character added to incremental search
        /// </summary>
        private DateTime _typeSearchLastInputUtc = DateTime.MinValue;

        /// <summary>
        /// Panel owning the current incremental search
        /// </summary>
        private int _typeSearchPanelIndex = -1;

        /// <summary>
        /// Directory owning the current incremental search
        /// </summary>
        private string _typeSearchPath = "";

        /// <summary>
        /// Workspace revision currently rendered by this circuit
        /// </summary>
        private long _workspaceRevision = 0;

        /// <summary>
        /// Unpredictable identifier of the current attachment
        /// </summary>
        private string _attachmentId = "";

        /// <summary>
        /// Lease generation observed by the circuit
        /// </summary>
        private long _leaseGeneration = 0;

        /// <summary>
        /// Whether the circuit is registered in the workspace
        /// </summary>
        private bool _clientAttached = false;

        /// <summary>
        /// Whether the circuit owns mutation control
        /// </summary>
        private bool _hasActiveLease = false;

        /// <summary>
        /// Active lease shown in the challenge
        /// </summary>
        private ActiveClientLeaseSnapshot _observedLease;

        /// <summary>
        /// Subscription to revocations and takeovers
        /// </summary>
        private IDisposable _workspaceChangeSubscription;

        /// <summary>
        /// Client heartbeat cancellation
        /// </summary>
        private CancellationTokenRegistration _leaseRevocationRegistration;

        /// <summary>
        /// Whether the component is detaching
        /// </summary>
        private bool _isDisposed = false;

        /// <summary>Local confirmation of the takeover by the non-owner browser, not transferred into A's workflow</summary>
        private RadzenDialogLifetime _workspaceConfirmationLifetime;

        /// <summary>
        /// Theme selected by Commander
        /// </summary>
        private string _currentTheme = RadzenThemeCatalog.DEFAULT_THEME;

        /// <summary>Availability of the keyboard context only, communicated by the browser</summary>
        private bool _keyboardGeneral, _keyboardControl, _keyboardNavigation, _keyboardPanelSwitch, _keyboardTerminal;

        /// <summary>Blocking DOM modal, distinct from modeless windows and popup menus</summary>
        private bool _keyboardModal;

        #endregion

        #region Overrides

        /// <summary>
        /// Initializes panels with the user home directory
        /// </summary>
        protected override async System.Threading.Tasks.Task OnInitializedAsync()
        {
            this._settingsChangeSubscription = this._settings.OnChange(this.HandleSettingsChanged);
            this._currentTheme = RadzenThemeCatalog.NormalizeOrDefault(this._settings.CurrentValue.DefaultTheme);
            this._themeService.SetTheme(this._currentTheme);
            await this.RefreshAuthenticationState();
        }

        /// <summary>
        /// Initializes browser lifecycle and input interop after the interactive render
        /// </summary>
        /// <param name="firstRender">Whether this is the first interactive render</param>
        protected override async System.Threading.Tasks.Task OnAfterRenderAsync(bool firstRender)
        {
            if (!this._clientInteropInitializationStarted && this._canAccess)
            {
                this._clientInteropInitializationStarted = true;
                await this.InitializeClientInteropAsync();
            }

            // Scroll cursor into view after DOM is ready
            if (this._scrollAfterRender)
            {
                this._scrollAfterRender = false;
                if (this._jsModule != null)
                    await this._jsModule.InvokeVoidAsync("scrollCursorIntoView", this.GetActivePanel().CursorIndex);
            }

            this.PersistWorkspacePanels();
        }

        #endregion

        #region Authentication

        /// <summary>
        /// Refreshes authentication status and access state
        /// </summary>
        private async System.Threading.Tasks.Task RefreshAuthenticationState()
        {
            AuthenticationState state = await this._authenticationStateProvider.GetAuthenticationStateAsync();
            this._authStatus = this._authenticationService.GetStatus(state.User);
            this._canAccess = this._authenticationService.CanAccess(state.User);
            this._authReady = true;
            if (this._canAccess)
            {
                this.EnsureClientAttachment();
                if (this._hasActiveLease)
                    this.InitializePanels();
            }
        }

        /// <summary>
        /// Refreshes authentication state after appsettings.json changes
        /// </summary>
        /// <param name="settings">Updated settings</param>
        /// <param name="name">Options name</param>
        private void HandleSettingsChanged(CommanderSettings settings, string name)
        {
            _ = this.InvokeAsync(async () =>
            {
                this._currentTheme = RadzenThemeCatalog.NormalizeOrDefault(settings.DefaultTheme);
                this._themeService.SetTheme(this._currentTheme);
                await this.RefreshAuthenticationState();
                this.StateHasChanged();
            });
        }

        #endregion

        #region Panel Navigation

        /// <summary>
        /// Registers the circuit and acquires or requests the exclusive lease
        /// </summary>
        private void EnsureClientAttachment()
        {
            if (this._clientAttached)
                return;

            HttpContext context = this._httpContextAccessor.HttpContext;
            string remoteIp = context?.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            string userAgent = context?.Request.Headers.UserAgent.ToString() ?? "Browser";
            WorkspaceAttachResult result = this._workspaceService.AttachClient(remoteIp, userAgent);
            this._attachmentId = result.AttachmentId;
            this._clientAttached = true;
            BiviumWorkspaceSnapshot workspace;
            this._workspaceChangeSubscription = this._workspaceService.SubscribeAttachment(this._attachmentId, this.HandleWorkspaceLeaseChanged, out workspace);
            this.ApplyAttachResult(result);
            this.HandleWorkspaceLeaseChanged(workspace);
        }

        /// <summary>
        /// Applies a result of attach or takeover
        /// </summary>
        private void ApplyAttachResult(WorkspaceAttachResult result)
        {
            this._hasActiveLease = result != null && result.HasControl;
            this._observedLease = result?.ActiveLease;
            this._leaseGeneration = this._hasActiveLease && result.ActiveLease != null ? result.ActiveLease.Generation : 0;
            if (result != null)
                this._workspaceRevision = Math.Max(this._workspaceRevision, result.WorkspaceRevision);
            this.ConfigureLeaseRevocation();
        }

        /// <summary>
        /// Connects the circuit to the server-side token revoked atomically by takeover
        /// </summary>
        private void ConfigureLeaseRevocation()
        {
            this._leaseRevocationRegistration.Dispose();
            if (!this._hasActiveLease)
                return;
            CancellationToken revocationToken = this._workspaceService.GetRevocationToken(this.GetClientToken());
            this._leaseRevocationRegistration = revocationToken.Register(() =>
            {
                if (this._isDisposed)
                    return;
                _ = this.InvokeAsync(async () =>
                {
                    if (this._isDisposed)
                        return;
                    this._hasActiveLease = false;
                    this._leaseGeneration = 0;
                    this._panelsInitialized = false;
                    await this.UpdateWorkspacePresenceLeaseAsync();
                    this.StateHasChanged();
                });
            });
        }

        /// <summary>
        /// Shows confirmation and attempts takeover with generation compare-and-swap
        /// </summary>
        private async System.Threading.Tasks.Task RequestWorkspaceTakeover()
        {
            if (!this._clientAttached || this._takeoverInProgress)
                return;

            string activeIp = this._observedLease?.RemoteIp ?? "unknown";
            string confirmationMessage = "Bivium is already open from IP " + activeIp + ". Make this session active?";
            bool confirmed = await this.ConfirmWorkspaceActionAsync("Activate this session", confirmationMessage, "Activate");
            if (!confirmed || this._isDisposed || !this._clientAttached)
                return;

            long expectedGeneration = this._observedLease?.Generation ?? 0;
            this._takeoverInProgress = true;
            this.StateHasChanged();
            try
            {
                WorkspaceAttachResult result = await this._workspaceService.TryTakeoverAsync(this._attachmentId, expectedGeneration, this._handoffLifetimeCancellation.Token);
                if (this._isDisposed)
                    return;
                // A later notification may have already transferred the lease again
                BiviumWorkspaceSnapshot current = this._workspaceService.GetSnapshot();
                result.ActiveLease = current.ActiveClientLease;
                result.WorkspaceRevision = current.Revision;
                result.HasControl = current.ActiveClientLease?.AttachmentId == this._attachmentId && current.ActiveClientLease.Connected;
                this.ApplyAttachResult(result);
                if (!string.IsNullOrEmpty(result.ErrorMessage))
                    this.NotifyWarning("Activate this session", result.ErrorMessage);
                await this.UpdateWorkspacePresenceLeaseAsync();
                if (this._hasActiveLease)
                    this.InitializePanels();
            }
            finally
            {
                this._takeoverInProgress = false;
                if (!this._isDisposed)
                    this.StateHasChanged();
            }
        }

        /// <summary>
        /// Reacts immediately to revocation, detach or takeover
        /// </summary>
        private void HandleWorkspaceLeaseChanged(BiviumWorkspaceSnapshot workspace)
        {
            if (this._isDisposed || workspace == null)
                return;
            _ = this.InvokeAsync(async () =>
            {
                if (this._isDisposed || (this._desktopWorkspace != null && workspace.Revision < this._desktopWorkspace.Revision))
                    return;
                ActiveClientLeaseSnapshot lease = workspace.ActiveClientLease;
                bool hasControl = lease != null && lease.Connected && lease.AttachmentId == this._attachmentId;
                bool changed = hasControl != this._hasActiveLease || (hasControl && lease.Generation != this._leaseGeneration);
                this._observedLease = lease;
                this._workspaceRevision = Math.Max(this._workspaceRevision, workspace.Revision);
                bool workspaceReset = workspace.Panels == null && this._desktopWorkspace?.Panels != null;
                if (workspaceReset)
                    this._panelsInitialized = false;
                this._desktopWorkspace = workspace;
                if (this._handoffDrainId != Guid.Empty && workspace.Handoff?.Id != this._handoffDrainId)
                    this._handoffDrainCancellation?.Cancel();
                this._clipboard = new ClipboardState { Paths = new List<string>(workspace.Desktop.ClipboardPaths), IsCut = workspace.Desktop.ClipboardIsCut };
                if (changed)
                {
                    this._hasActiveLease = hasControl;
                    this._leaseGeneration = hasControl ? lease.Generation : 0;
                    this.ConfigureLeaseRevocation();
                    if (!hasControl)
                    {
                        this._panelsInitialized = false;
                    }
                    await this.UpdateWorkspacePresenceLeaseAsync();
                    if (this._hasActiveLease)
                        this.InitializePanels();
                }
                if (workspaceReset && hasControl && !changed && this.CanMutateWorkspace())
                {
                    try { this.InitializePanels(); }
                    catch (UnauthorizedAccessException) { /* A concurrent takeover or freeze leaves the initialization to the new owner */ }
                }
                this.RefreshWorkspaceWorkflow();
                this.StateHasChanged();
                if (hasControl && workspace.Handoff != null && workspace.Handoff.OwnerAttachmentId == this._attachmentId && this._handoffDrainId == Guid.Empty)
                    await this.ProcessWorkspaceHandoffAsync(workspace.Handoff);
            });
        }

        /// <summary>Coordinates freeze and publisher; non-migrated workflows are only a transitional guard</summary>
        /// <param name="handoff">Observed server-owned request</param>
        /// <returns>Bounded drain without transferring credentials, uploads or local workflows</returns>
        private async Task ProcessWorkspaceHandoffAsync(WorkspaceHandoffSnapshot handoff)
        {
            WorkspaceClientToken token = this.GetClientToken();
            if (this._isDisposed || this._handoffDrainId != Guid.Empty)
                return;
            if (this.HasNonTransferableHandoffWork())
            {
                this._workspaceService.RejectHandoff(token, handoff.Id, "The active browser has a workflow or operation that is not transferable yet. Finish it, then retry activation.");
                return;
            }
            this._handoffDrainId = handoff.Id;
            using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(this._handoffLifetimeCancellation.Token);
            this._handoffDrainCancellation = cancellation;
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1, (handoff.DeadlineUtc - DateTime.UtcNow).TotalMilliseconds)));
            string id = handoff.Id.ToString();
            try
            {
                string workflowId = this._workspaceWorkflow?.IsActive == true ? this._workspaceWorkflow.Id.ToString() : this._workspaceUpload?.Visible == true ? this._workspaceUpload.Id.ToString() : "";
                if (this._jsModule == null || !await this._jsModule.InvokeAsync<bool>("beginWorkspaceHandoff", cancellation.Token, id, Math.Max(1, (handoff.DeadlineUtc - DateTime.UtcNow).TotalMilliseconds), workflowId))
                    throw new InvalidOperationException("The active desktop could not be frozen safely. Retry activation.");
                this.StateHasChanged();
                if (!await this._jsModule.InvokeAsync<bool>("flushDesktopPublications", cancellation.Token, id))
                    throw new InvalidOperationException("The active desktop publishers did not confirm their state. Retry activation.");
                if (this.HasNonTransferableHandoffWork())
                    throw new InvalidOperationException("The active browser started a workflow that is not transferable yet. Finish it, then retry activation.");
                if (this._workspaceUpload?.Visible == true && (this._uploadDialog == null || !await this._uploadDialog.FlushForHandoffAsync(cancellation.Token)))
                    throw new InvalidOperationException("The upload checkpoint was not confirmed. Retry activation.");
                if (!this._workspaceService.TryConfirmHandoffFreeze(token, handoff.Id))
                    return;
                if (this._workspaceWorkflow?.Phase is WorkspaceWorkflowPhase.AwaitingInput or WorkspaceWorkflowPhase.AwaitingConfirmation or WorkspaceWorkflowPhase.AwaitingOverwrite or WorkspaceWorkflowPhase.Failed or WorkspaceWorkflowPhase.Cancelled || (this._workspaceWorkflow?.Kind == WorkspaceWorkflowKind.Properties && this._workspaceWorkflow.Phase == WorkspaceWorkflowPhase.Running))
                {
                    bool workflowAcknowledged = this._workspaceWorkflow.Kind switch
                    {
                        _ when BiviumWorkspaceService.IsFormKind(this._workspaceWorkflow.Kind) && (this._workspaceWorkflow.Phase == WorkspaceWorkflowPhase.Cancelled || (this._workspaceWorkflow.Phase == WorkspaceWorkflowPhase.Failed && !BiviumWorkspaceService.IsEditableFormKind(this._workspaceWorkflow.Kind))) => this._formFailureDialog != null && await this._formFailureDialog.FlushForHandoffAsync(cancellation.Token),
                        WorkspaceWorkflowKind.EditorExtensions => this._settingsDialog != null && await this._settingsDialog.FlushForHandoffAsync(cancellation.Token),
                        WorkspaceWorkflowKind.CreationPermissions => this._creationPermissionsDialog != null && await this._creationPermissionsDialog.FlushForHandoffAsync(cancellation.Token),
                        WorkspaceWorkflowKind.Permissions => this._permissionsDialog != null && await this._permissionsDialog.FlushForHandoffAsync(cancellation.Token),
                        WorkspaceWorkflowKind.Compress => this._compressDialog != null && await this._compressDialog.FlushForHandoffAsync(cancellation.Token),
                        WorkspaceWorkflowKind.Properties => this._propertiesDialog != null && await this._propertiesDialog.FlushForHandoffAsync(cancellation.Token),
                        WorkspaceWorkflowKind.About => this._aboutDialog != null && await this._aboutDialog.FlushForHandoffAsync(cancellation.Token),
                        WorkspaceWorkflowKind.Authentication => this._authSettingsDialog != null && await this._authSettingsDialog.FlushForHandoffAsync(cancellation.Token),
                        WorkspaceWorkflowKind.TerminalRename or WorkspaceWorkflowKind.TerminalClose or WorkspaceWorkflowKind.TerminalClipboard => this._terminalWorkflowDialog != null && await this._terminalWorkflowDialog.FlushForHandoffAsync(cancellation.Token),
                        WorkspaceWorkflowKind.DeleteEntries or WorkspaceWorkflowKind.EditorAlert or WorkspaceWorkflowKind.ResetWorkspace => this._confirmDialog != null && await this._confirmDialog.FlushForHandoffAsync(cancellation.Token),
                        WorkspaceWorkflowKind.EditorClose => this._editorCloseDialog != null && await this._editorCloseDialog.FlushForHandoffAsync(cancellation.Token),
                        WorkspaceWorkflowKind.CopyEntries or WorkspaceWorkflowKind.MoveEntries or WorkspaceWorkflowKind.TransferEntries => this._overwriteDialog != null && await this._overwriteDialog.FlushForHandoffAsync(cancellation.Token),
                        WorkspaceWorkflowKind.BatchRename => this._renamerDialog != null && await this._renamerDialog.FlushForHandoffAsync(cancellation.Token),
                        _ => this._inputDialog != null && await this._inputDialog.FlushForHandoffAsync(cancellation.Token)
                    };
                    if (!workflowAcknowledged)
                        throw new InvalidOperationException("The workflow state was not acknowledged. Retry activation.");
                }
                if (this._editorDialog == null || !await this._editorDialog.FlushForHandoffAsync(cancellation.Token)
                    || this._renamerDialog == null || !await this._renamerDialog.FlushForHandoffAsync(cancellation.Token)
                    || this._terminalPanel == null || !await this._terminalPanel.FlushForHandoffAsync(cancellation.Token))
                    throw new InvalidOperationException("A desktop window is still initializing or has a non-transferable workflow. Retry activation.");
                if (!await this._jsModule.InvokeAsync<bool>("flushDesktopPublications", cancellation.Token, id))
                    throw new InvalidOperationException("The final desktop publications were not confirmed. Retry activation.");
                cancellation.Token.ThrowIfCancellationRequested();
                if (this.HasNonTransferableHandoffWork())
                    throw new InvalidOperationException("A non-transferable workflow is pending. Finish it, then retry activation.");
                WorkspacePanelsSnapshot panels = new WorkspacePanelsSnapshot(this.CreatePanelSnapshot(this._leftPanel), this.CreatePanelSnapshot(this._rightPanel), this._activePanel, this._singlePanelMode, this._outerPanelSizePercent, this._collapsedPanelIndex);
                BiviumWorkspaceSnapshot observed = this._workspaceService.GetSnapshot();
                if (!this._workspaceService.TryUpdatePanels(token, observed.Revision, panels, out _))
                    throw new InvalidOperationException("Desktop state changed during the final checkpoint. Retry activation.");
                // The clipboard is already server-owned: do not republish the UI copy after a move completes
                WorkspaceHandoffStamp stamp = this._workspaceService.GetHandoffStamp(token, handoff.Id);
                if (!this._workspaceService.TryCompleteHandoff(token, handoff.Id, stamp))
                    throw new InvalidOperationException("Desktop revisions changed before activation. Retry activation.");
            }
            catch (Exception ex) when (ex is JSException || ex is OperationCanceledException || ex is ObjectDisposedException || ex is InvalidOperationException || ex is UnauthorizedAccessException)
            {
                this._workspaceService.RejectHandoff(token, handoff.Id, ex is InvalidOperationException ? ex.Message : "The active browser did not confirm the handoff. Retry activation.");
            }
            finally
            {
                // The begin may have frozen the DOM even if its ack arrived after the cancellation
                if (this._jsModule != null)
                {
                    try
                    {
                        using CancellationTokenSource cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                        await this._jsModule.InvokeVoidAsync("endWorkspaceHandoff", cleanup.Token, id, !this._isDisposed && this._workspaceService.ValidatePublication(token));
                    }
                    catch (Exception ex) when (ex is JSException || ex is OperationCanceledException || ex is ObjectDisposedException)
                    {
                    }
                }
                // Keeps the adapter ownership until the JS cleanup: C must not start against freeze B
                if (this._handoffDrainId == handoff.Id)
                {
                    this._handoffDrainId = Guid.Empty;
                    this._handoffDrainCancellation = null;
                }
                // A reset confirmed before the freeze may cancel the attempt while the drain is busy.
                // Only after the cleanup, the still-valid owner initializes the panels already emptied by the server.
                if (!this._isDisposed && !this._panelsInitialized && this._hasActiveLease && this._workspaceService.GetSnapshot().Panels == null && this.CanMutateWorkspace())
                {
                    try { this.InitializePanels(); }
                    catch (UnauthorizedAccessException) { /* The new owner will initialize the reset snapshot */ }
                }
                if (!this._isDisposed)
                    this.StateHasChanged();
                // A C request may have been notified while drain B occupied the adapter
                if (!this._isDisposed && this._handoffDrainId == Guid.Empty)
                {
                    BiviumWorkspaceSnapshot current = this._workspaceService.GetSnapshot();
                    WorkspaceHandoffSnapshot next = current.Handoff;
                    if (next != null && next.Id != handoff.Id && next.OwnerAttachmentId == this._attachmentId && current.ActiveClientLease?.AttachmentId == this._attachmentId && next.Generation == this._leaseGeneration && next.DeadlineUtc > DateTime.UtcNow)
                        _ = this.InvokeAsync(() => this.ProcessWorkspaceHandoffAsync(next));
                }
            }
        }

        /// <summary>Technical guards to remove one by one after the workflow migration</summary>
        /// <returns>True when the attempt must preserve A without revocation</returns>
        private bool HasNonTransferableHandoffWork()
        {
            bool migratedQuestion = this._workspaceWorkflow?.IsActive == true || this._workspaceUpload?.Visible == true;
            return this._workspaceConfirmationLifetime != null || (this._keyboardModal && !migratedQuestion)
                || this._editorDialog?.HasNonTransferableWork == true || this._terminalPanel?.HasNonTransferableWork == true || this._authSettingsDialog?.HasNonTransferableWork == true || this._terminalWorkflowDialog?.HasNonTransferableWork == true;
        }

        /// <summary>
        /// Receives a browser heartbeat and automatically reacquires an available lease
        /// </summary>
        [JSInvokable]
        public async System.Threading.Tasks.Task OnWorkspaceHeartbeat()
        {
            if (this._isDisposed || !this._clientAttached)
                return;

            if (this._hasActiveLease && this._workspaceService.Heartbeat(this.GetClientToken()))
                return;

            WorkspaceAttachResult result = this._workspaceService.TryReconnectClient(this._attachmentId);
            this.ApplyAttachResult(result);
            await this.UpdateWorkspacePresenceLeaseAsync();
            if (this._hasActiveLease)
                this.InitializePanels();
            this.StateHasChanged();
        }

        /// <summary>
        /// Revokes control when the browser reports leaving the page
        /// </summary>
        [JSInvokable]
        public void OnWorkspaceDisconnected()
        {
            if (this._isDisposed || !this._clientAttached || !this._hasActiveLease)
                return;

            WorkspaceClientToken token = this.GetClientToken();
            this._workspaceService.MarkClientDisconnected(token);
            this._hasActiveLease = false;
            this._leaseGeneration = 0;
            this._panelsInitialized = false;
        }

        /// <summary>
        /// Returns the current mutation token
        /// </summary>
        private WorkspaceClientToken GetClientToken()
        {
            return new WorkspaceClientToken(this._attachmentId, this._leaseGeneration);
        }

        /// <summary>
        /// Updates the unload notification token owned by the active presence registration
        /// </summary>
        private async System.Threading.Tasks.Task UpdateWorkspacePresenceLeaseAsync()
        {
            if (this._jsModule == null || this._isDisposed)
                return;

            try
            {
                await this._jsModule.InvokeVoidAsync("updateWorkspacePresenceLease", this._attachmentId, this._leaseGeneration);
            }
            catch (Exception ex) when (ex is JSDisconnectedException || ex is OperationCanceledException || ex is ObjectDisposedException)
            {
            }
        }

        /// <summary>
        /// Revalidates the lease in the server process immediately before a mutation
        /// </summary>
        /// <returns>True if the circuit still owns the workspace</returns>
        private bool CanMutateWorkspace()
        {
            if (this._handoffDrainId != Guid.Empty && this._workspaceService.ValidatePublication(this.GetClientToken()))
                return false;
            bool result = this._hasActiveLease && this._workspaceService.ValidateMutation(this.GetClientToken());
            if (!result && !this._workspaceService.ValidatePublication(this.GetClientToken()))
            {
                this._hasActiveLease = false;
                this._leaseGeneration = 0;
            }

            return result;
        }

        /// <summary>Allows only the persistence callbacks during the freeze, not new commands</summary>
        /// <returns>Authority of the current publisher</returns>
        private bool CanPublishWorkspace() => !this._isDisposed && this._hasActiveLease && this._workspaceService.ValidatePublication(this.GetClientToken());

        /// <summary>
        /// Initializes the panels when the lease grants access
        /// </summary>
        private void InitializePanels()
        {
            if (this._panelsInitialized)
                return;

            string homePath = this.GetHomePath();
            WorkspacePanelSnapshot defaultPanel = new WorkspacePanelSnapshot(homePath, "", 0, new List<string>(), SortField.Name, SortDirection.Ascending);
            WorkspacePanelsSnapshot defaultPanels = new WorkspacePanelsSnapshot(defaultPanel, defaultPanel, 0, false);
            BiviumWorkspaceSnapshot workspace = this._workspaceService.InitializePanels(this.GetClientToken(), defaultPanels);
            this.ApplyWorkspacePanels(workspace, homePath);
            this._panelsInitialized = true;
        }

        /// <summary>
        /// Applies authoritative panel state to the current circuit
        /// </summary>
        /// <param name="workspace">Authoritative workspace snapshot</param>
        /// <param name="homePath">Fallback directory</param>
        private void ApplyWorkspacePanels(BiviumWorkspaceSnapshot workspace, string homePath)
        {
            if (workspace == null || workspace.Panels == null)
                return;

            bool leftFallback;
            bool rightFallback;
            this._leftPanel = this.RestorePanel(workspace.Panels.LeftPanel, homePath, out leftFallback);
            this._rightPanel = this.RestorePanel(workspace.Panels.RightPanel, homePath, out rightFallback);
            this._singlePanelMode = workspace.Panels.SinglePanelMode;
            this._outerPanelSizePercent = this.ClampOuterPanelSize(workspace.Panels.OuterSizePercent);
            this._collapsedPanelIndex = workspace.Panels.CollapsedPanelIndex is 0 or 1 ? workspace.Panels.CollapsedPanelIndex : -1;
            this._activePanel = this._singlePanelMode ? 0 : Math.Clamp(workspace.Panels.ActivePanel, 0, 1);
            if (!this._singlePanelMode && this._collapsedPanelIndex == this._activePanel)
                this._activePanel = this._activePanel == 0 ? 1 : 0;
            this._workspaceRevision = workspace.Revision;
            this._desktopWorkspace = workspace;
            this._clipboard = new ClipboardState { Paths = new List<string>(workspace.Desktop.ClipboardPaths), IsCut = workspace.Desktop.ClipboardIsCut };
            this.RefreshWorkspaceWorkflow();
            this.HydrateContextMenu();

            if (leftFallback || rightFallback)
            {
                this._progressText = "A saved panel path is unavailable. Opened the home directory instead.";
                this.NotifyWarning("Workspace restored", this._progressText);
            }
        }

        /// <summary>
        /// Rebuilds a panel while revalidating paths, selection and cursor
        /// </summary>
        /// <param name="snapshot">Persistent panel snapshot</param>
        /// <param name="homePath">Fallback directory</param>
        /// <param name="usedFallback">Whether the fallback directory was used</param>
        /// <returns>Reconciled runtime state</returns>
        private PanelState RestorePanel(WorkspacePanelSnapshot snapshot, string homePath, out bool usedFallback)
        {
            // Revalidate the persisted path before rebuilding filesystem-dependent state
            string path = snapshot == null ? "" : snapshot.CurrentPath;
            usedFallback = !this.IsRestorableDirectory(path);
            if (usedFallback)
                path = homePath;

            PanelState result = new PanelState(path);
            this.ConfigurePanelCallbacks(result);
            if (snapshot != null)
            {
                result.CurrentSort = new SortColumn(snapshot.SortField, snapshot.SortDirection);
                result.ScrollAnchorPath = snapshot.ScrollAnchorPath;
                result.NameColumnRatio = snapshot.NameColumnRatio;
                result.SizeColumnRatio = snapshot.SizeColumnRatio;
                result.DateColumnRatio = snapshot.DateColumnRatio;
                result.AttributesColumnRatio = snapshot.AttributesColumnRatio;
                result.OwnerColumnRatio = snapshot.OwnerColumnRatio;
                result.BackHistory = this.RestoreHistory(snapshot.BackHistory, path);
                result.ForwardHistory = this.RestoreHistory(snapshot.ForwardHistory, path);
                result.TreeSizePercent = this.ClampTreeSize(snapshot.TreeSizePercent);
                result.TreeCollapsed = snapshot.TreeCollapsed;
                for (int i = 0; i < snapshot.Columns.Count; i++)
                {
                    FileListColumnSnapshot column = snapshot.Columns[i];
                    result.Columns.Add(new FileListColumnState(column.Id, column.Width, column.Visible));
                }
                for (int i = 0; i < snapshot.ExpandedDirectoryPaths.Count; i++)
                {
                    if (this.IsRestorableDirectory(snapshot.ExpandedDirectoryPaths[i]))
                        result.ExpandedDirectoryPaths.Add(snapshot.ExpandedDirectoryPaths[i]);
                }
            }

            this.LoadPanelContents(result);
            if (snapshot == null)
                return result;

            // Reconcile selection and cursor by path because indices are not stable across listings
            StringComparer pathComparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            HashSet<string> availablePaths = new HashSet<string>(pathComparer);
            for (int i = 0; i < result.Entries.Count; i++)
            {
                availablePaths.Add(result.Entries[i].FullPath);
            }

            for (int i = 0; i < snapshot.SelectedPaths.Count; i++)
            {
                if (availablePaths.Contains(snapshot.SelectedPaths[i]))
                    result.SelectedPaths.Add(snapshot.SelectedPaths[i]);
            }

            int cursorIndex = -1;
            if (!string.IsNullOrEmpty(snapshot.FocusedPath))
            {
                for (int i = 0; i < result.Entries.Count; i++)
                {
                    if (pathComparer.Equals(result.Entries[i].FullPath, snapshot.FocusedPath))
                    {
                        cursorIndex = i;
                        break;
                    }
                }
            }

            if (cursorIndex < 0 && result.Entries.Count > 0)
                cursorIndex = Math.Clamp(snapshot.CursorIndex, 0, result.Entries.Count - 1);

            result.CursorIndex = cursorIndex < 0 ? 0 : cursorIndex;
            if (result.Entries.Count > 0)
                result.FocusedPath = result.Entries[result.CursorIndex].FullPath;
            if (!string.IsNullOrEmpty(snapshot.SelectionAnchorPath) && availablePaths.Contains(snapshot.SelectionAnchorPath))
                result.SelectionAnchorPath = snapshot.SelectionAnchorPath;
            if (!availablePaths.Contains(result.ScrollAnchorPath))
                result.ScrollAnchorPath = result.Entries.Count == 0 ? "" : result.Entries[result.CursorIndex].FullPath;
            return result;
        }

        /// <summary>
        /// Attaches Commander-owned reducers to runtime-only panel callbacks
        /// </summary>
        /// <param name="panel">Panel receiving callbacks</param>
        private void ConfigurePanelCallbacks(PanelState panel)
        {
            panel.InteractionRequested = request => this.HandleFileListInteraction(panel, request);
            panel.ColumnLayoutRequested = columns => this.HandleColumnLayoutRequested(panel, columns);
        }

        /// <summary>
        /// Restores safe existing history entries without duplicates or the current path
        /// </summary>
        /// <param name="paths">Persisted paths</param>
        /// <param name="currentPath">Current panel path</param>
        /// <returns>Filtered history with the nearest destination last</returns>
        private List<string> RestoreHistory(IReadOnlyList<string> paths, string currentPath)
        {
            List<string> result = new List<string>();
            if (paths == null)
                return result;

            int start = Math.Max(0, paths.Count - MAX_HISTORY_COUNT);
            for (int i = start; i < paths.Count; i++)
            {
                string path = paths[i];
                if (this.IsRestorableDirectory(path) && !this.AreSamePath(path, currentPath))
                    this.AddHistoryPath(result, path);
            }
            return result;
        }

        /// <summary>
        /// Saves persistent panel state when it changes
        /// </summary>
        private void PersistWorkspacePanels()
        {
            if (!this._panelsInitialized)
                return;

            WorkspacePanelsSnapshot panels = new WorkspacePanelsSnapshot(this.CreatePanelSnapshot(this._leftPanel), this.CreatePanelSnapshot(this._rightPanel), this._activePanel, this._singlePanelMode, this._outerPanelSizePercent, this._collapsedPanelIndex);
            BiviumWorkspaceSnapshot workspace;
            bool updated;
            try
            {
                // The first attempt uses the revision from which the circuit's visible state derives
                updated = this._workspaceService.TryUpdatePanels(this.GetClientToken(), this._workspaceRevision, panels, out workspace);
            }
            catch (Exception ex) when (ex is ObjectDisposedException || ex is UnauthorizedAccessException)
            {
                return;
            }

            if (updated)
            {
                this._workspaceRevision = workspace.Revision;
            }
            else if (workspace.Panels != null)
            {
                // One retry preserves the local mutation when only another workspace area changed
                try
                {
                    updated = this._workspaceService.TryUpdatePanels(this.GetClientToken(), workspace.Revision, panels, out workspace);
                }
                catch (Exception ex) when (ex is ObjectDisposedException || ex is UnauthorizedAccessException)
                {
                    return;
                }

                if (updated)
                    this._workspaceRevision = workspace.Revision;
                else
                {
                    // Two consecutive conflicts make the latest singleton snapshot authoritative
                    this.ApplyWorkspacePanels(workspace, this.GetHomePath());
                    _ = this.InvokeAsync(this.StateHasChanged);
                }
            }
        }

        /// <summary>
        /// Creates the persistent snapshot of a runtime panel
        /// </summary>
        /// <param name="panel">Runtime panel</param>
        /// <returns>Persistent snapshot</returns>
        private WorkspacePanelSnapshot CreatePanelSnapshot(PanelState panel)
        {
            string focusedPath = panel.FocusedPath;
            if (string.IsNullOrEmpty(focusedPath) && panel.CursorIndex >= 0 && panel.CursorIndex < panel.Entries.Count)
                focusedPath = panel.Entries[panel.CursorIndex].FullPath;

            List<string> expandedPaths = new List<string>(panel.ExpandedDirectoryPaths);
            expandedPaths.Sort(StringComparer.Ordinal);
            return new WorkspacePanelSnapshot(panel.CurrentPath, focusedPath, panel.CursorIndex, panel.SelectedPaths, panel.CurrentSort.Field, panel.CurrentSort.Direction, panel.ScrollAnchorPath, expandedPaths, panel.NameColumnRatio, panel.SizeColumnRatio, panel.DateColumnRatio, panel.AttributesColumnRatio, panel.OwnerColumnRatio, panel.SelectionAnchorPath, panel.BackHistory, panel.ForwardHistory, panel.Columns, panel.TreeSizePercent, panel.TreeCollapsed);
        }

        /// <summary>
        /// Returns the configured usable home directory
        /// </summary>
        /// <returns>Home directory</returns>
        private string GetHomePath()
        {
            string result = Environment.GetEnvironmentVariable("BIVIUM_HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!this.IsRestorableDirectory(result))
                result = Environment.CurrentDirectory;

            return result;
        }

        /// <summary>
        /// Validates whether a saved directory can be restored
        /// </summary>
        /// <param name="path">Path to verify</param>
        /// <returns>True when the path is safe and exists</returns>
        private bool IsRestorableDirectory(string path)
        {
            bool result = false;

            try
            {
                result = this._securityService.IsPathSafe(path) && Directory.Exists(path);
            }
            catch (Exception)
            {
                result = false;
            }

            return result;
        }

        /// <summary>
        /// Returns the currently active panel state
        /// </summary>
        /// <returns>Active panel state</returns>
        private PanelState GetActivePanel()
        {
            PanelState result = this._activePanel == 0 ? this._leftPanel : this._rightPanel;
            return result;
        }

        /// <summary>
        /// Sets the active panel
        /// </summary>
        /// <param name="index">Panel index (0=left, 1=right)</param>
        private void SetActivePanel(int index)
        {
            if (this.IsCommandWorkflowAvailable() && this.CanMutateWorkspace())
                this._activePanel = Math.Clamp(index, 0, 1);
        }

        /// <summary>
        /// Updates the responsive projection when the Radzen media query changes
        /// </summary>
        /// <param name="matches">True when the viewport is narrow</param>
        private void HandleCompactLayoutChanged(bool matches)
        {
            this._compactLayout = matches;
        }

        /// <summary>
        /// Toggles the visible panel in the responsive projection only
        /// </summary>
        private void SwitchResponsivePanel()
        {
            if (this.IsCommandWorkflowAvailable() && this.CanMutateWorkspace() && !this._singlePanelMode)
                this._activePanel = this._activePanel == 0 ? 1 : 0;
        }

        /// <summary>
        /// Navigates the active panel to a new directory
        /// </summary>
        /// <param name="path">New directory path</param>
        private void NavigateActivePanel(string path)
        {
            this.NavigatePanel(this.GetActivePanel(), path, PanelNavigationKind.Ordinary);
        }

        /// <summary>
        /// Handles navigation in the left panel
        /// </summary>
        /// <param name="path">New directory path</param>
        private void HandleLeftNavigate(string path)
        {
            this.NavigatePanel(this._leftPanel, path, PanelNavigationKind.Ordinary);
        }

        /// <summary>
        /// Handles navigation in the right panel
        /// </summary>
        /// <param name="path">New directory path</param>
        private void HandleRightNavigate(string path)
        {
            this.NavigatePanel(this._rightPanel, path, PanelNavigationKind.Ordinary);
        }

        /// <summary>
        /// Centralizes validated directory navigation and history transfer
        /// </summary>
        /// <param name="panel">Panel to navigate</param>
        /// <param name="path">Requested destination for ordinary navigation</param>
        /// <param name="kind">Navigation direction</param>
        /// <returns>True when navigation completed</returns>
        private bool NavigatePanel(PanelState panel, string path, PanelNavigationKind kind)
        {
            if (!this.IsCommandWorkflowAvailable() || !this.CanMutateWorkspace())
                return false;

            string destination = path;
            int historyIndex = -1;
            List<string> sourceHistory = kind == PanelNavigationKind.Back ? panel.BackHistory : panel.ForwardHistory;

            if (kind != PanelNavigationKind.Ordinary)
            {
                for (int i = sourceHistory.Count - 1; i >= 0; i--)
                {
                    if (this.IsRestorableDirectory(sourceHistory[i]) && !this.AreSamePath(sourceHistory[i], panel.CurrentPath))
                    {
                        destination = sourceHistory[i];
                        historyIndex = i;
                        break;
                    }
                }
                if (historyIndex < 0)
                {
                    sourceHistory.Clear();
                    return false;
                }
            }

            if (!this.IsRestorableDirectory(destination) || this.AreSamePath(destination, panel.CurrentPath))
                return false;

            if (kind == PanelNavigationKind.Ordinary)
            {
                this.AddHistoryPath(panel.BackHistory, panel.CurrentPath);
                panel.ForwardHistory.Clear();
            }
            else
            {
                sourceHistory.RemoveRange(historyIndex, sourceHistory.Count - historyIndex);
                List<string> destinationHistory = kind == PanelNavigationKind.Back ? panel.ForwardHistory : panel.BackHistory;
                this.AddHistoryPath(destinationHistory, panel.CurrentPath);
            }

            panel.CurrentPath = destination;
            panel.FocusedPath = "";
            panel.CursorIndex = 0;
            panel.ScrollAnchorPath = "";
            panel.SelectedPaths.Clear();
            panel.SelectionAnchorPath = "";
            this.LoadPanelContents(panel);
            return true;
        }

        /// <summary>
        /// Adds one unique history destination and enforces the bounded stack size
        /// </summary>
        /// <param name="history">History stack</param>
        /// <param name="path">Path becoming the nearest destination</param>
        private void AddHistoryPath(List<string> history, string path)
        {
            for (int i = history.Count - 1; i >= 0; i--)
            {
                if (this.AreSamePath(history[i], path))
                    history.RemoveAt(i);
            }
            history.Add(path);
            if (history.Count > MAX_HISTORY_COUNT)
                history.RemoveRange(0, history.Count - MAX_HISTORY_COUNT);
        }



        /// <summary>
        /// Handles sort change in the left panel
        /// </summary>
        /// <param name="sort">New sort configuration</param>
        private void HandleLeftSortChanged(SortColumn sort)
        {
            if (!this.CanMutateWorkspace())
                return;
            this._leftPanel.CurrentSort = sort;
            this.SortEntries(this._leftPanel);
        }

        /// <summary>
        /// Handles sort change in the right panel
        /// </summary>
        /// <param name="sort">New sort configuration</param>
        private void HandleRightSortChanged(SortColumn sort)
        {
            if (!this.CanMutateWorkspace())
                return;
            this._rightPanel.CurrentSort = sort;
            this.SortEntries(this._rightPanel);
        }



        /// <summary>
        /// Updates semantic focus from a compatibility cursor index
        /// </summary>
        private void SetPanelFocusFromIndex(PanelState panel, int index)
        {
            panel.CursorIndex = index;
            panel.FocusedPath = index >= 0 && index < panel.Entries.Count ? panel.Entries[index].FullPath : "";
        }





        /// <summary>
        /// Accepts only a complete measured layout while this circuit owns workspace mutation
        /// </summary>
        /// <param name="panel">Target panel</param>
        /// <param name="columns">Ordered complete column layout</param>
        private void HandleColumnLayoutRequested(PanelState panel, IEnumerable<FileListColumnState> columns)
        {
            if (!this.CanPublishWorkspace())
                return;

            List<FileListColumnState> source = columns == null ? new List<FileListColumnState>() : new List<FileListColumnState>(columns);
            HashSet<FileListColumnId> seen = new HashSet<FileListColumnId>();
            if (source.Count != Enum.GetValues<FileListColumnId>().Length)
                return;
            for (int i = 0; i < source.Count; i++)
            {
                FileListColumnState column = source[i];
                if (column == null || !Enum.IsDefined(column.Id) || !seen.Add(column.Id) || !double.IsFinite(column.Width) || column.Width <= 0 || (column.Id == FileListColumnId.Name && !column.Visible))
                    return;
            }

            panel.Columns.Clear();
            for (int i = 0; i < source.Count; i++)
                panel.Columns.Add(new FileListColumnState(source[i].Id, source[i].Width, source[i].Visible));
        }

        /// <summary>
        /// Persists the semantic scroll anchor of the left panel
        /// </summary>
        /// <param name="path">First visible entry path</param>
        private void HandleLeftScrollAnchorChanged(string path)
        {
            if (this.CanPublishWorkspace())
                this._leftPanel.ScrollAnchorPath = path;
        }

        /// <summary>
        /// Persists the semantic scroll anchor of the right panel
        /// </summary>
        /// <param name="path">First visible entry path</param>
        private void HandleRightScrollAnchorChanged(string path)
        {
            if (this.CanPublishWorkspace())
                this._rightPanel.ScrollAnchorPath = path;
        }

        /// <summary>
        /// Stores the measured page size of the left panel
        /// </summary>
        /// <param name="pageSize">Number of file rows visible in the panel</param>
        private void HandleLeftPageSizeChanged(int pageSize)
        {
            this._leftPageSize = pageSize;
        }

        /// <summary>
        /// Stores the measured page size of the right panel
        /// </summary>
        /// <param name="pageSize">Number of file rows visible in the panel</param>
        private void HandleRightPageSizeChanged(int pageSize)
        {
            this._rightPageSize = pageSize;
        }

        /// <summary>
        /// Returns the page jump size of the active panel
        /// </summary>
        /// <returns>Number of file rows visible in the active panel</returns>
        private int GetActivePageSize()
        {
            return this._activePanel == 0 ? this._leftPageSize : this._rightPageSize;
        }


        /// <summary>
        /// Applies a semantic change to the left tree after lease revalidation
        /// </summary>
        /// <param name="change">Change requested by the Radzen variant</param>
        private void HandleLeftTreeExpansionChanged(DirectoryTreeExpansionChange change)
        {
            this.HandleTreeExpansionChanged(this._leftPanel, change);
        }

        /// <summary>
        /// Applies a semantic change to the right tree after lease revalidation
        /// </summary>
        /// <param name="change">Change requested by the Radzen variant</param>
        private void HandleRightTreeExpansionChanged(DirectoryTreeExpansionChange change)
        {
            this.HandleTreeExpansionChanged(this._rightPanel, change);
        }

        /// <summary>
        /// Reduces an expansion change into the authoritative PanelState
        /// </summary>
        /// <param name="panel">Target panel</param>
        /// <param name="change">Requested semantic change</param>
        private void HandleTreeExpansionChanged(PanelState panel, DirectoryTreeExpansionChange change)
        {
            if (change == null || string.IsNullOrEmpty(change.Path) || !this.CanMutateWorkspace())
                return;
            if (change.Expanded)
                panel.ExpandedDirectoryPaths.Add(change.Path);
            else
                panel.ExpandedDirectoryPaths.Remove(change.Path);
        }

        /// <summary>
        /// Applies the vertical layout of the left panel
        /// </summary>
        /// <param name="change">Requested geometry</param>
        private void HandleLeftTreeLayoutChanged(PanelTreeLayoutChange change)
        {
            this.HandleTreeLayoutChanged(this._leftPanel, change);
        }

        /// <summary>
        /// Applies the vertical layout of the right panel
        /// </summary>
        /// <param name="change">Requested geometry</param>
        private void HandleRightTreeLayoutChanged(PanelTreeLayoutChange change)
        {
            this.HandleTreeLayoutChanged(this._rightPanel, change);
        }

        /// <summary>
        /// Validates and reduces a vertical layout change
        /// </summary>
        /// <param name="panel">Target panel</param>
        /// <param name="change">Requested geometry</param>
        private void HandleTreeLayoutChanged(PanelState panel, PanelTreeLayoutChange change)
        {
            if (change == null || !this.CanPublishWorkspace())
                return;
            panel.TreeSizePercent = this.ClampTreeSize(change.SizePercent);
            panel.TreeCollapsed = change.Collapsed;
        }

        /// <summary>
        /// Persists the new horizontal proportion of the panels
        /// </summary>
        /// <param name="args">Final data of the Radzen resize</param>
        private void HandleOuterSplitterResize(RadzenSplitterResizeEventArgs args)
        {
            if (!this.CanPublishWorkspace())
                return;
            double leftSize = args.PaneIndex == 0 ? args.NewSize : 100 - args.NewSize;
            this._outerPanelSizePercent = this.ClampOuterPanelSize(leftSize);
        }

        /// <summary>
        /// Persists the panel collapsed by the user
        /// </summary>
        /// <param name="args">Panel collapsed by Radzen</param>
        private void HandleOuterSplitterCollapse(RadzenSplitterEventArgs args)
        {
            if (this.CanMutateWorkspace() && args.PaneIndex is 0 or 1)
            {
                this._collapsedPanelIndex = args.PaneIndex;
                if (this._activePanel == args.PaneIndex)
                    this._activePanel = args.PaneIndex == 0 ? 1 : 0;
            }
        }

        /// <summary>
        /// Removes the collapse state when the panel is expanded
        /// </summary>
        /// <param name="args">Panel expanded by Radzen</param>
        private void HandleOuterSplitterExpand(RadzenSplitterEventArgs args)
        {
            if (this.CanMutateWorkspace() && this._collapsedPanelIndex == args.PaneIndex)
                this._collapsedPanelIndex = -1;
        }

        /// <summary>
        /// Forces the owner render after a mutation that already went through the reducer
        /// </summary>
        private void HandlePanelUiStateChanged()
        {
        }

        /// <summary>
        /// Clamps the horizontal geometry to finite and usable values
        /// </summary>
        /// <param name="value">Proposed percentage</param>
        /// <returns>Valid percentage between 20 and 80</returns>
        private double ClampOuterPanelSize(double value)
        {
            return double.IsFinite(value) ? Math.Clamp(value, 20, 80) : 50;
        }

        /// <summary>
        /// Clamps the vertical geometry to finite and usable values
        /// </summary>
        /// <param name="value">Proposed percentage</param>
        /// <returns>Valid percentage between 15 and 85</returns>
        private double ClampTreeSize(double value)
        {
            return double.IsFinite(value) ? Math.Clamp(value, 15, 85) : 30;
        }

        #endregion

        #region Panel Data

        /// <summary>
        /// Loads directory contents into a panel
        /// </summary>
        /// <param name="panel">Panel to load</param>
        private void LoadPanelContents(PanelState panel)
        {
            string focusedPath = panel.FocusedPath;
            if (string.IsNullOrEmpty(focusedPath) && panel.CursorIndex >= 0 && panel.CursorIndex < panel.Entries.Count)
                focusedPath = panel.Entries[panel.CursorIndex].FullPath;
            panel.Entries = this._fileSystemService.GetDirectoryContents(panel.CurrentPath);
            this.SortEntries(panel, focusedPath);
        }

        /// <summary>
        /// Refreshes visible panels while avoiding duplicate directory reads
        /// </summary>
        private void RefreshVisiblePanels()
        {
            if (this._singlePanelMode)
            {
                this.LoadPanelContents(this._leftPanel);
                return;
            }

            if (this._leftPanel.CurrentPath == this._rightPanel.CurrentPath)
            {
                List<FileSystemEntry> entries = this._fileSystemService.GetDirectoryContents(this._leftPanel.CurrentPath);
                string leftCursorPath = this._leftPanel.FocusedPath;
                string rightCursorPath = this._rightPanel.FocusedPath;

                this._leftPanel.Entries = new List<FileSystemEntry>(entries);
                this.SortEntries(this._leftPanel, leftCursorPath);

                this._rightPanel.Entries = new List<FileSystemEntry>(entries);
                this.SortEntries(this._rightPanel, rightCursorPath);
            }
            else
            {
                this.LoadPanelContents(this._leftPanel);
                this.LoadPanelContents(this._rightPanel);
            }
        }

        /// <summary>
        /// Sorts entries in a panel according to its sort configuration
        /// </summary>
        /// <param name="panel">Panel to sort</param>
        private void SortEntries(PanelState panel, string preservedCursorPath = null)
        {
            string cursorPath = preservedCursorPath ?? panel.FocusedPath;
            if (string.IsNullOrEmpty(cursorPath) && panel.CursorIndex >= 0 && panel.CursorIndex < panel.Entries.Count)
                cursorPath = panel.Entries[panel.CursorIndex].FullPath;

            // Always keep directories first
            List<FileSystemEntry> dirs = new List<FileSystemEntry>();
            List<FileSystemEntry> files = new List<FileSystemEntry>();

            for (int i = 0; i < panel.Entries.Count; i++)
            {
                if (panel.Entries[i].IsDirectory)
                {
                    dirs.Add(panel.Entries[i]);
                }
                else
                {
                    files.Add(panel.Entries[i]);
                }
            }

            Comparison<FileSystemEntry> comparer = this.GetComparer(panel.CurrentSort);
            dirs.Sort(comparer);
            files.Sort(comparer);

            panel.Entries.Clear();
            panel.Entries.AddRange(dirs);
            panel.Entries.AddRange(files);

            StringComparer pathComparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            HashSet<string> availablePaths = new HashSet<string>(pathComparer);
            for (int i = 0; i < panel.Entries.Count; i++)
                availablePaths.Add(panel.Entries[i].FullPath);

            panel.SelectedPaths.RemoveAll(path => !availablePaths.Contains(path));
            if (!string.IsNullOrEmpty(panel.SelectionAnchorPath) && !availablePaths.Contains(panel.SelectionAnchorPath))
                panel.SelectionAnchorPath = "";

            int cursorIndex = this.FindPanelEntryIndex(panel, cursorPath);
            if (cursorIndex >= 0)
            {
                panel.CursorIndex = cursorIndex;
                panel.FocusedPath = panel.Entries[cursorIndex].FullPath;
            }
            else if (panel.Entries.Count == 0)
            {
                panel.CursorIndex = 0;
                panel.FocusedPath = "";
            }
            else
            {
                panel.CursorIndex = Math.Clamp(panel.CursorIndex, 0, panel.Entries.Count - 1);
                panel.FocusedPath = panel.Entries[panel.CursorIndex].FullPath;
            }
        }

        /// <summary>
        /// Finds a path in the current ordered entries of a panel
        /// </summary>
        /// <param name="panel">Panel to search</param>
        /// <param name="path">Semantic full path</param>
        /// <returns>Current entry index, or -1 when absent</returns>
        private int FindPanelEntryIndex(PanelState panel, string path)
        {
            if (string.IsNullOrEmpty(path))
                return -1;

            StringComparison comparison = this.GetPathComparison();
            for (int i = 0; i < panel.Entries.Count; i++)
            {
                if (string.Equals(panel.Entries[i].FullPath, path, comparison))
                    return i;
            }

            return -1;
        }

        /// <summary>
        /// Returns the entry identified by the semantic focus, with fallback to the legacy cursor
        /// </summary>
        /// <param name="panel">Panel to consult</param>
        /// <returns>Focused entry or null if unavailable</returns>
        private FileSystemEntry GetFocusedEntry(PanelState panel)
        {
            int index = this.FindPanelEntryIndex(panel, panel.FocusedPath);
            if (index < 0 && string.IsNullOrEmpty(panel.FocusedPath))
                index = panel.CursorIndex;
            return index >= 0 && index < panel.Entries.Count ? panel.Entries[index] : null;
        }

        /// <summary>
        /// Applies an atomic request keeping selection, focus and anchor under Commander control
        /// </summary>
        /// <param name="panel">Target panel</param>
        /// <param name="request">Interaction requested by the UI surface</param>
        private void HandleFileListInteraction(PanelState panel, FileListInteractionRequest request)
        {
            if (panel == null || request == null || !this.IsCommandWorkflowAvailable() || !this.CanMutateWorkspace())
                return;

            if (request.Intent == FileListInteractionIntent.ClearSelection)
            {
                panel.SelectedPaths.Clear();
                return;
            }
            if (request.Intent == FileListInteractionIntent.SelectAll)
            {
                panel.SelectedPaths.Clear();
                for (int i = 0; i < panel.Entries.Count; i++)
                    panel.SelectedPaths.Add(panel.Entries[i].FullPath);
                return;
            }
            if (panel.Entries.Count == 0)
                return;

            int focusedIndex = this.FindPanelEntryIndex(panel, panel.FocusedPath);
            if (focusedIndex < 0)
                focusedIndex = Math.Clamp(panel.CursorIndex, 0, panel.Entries.Count - 1);
            int targetIndex;
            int pageSize = request.PageSize > 0 ? request.PageSize : DEFAULT_PAGE_SIZE;
            switch (request.Intent)
            {
                case FileListInteractionIntent.Click:
                    targetIndex = this.FindPanelEntryIndex(panel, request.TargetPath);
                    break;
                case FileListInteractionIntent.MovePrevious:
                    targetIndex = Math.Max(0, focusedIndex - 1);
                    break;
                case FileListInteractionIntent.MoveNext:
                    targetIndex = Math.Min(panel.Entries.Count - 1, focusedIndex + 1);
                    break;
                case FileListInteractionIntent.MoveFirst:
                    targetIndex = 0;
                    break;
                case FileListInteractionIntent.MoveLast:
                    targetIndex = panel.Entries.Count - 1;
                    break;
                case FileListInteractionIntent.MovePagePrevious:
                    targetIndex = Math.Max(0, focusedIndex - pageSize);
                    break;
                case FileListInteractionIntent.MovePageNext:
                    targetIndex = (int)Math.Min(panel.Entries.Count - 1L, (long)focusedIndex + pageSize);
                    break;
                case FileListInteractionIntent.ToggleFocused:
                    targetIndex = focusedIndex;
                    break;
                case FileListInteractionIntent.ActivateFocused:
                    FileSystemEntry entry = this.GetFocusedEntry(panel);
                    if (entry != null)
                    {
                        if (entry.IsDirectory)
                        {
                            this.NavigatePanel(panel, entry.FullPath, PanelNavigationKind.Ordinary);
                        }
                        else
                        {
                            this.OpenEditor(entry, true);
                        }
                    }
                    return;
                case FileListInteractionIntent.Focus:
                    targetIndex = this.FindPanelEntryIndex(panel, request.TargetPath);
                    if (targetIndex >= 0)
                        this.SetPanelFocusFromIndex(panel, targetIndex);
                    return;
                default:
                    return;
            }

            if (targetIndex < 0)
                return;

            if (request.Shift)
            {
                List<string> previousSelection = request.Control ? new List<string>(panel.SelectedPaths) : null;
                this.MoveSelectionFocus(panel, targetIndex, true);
                if (previousSelection != null)
                {
                    StringComparer comparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
                    HashSet<string> selectedPaths = new HashSet<string>(panel.SelectedPaths, comparer);
                    for (int i = 0; i < previousSelection.Count; i++)
                    {
                        if (selectedPaths.Add(previousSelection[i]))
                            panel.SelectedPaths.Add(previousSelection[i]);
                    }
                }
            }
            else if (request.Intent == FileListInteractionIntent.ToggleFocused || (request.Control && request.Intent == FileListInteractionIntent.Click))
            {
                string targetPath = panel.Entries[targetIndex].FullPath;
                int selectedIndex = panel.SelectedPaths.FindIndex(path => this.AreSamePath(path, targetPath));
                if (selectedIndex >= 0)
                {
                    panel.SelectedPaths.RemoveAt(selectedIndex);
                }
                else
                {
                    panel.SelectedPaths.Add(targetPath);
                }
                if (this.FindPanelEntryIndex(panel, panel.SelectionAnchorPath) < 0)
                    panel.SelectionAnchorPath = targetPath;
                this.SetPanelFocusFromIndex(panel, targetIndex);
            }
            else if (request.Control)
            {
                this.SetPanelFocusFromIndex(panel, targetIndex);
            }
            else
            {
                this.MoveSelectionFocus(panel, targetIndex, false);
            }
        }

        /// <summary>
        /// Returns a comparison delegate for the specified sort configuration
        /// </summary>
        /// <param name="sort">Sort configuration</param>
        /// <returns>Comparison delegate</returns>
        private Comparison<FileSystemEntry> GetComparer(SortColumn sort)
        {
            int multiplier = sort.Direction == SortDirection.Ascending ? 1 : -1;

            Comparison<FileSystemEntry> comparer;

            if (sort.Field == SortField.Size)
            {
                comparer = (a, b) => a.SizeBytes.CompareTo(b.SizeBytes) * multiplier;
            }
            else if (sort.Field == SortField.Date)
            {
                comparer = (a, b) => a.LastModified.CompareTo(b.LastModified) * multiplier;
            }
            else if (sort.Field == SortField.Attributes)
            {
                comparer = (a, b) => string.Compare(a.Attributes, b.Attributes, StringComparison.OrdinalIgnoreCase) * multiplier;
            }
            else if (sort.Field == SortField.Owner)
            {
                comparer = (a, b) => string.Compare(a.Owner, b.Owner, StringComparison.OrdinalIgnoreCase) * multiplier;
            }
            else
            {
                comparer = (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase) * multiplier;
            }

            return comparer;
        }

        #endregion

        #region Context Menu

        /// <summary>Visual opening with captured identity and sources</summary>
        private WorkspaceContextMenuDraft _contextDraft;

        /// <summary>Hydration of the opening, without re-invoking the request or recomputing flags</summary>
        private void HydrateContextMenu()
        {
            this._contextDraft = this._workspaceService.GetContextMenuDraft(this.GetClientToken());
            WorkspaceContextMenuDraft draft = this._contextDraft;
            this._contextMenuVisible = draft?.Visible == true;
            if (draft == null)
                return;
            this._contextMenuX = draft.X;
            this._contextMenuY = draft.Y;
            this._contextMenuIsDirectory = draft.IsDirectory;
            this._contextMenuIsArchive = draft.IsArchive;
            this._contextMenuIsEditable = draft.IsEditable;
            this._contextMenuHasSelection = draft.HasSelection;
            this._contextMenuIsMultiSelection = draft.IsMultiSelection;
            this._contextMenuArchiveBaseName = draft.ArchiveBaseName;
        }

        /// <summary>CAS of the position of the same opening; does not allow replacement entries or flags</summary>
        /// <param name="draft">Position captured by the portal</param>
        /// <returns>New revision or -1</returns>
        private long PublishContextVisual(WorkspaceContextMenuDraft draft)
        {
            if (this._isDisposed || this._contextDraft == null || draft.Id != this._contextDraft.Id || draft.Revision != this._contextDraft.Revision)
                return -1;
            WorkspaceContextMenuDraft current = this._contextDraft with { X = draft.X, Y = draft.Y, ViewportWidth = draft.ViewportWidth, ViewportHeight = draft.ViewportHeight, ActiveItem = draft.ActiveItem, Focused = draft.Focused };
            WorkspaceContextMenuDraft acknowledged = this._workspaceService.PublishContextMenuDraft(this.GetClientToken(), current);
            if (acknowledged == null)
                return -1;
            this._contextDraft = acknowledged;
            return acknowledged.Revision;
        }

        /// <summary>Existing commands operate only if the captured context is still the same</summary>
        /// <returns>False if panels or selection changed, without implicit retarget</returns>
        private bool CanInvokeCapturedContext()
        {
            WorkspaceContextMenuDraft draft = this._contextDraft;
            PanelState panel = this.GetActivePanel();
            string focusedPath = this.CreatePanelSnapshot(panel).FocusedPath;
            return this.CanMutateWorkspace() && draft != null && this._activePanel == draft.PanelIndex && panel.CurrentPath == draft.BasePath && focusedPath == draft.FocusedPath && panel.SelectedPaths.SequenceEqual(draft.SelectedPaths);
        }

        /// <summary>
        /// Handles context menu request from a panel
        /// </summary>
        /// <param name="args">Context menu event data</param>
        /// <param name="panelIndex">Panel index (0=left, 1=right)</param>
        private void HandleContextMenuRequest(ContextMenuEventArgs args, int panelIndex)
        {
            if (!this.IsCommandWorkflowAvailable() || !this.CanMutateWorkspace())
                return;
            // Switch focus to the panel that was right-clicked
            this._activePanel = panelIndex;

            this._contextMenuX = args.X;
            this._contextMenuY = args.Y;
            this._contextMenuVisible = true;

            // Determine context flags for menu items
            PanelState active = this.GetActivePanel();
            this._contextMenuHasSelection = active.SelectedPaths.Count > 0;
            this._contextMenuIsMultiSelection = active.SelectedPaths.Count > 1;
            this._contextMenuIsDirectory = false;
            this._contextMenuIsArchive = false;
            this._contextMenuIsEditable = false;
            this._contextMenuArchiveBaseName = "";

            List<string> selectedArchivePaths = this.GetSelectedArchivePaths(active);
            if (selectedArchivePaths.Count > 0 && selectedArchivePaths.Count == active.SelectedPaths.Count)
            {
                this._contextMenuIsArchive = true;
                if (selectedArchivePaths.Count == 1)
                {
                    this._contextMenuArchiveBaseName = this.GetArchiveBaseName(Path.GetFileName(selectedArchivePaths[0]));
                }
                else
                {
                    this._contextMenuArchiveBaseName = "*";
                }
            }

            if (args.Entry != null && active.CursorIndex >= 0 && active.CursorIndex < active.Entries.Count)
            {
                FileSystemEntry entry = active.Entries[active.CursorIndex];
                this._contextMenuIsDirectory = entry.IsDirectory;

                if (!entry.IsDirectory && selectedArchivePaths.Count == 0)
                {
                    // Check if the file extension is editable by Monaco
                    string ext = Path.GetExtension(entry.Name).ToLowerInvariant();
                    List<string> editableExtensions = this._settings.CurrentValue.EditableExtensions;
                    for (int i = 0; i < editableExtensions.Count; i++)
                    {
                        if (editableExtensions[i].ToLowerInvariant() == ext)
                        {
                            this._contextMenuIsEditable = true;
                            break;
                        }
                    }

                    // Check if it's an archive
                    this._contextMenuIsArchive = this._archiveService.IsArchive(entry.FullPath);
                    if (this._contextMenuIsArchive)
                    {
                        this._contextMenuArchiveBaseName = this.GetArchiveBaseName(entry.Name);
                    }
                }
            }

            this.PersistWorkspacePanels();
            WorkspaceContextMenuDraft previous = this._workspaceService.GetContextMenuDraft(this.GetClientToken());
            string focusedPath = this.CreatePanelSnapshot(active).FocusedPath;
            ImmutableArray<WorkspaceContextEntry> entries = active.Entries.Where(entry => active.SelectedPaths.Contains(entry.FullPath) || entry.FullPath == focusedPath).Select(entry => new WorkspaceContextEntry(entry.FullPath, entry.Name, entry.IsDirectory)).ToImmutableArray();
            WorkspaceContextMenuDraft draft = new WorkspaceContextMenuDraft(previous?.Revision ?? 0, Guid.NewGuid(), true, panelIndex, active.CurrentPath, focusedPath, active.SelectedPaths.ToImmutableArray(), entries, args.X, args.Y, this._contextMenuIsDirectory, this._contextMenuIsArchive, this._contextMenuIsEditable, this._contextMenuHasSelection, this._contextMenuIsMultiSelection, this._contextMenuArchiveBaseName);
            this._contextDraft = this._workspaceService.PublishContextMenuDraft(this.GetClientToken(), draft);
            this._contextMenuVisible = this._contextDraft != null;
        }

        /// <summary>
        /// Closes the context menu
        /// </summary>
        private void CloseContextMenu()
        {
            // The synthetic close of the freeze must not cancel the captured opening
            if (!this.CanMutateWorkspace())
                return;
            if (this._contextDraft != null)
            {
                WorkspaceContextMenuDraft closed = this._workspaceService.PublishContextMenuDraft(this.GetClientToken(), this._contextDraft with { Visible = false });
                if (closed == null)
                    return;
                this._contextDraft = closed;
            }
            this._contextMenuVisible = false;
        }

        #endregion

        #region File Operations

        /// <summary>Projects only open UI windows, including minimized ones</summary>
        /// <returns>Transient taskbar state</returns>
        private IReadOnlyList<DesktopWindowState> GetOpenWindows()
        {
            List<DesktopWindowState> result = new List<DesktopWindowState>();
            BiviumWorkspaceSnapshot workspace = this._desktopWorkspace ?? this._workspaceService.GetSnapshot();
            FloatingWindowsSnapshot windows = workspace.FloatingWindows;
            string activeWindowId = this.GetActiveDesktopWindowId();
            if (windows.Terminal.Visible || windows.Terminal.Minimized)
                result.Add(new DesktopWindowState("terminal-window", this._terminalPanel?.GetTitle() ?? "Terminal", "terminal", windows.Terminal.Minimized, windows.Terminal.Visible && activeWindowId == "terminal-window", this._terminalPanel?.NeedsAttention() ?? false));
            if (workspace.Desktop.EditorId != Guid.Empty)
                result.Add(new DesktopWindowState("editor-window", workspace.Desktop.EditorTitle, "edit", windows.Editor.Minimized, windows.Editor.Visible && activeWindowId == "editor-window", false));
            if (workspace.Desktop.RenamerId != Guid.Empty)
                result.Add(new DesktopWindowState("renamer-window", "Advanced Rename", "drive_file_rename_outline", windows.Renamer.Minimized, windows.Renamer.Visible && activeWindowId == "renamer-window", false));
            return result;
        }

        /// <summary>Projects the saved stacking, without depending on the component mount order</summary>
        /// <returns>Visible window with the most recent MRU order</returns>
        private string GetActiveDesktopWindowId()
        {
            FloatingWindowsSnapshot windows = (this._desktopWorkspace ?? this._workspaceService.GetSnapshot()).FloatingWindows;
            string id = "";
            long order = -1;
            if (windows.Terminal.Visible)
            {
                id = "terminal-window";
                order = windows.Terminal.MruOrder;
            }
            if (windows.Editor.Visible && windows.Editor.MruOrder >= order)
            {
                id = "editor-window";
                order = windows.Editor.MruOrder;
            }
            if (windows.Renamer.Visible && windows.Renamer.MruOrder >= order)
                id = "renamer-window";
            return id;
        }

        /// <summary>Pure availability of the activation, even during file operations</summary>
        /// <returns>True without accessing the workspace service</returns>
        private bool CanActivateWindows()
        {
            return this._canAccess && this._hasActiveLease && this._panelsInitialized && !this._isDisposed
                && !this._keyboardModal && this._workspaceConfirmationLifetime == null;
        }

        /// <summary>Revalidates the lease and activates an existing UI session</summary>
        /// <param name="id">Stable window identity</param>
        private async Task RestoreWindowAsync(string id)
        {
            if (!this.CanActivateWindows() || !this.CanMutateWorkspace())
                return;
            switch (id)
            {
                case "terminal-window":
                    if (this._terminalPanel != null)
                        await this._terminalPanel.RestoreAsync();
                    break;
                case "editor-window":
                    if (this._editorDialog != null)
                        await this._editorDialog.RestoreAsync();
                    break;
                case "renamer-window":
                    if (this._renamerDialog != null)
                        await this._renamerDialog.RestoreAsync();
                    break;
            }
        }

        /// <summary>Updates the taskbar without introducing a parallel C# stack</summary>
        private void HandleDesktopWindowStateChanged()
        {
            if (!this._isDisposed)
                this.StateHasChanged();
        }

        /// <summary>Pure availability of the workflow, independent of the modeless focus</summary>
        /// <returns>True if a new workflow can start</returns>
        private bool IsCommandWorkflowAvailable()
        {
            return this._canAccess && this._hasActiveLease && this._panelsInitialized && !this._isDisposed
                && this._workspaceWorkflow?.IsActive != true && this._workspaceUpload?.Visible != true && this._desktopWorkspace?.Operation?.IsRunning != true && this._workspaceConfirmationLifetime == null && !this._keyboardModal;
        }

        /// <summary>Builds the single shared projection using only in-memory state</summary>
        /// <returns>Availability of existing actions and shortcuts</returns>
        private IReadOnlyList<CommanderCommandState> GetCommandStates()
        {
            PanelState active = this.GetActivePanel();
            List<string> paths = this.GetSelectedOrCursorPaths(active);
            FileSystemEntry single = paths.Count == 1 ? this.FindEntryByPath(active, paths[0]) : null;
            FileSystemEntry focused = this.GetFocusedEntry(active);
            StringComparer pathComparer = this.GetPathComparison() == StringComparison.OrdinalIgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            HashSet<string> targetPaths = new HashSet<string>(paths, pathComparer);
            HashSet<string> availablePaths = new HashSet<string>(pathComparer);
            bool hasSelectedFile = false;
            bool allTargetsArchives = true;
            foreach (FileSystemEntry entry in active.Entries)
            {
                availablePaths.Add(entry.FullPath);
                if (targetPaths.Contains(entry.FullPath))
                {
                    if (!entry.IsDirectory)
                        hasSelectedFile = true;
                    if (entry.IsDirectory || !this._archiveService.IsArchive(entry.FullPath))
                        allTargetsArchives = false;
                }
            }
            bool ready = this._canAccess && this._hasActiveLease && this._panelsInitialized && !this._isDisposed;
            bool idle = this.IsCommandWorkflowAvailable();
            bool blockingConfirmation = this._workspaceWorkflow?.IsActive == true && this._workspaceWorkflow.Kind is WorkspaceWorkflowKind.EditorAlert or WorkspaceWorkflowKind.ResetWorkspace;
            bool target = paths.Count > 0 && paths.All(availablePaths.Contains);
            bool editable = this.IsEditableEntry(single);
            bool renameTargets = single != null || (paths.Count > 1 && hasSelectedFile);
            bool editorVisible = this._editorDialog?.IsOpen() == true;
            bool renamerVisible = this._renamerDialog?.IsOpen() == true;
            List<CommanderCommandState> result = new List<CommanderCommandState>();

            void Add(string id, string label, string shortcut, bool enabled)
            {
                bool context = shortcut == "F12" ? this._keyboardTerminal
                    : shortcut == "Tab" ? this._keyboardPanelSwitch
                    : shortcut == "Enter" || shortcut == "Alt+Enter" || shortcut == "Delete" ? this._keyboardNavigation
                    : shortcut.StartsWith("Ctrl+", StringComparison.Ordinal) ? this._keyboardControl
                    : this._keyboardGeneral;
                result.Add(new CommanderCommandState(id, label, shortcut, enabled, enabled && context && !string.IsNullOrEmpty(shortcut)));
            }

            Add("new-file", "New File", "Shift+F4", idle && !string.IsNullOrEmpty(active.CurrentPath));
            Add("new-folder", "New Folder", "F7", idle && !string.IsNullOrEmpty(active.CurrentPath));
            Add("copy", "Copy", "Ctrl+C", idle && target);
            Add("cut", "Cut", "Ctrl+X", idle && target);
            Add("paste", "Paste", "Ctrl+V", idle && this._clipboard.HasEntries() && !string.IsNullOrEmpty(active.CurrentPath));
            Add("delete", "Delete", "Delete", idle && target);
            Add("rename", "Rename", "F2", idle && single != null);
            Add("advanced-rename", "Advanced Rename...", "Ctrl+F2", idle && target && renameTargets && !renamerVisible);
            Add("edit", "Edit", "F4", idle && editable && !editorVisible);
            Add("permissions", "Permissions", "Ctrl+P", idle && single != null);
            Add("properties", "Properties", "Alt+Enter", idle && single != null);
            Add("download", paths.Count > 1 ? "Download as ZIP" : "Download", "", idle && target);
            Add("upload", "Upload", "", idle && !string.IsNullOrEmpty(active.CurrentPath));
            Add("compress", "Compress to...", "", idle && target);
            Add("extract", "Extract Here", "", idle && target && allTargetsArchives);
            Add("select-all", "Select All", "Ctrl+A", idle && active.Entries.Count > 0);
            Add("refresh", "Refresh", "F5", idle && !string.IsNullOrEmpty(active.CurrentPath));
            Add("panels", this._singlePanelMode ? "Dual Panel" : "Single Panel", "Ctrl+O", idle);
            Add("switch-panel", "Switch panel", "Tab", idle && !this._singlePanelMode);
            Add("open", "Open", "Enter", idle && focused != null && (focused.IsDirectory || (!editorVisible && this.IsEditableEntry(focused))));
            Add("parent", "Parent folder", "Backspace", idle && !string.IsNullOrEmpty(active.CurrentPath) && Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(active.CurrentPath)) != null);
            Add("context-menu", "Context menu", "Shift+F10", idle && focused != null);
            Add("terminal", "Terminal", "F12", ready && !blockingConfirmation && this._workspaceConfirmationLifetime == null && !this._keyboardModal);
            Add("theme", "Theme", "", idle);
            Add("editor-extensions", "Editor Extensions...", "", idle);
            Add("creation-permissions", "Default Permissions...", "", idle);
            Add("authentication", "Authentication...", "", idle && this._authStatus.CanManageSettings);
            Add("logout", "Logout", "", idle && this._authStatus.Authenticated);
            Add("reset", "Reset Workspace...", "", idle);
            Add("about", "About", "Ctrl+?", idle);
            return result;
        }

        /// <summary>Applies to dispatches the same rule as the projection and revalidates the lease</summary>
        /// <param name="id">Requested command</param>
        /// <returns>True only if executable now</returns>
        private bool CanExecuteCommand(string id)
        {
            return this.GetCommandStates().Any(command => command.Id == id && command.Enabled) && this.CanMutateWorkspace();
        }

        /// <summary>Checks editor extension and size without reading the filesystem</summary>
        /// <param name="entry">Already loaded entry</param>
        /// <returns>True if compatible with the existing editor policy</returns>
        private bool IsEditableEntry(FileSystemEntry entry)
        {
            return entry != null && !entry.IsDirectory && entry.SizeBytes <= MAX_EDITOR_SIZE
                && this._settings.CurrentValue.EditableExtensions.Any(extension => string.Equals(extension, Path.GetExtension(entry.Name), StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Opens About through the same entry used by the keyboard</summary>
        private void DoAbout()
        {
            if (!this.CanExecuteCommand("about"))
                return;
            System.Reflection.Assembly assembly = System.Reflection.Assembly.GetExecutingAssembly();
            string version = assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false).OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? assembly.GetName().Version?.ToString() ?? "";
            WorkspaceAboutDraft draft = new WorkspaceAboutDraft(version, System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, System.Runtime.InteropServices.RuntimeInformation.OSDescription);
            this._workspaceWorkflow = this._workspaceService.BeginFormWorkflow(this.GetClientToken(), WorkspaceWorkflowKind.About, new WorkspaceWorkflowInvocation("", "", this._activePanel, "About", "", -1), JsonSerializer.Serialize(draft));
        }

        /// <summary>
        /// Activates the focused entry using Explorer directory/editor behavior
        /// </summary>
        private void DoOpen()
        {
            if (!this.CanExecuteCommand("open"))
                return;
            PanelState active = this.GetActivePanel();
            FileSystemEntry entry = this.GetFocusedEntry(active);
            if (entry == null)
                return;
            if (entry.IsDirectory)
            {
                this.NavigateActivePanel(entry.FullPath);
            }
            else
            {
                this.OpenEditor(entry, true);
            }
        }

        /// <summary>
        /// Opens the selected file in the Monaco editor
        /// </summary>
        private void DoEdit()
        {
            if (!this.CanExecuteCommand("edit"))
                return;
            PanelState active = this.GetActivePanel();
            FileSystemEntry entry = this.GetSingleTargetEntry(active, "Edit");
            if (entry != null)
                this.OpenEditor(entry, false);
        }

        /// <summary>
        /// Opens an editable file while optionally suppressing activation policy messages
        /// </summary>
        /// <param name="entry">File entry to open</param>
        /// <param name="silentPolicyRejection">Whether unsupported and oversized files silently no-op</param>
        private void OpenEditor(FileSystemEntry entry, bool silentPolicyRejection)
        {
            if (this._editorDialog?.IsOpen() == true || !this.IsCommandWorkflowAvailable() || !this.CanMutateWorkspace())
                return;
            if (entry == null || entry.IsDirectory)
                return;

            string extension = Path.GetExtension(entry.Name).ToLowerInvariant();
            List<string> editableExtensions = this._settings.CurrentValue.EditableExtensions;
            bool isEditable = false;
            for (int i = 0; i < editableExtensions.Count; i++)
            {
                if (editableExtensions[i].ToLowerInvariant() == extension)
                {
                    isEditable = true;
                    break;
                }
            }

            if (!isEditable)
            {
                if (!silentPolicyRejection)
                {
                    this.BeginWorkspaceConfirmation(WorkspaceWorkflowKind.EditorAlert, "Edit", "Extension '" + extension + "' is not in the editable extensions list.");
                }
                return;
            }
            if (entry.SizeBytes > MAX_EDITOR_SIZE)
            {
                if (!silentPolicyRejection)
                {
                    this.BeginWorkspaceConfirmation(WorkspaceWorkflowKind.EditorAlert, "Edit", "File is too large to edit (max 5 MB).");
                }
                return;
            }

            FileTextResult readResult = this._fileOperationService.ReadFileText(entry.FullPath, MAX_EDITOR_SIZE);
            if (readResult.Success)
            {
                this._editorDialog.Show(entry.FullPath, readResult.Content);
            }
            else
            {
                this.BeginWorkspaceConfirmation(WorkspaceWorkflowKind.EditorAlert, "Edit", readResult.ErrorMessage);
            }
        }

        /// <summary>
        /// Copies selected entries to internal clipboard
        /// </summary>
        private void DoCopy()
        {
            if (!this.CanExecuteCommand("copy"))
                return;
            PanelState active = this.GetActivePanel();
            this._clipboard.Paths = this.GetSelectedOrCursorPaths(active);
            this._clipboard.IsCut = false;
            this.PersistWorkspaceClipboard();
        }

        /// <summary>
        /// Cuts selected entries to internal clipboard
        /// </summary>
        private void DoCut()
        {
            if (!this.CanExecuteCommand("cut"))
                return;
            PanelState active = this.GetActivePanel();
            this._clipboard.Paths = this.GetSelectedOrCursorPaths(active);
            this._clipboard.IsCut = true;
            this.PersistWorkspaceClipboard();
        }

        /// <summary>Internal clipboard commit; a rejection restores the authoritative state without retry</summary>
        private void PersistWorkspaceClipboard()
        {
            BiviumWorkspaceSnapshot workspace = this._workspaceService.GetSnapshot();
            if (!this._workspaceService.TryUpdateClipboard(this.GetClientToken(), workspace.Revision, this._clipboard.Paths, this._clipboard.IsCut))
            {
                workspace = this._workspaceService.GetSnapshot();
                this._clipboard = new ClipboardState { Paths = new List<string>(workspace.Desktop.ClipboardPaths), IsCut = workspace.Desktop.ClipboardIsCut };
            }
            this._desktopWorkspace = this._workspaceService.GetSnapshot();
        }

        /// <summary>
        /// Pastes from clipboard into the active panel's directory
        /// </summary>
        private void DoPaste()
        {
            if (!this.CanExecuteCommand("paste"))
                return;
            if (this._clipboard.HasEntries())
            {
                this._workspaceWorkflow = this._workspaceService.BeginPasteWorkflow(this.GetClientToken(), this.GetActivePanel().CurrentPath, this._activePanel);
                this._desktopWorkspace = this._workspaceService.GetSnapshot();
                this.StateHasChanged();
            }
        }

        /// <summary>
        /// Cancels the running file operation, keeping whatever has already been written
        /// </summary>
        private void CancelActiveOperation()
        {
            WorkspaceOperationSnapshot operation = this._workspaceService.GetSnapshot().Operation;
            if (operation?.IsRunning == true)
            {
                if (!this._workspaceService.TryCancelWorkspaceOperation(this.GetClientToken(), operation.Id, operation.Revision))
                    this.ShowOperationWarning("Cancellation not accepted", "The operation or workspace ownership changed. Review the current state before retrying.");
                return;
            }
        }

        /// <summary>
        /// Checks if two paths resolve to the same path
        /// </summary>
        /// <param name="left">First path</param>
        /// <param name="right">Second path</param>
        /// <returns>True if paths are equal</returns>
        private bool AreSamePath(string left, string right)
        {
            bool result = string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), this.GetPathComparison());
            return result;
        }

        /// <summary>
        /// Gets the appropriate path comparison for the current platform
        /// </summary>
        /// <returns>String comparison for filesystem paths</returns>
        private StringComparison GetPathComparison()
        {
            StringComparison result = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return result;
        }

        /// <summary>
        /// Selects all entries in the active panel
        /// </summary>
        private void DoSelectAll()
        {
            if (!this.CanExecuteCommand("select-all"))
                return;
            PanelState active = this.GetActivePanel();
            this.HandleFileListInteraction(active, new FileListInteractionRequest { Intent = FileListInteractionIntent.SelectAll });
        }

        /// <summary>
        /// Initiates new file creation with input dialog
        /// </summary>
        private void DoNewFile()
        {
            if (!this.CanExecuteCommand("new-file"))
                return;
            this.BeginWorkspaceInput(WorkspaceWorkflowKind.CreateFile, "", this.GetActivePanel().CurrentPath, "New File", "File name:", "", -1);
        }

        /// <summary>
        /// Initiates new folder creation with input dialog
        /// </summary>
        private void DoNewFolder()
        {
            if (!this.CanExecuteCommand("new-folder"))
                return;
            this.BeginWorkspaceInput(WorkspaceWorkflowKind.CreateDirectory, "", this.GetActivePanel().CurrentPath, "New Folder", "Folder name:", "", -1);
        }

        /// <summary>
        /// Initiates rename with input dialog
        /// </summary>
        private void DoRename()
        {
            if (!this.CanExecuteCommand("rename"))
                return;
            PanelState active = this.GetActivePanel();
            FileSystemEntry entry = this.GetSingleTargetEntry(active, "Rename");
            if (entry != null)
            {
                int selectionLength = entry.Name.Length;
                if (!entry.IsDirectory)
                {
                    string extension = Path.GetExtension(entry.Name);
                    if (extension.Length < entry.Name.Length)
                        selectionLength -= extension.Length;
                }

                this.BeginWorkspaceInput(WorkspaceWorkflowKind.RenameEntry, entry.FullPath, Path.GetDirectoryName(entry.FullPath), "Rename", "New name:", entry.Name, selectionLength);
            }
        }

        /// <summary>Invokes the server-owned workflow once with captured arguments</summary>
        /// <param name="kind">Typed command</param>
        /// <param name="sourcePath">Captured source</param>
        /// <param name="parentPath">Captured destination</param>
        /// <param name="title">Pre-existing local title</param>
        /// <param name="label">Pre-existing local label</param>
        /// <param name="draft">Initial draft</param>
        /// <param name="selectionLength">Initial selection</param>
        private void BeginWorkspaceInput(WorkspaceWorkflowKind kind, string sourcePath, string parentPath, string title, string label, string draft, int selectionLength)
        {
            WorkspaceWorkflowInvocation invocation = new WorkspaceWorkflowInvocation(sourcePath, parentPath, this._activePanel, title, label, selectionLength);
            this._workspaceWorkflow = this._workspaceService.BeginInputWorkflow(this.GetClientToken(), kind, invocation, draft);
            this._desktopWorkspace = this._workspaceService.GetSnapshot();
            this.StateHasChanged();
        }

        /// <summary>Hydrates and reconciles results; does not answer questions and does not run mutating filesystem operations</summary>
        private void RefreshWorkspaceWorkflow()
        {
            this._workspaceWorkflow = this._hasActiveLease ? this._workspaceService.GetWorkflow(this.GetClientToken()) : null;
            this._workspaceUpload = this._hasActiveLease ? this._workspaceService.GetUpload(this.GetClientToken()) : null;
            if (this._workspaceUpload?.Phase == WorkspaceUploadPhase.Succeeded && this._workspaceUpload.Id != this._observedCompletedUploadId && this._panelsInitialized && this._desktopWorkspace?.Handoff?.Frozen != true)
            {
                this._observedCompletedUploadId = this._workspaceUpload.Id;
                this.RefreshVisiblePanels();
                this.NotifySuccess("Upload completed", "Visible panels have been refreshed.");
            }
            WorkspaceOperationSnapshot operation = this._desktopWorkspace?.Operation;
            if (operation?.IsRunning == true && BiviumWorkspaceService.IsFormKind(operation.Kind))
                this._progressText = "";
            if (!this._hasActiveLease || !this._panelsInitialized || operation == null || operation.IsRunning || operation.Id == this._observedCompletedOperationId || this._desktopWorkspace.Handoff?.Frozen == true)
                return;
            this._observedCompletedOperationId = operation.Id;
            this.RefreshVisiblePanels();
            if (operation.Kind == WorkspaceWorkflowKind.DeleteEntries && this._workspaceWorkflow?.OperationId == operation.Id)
            {
                WorkspaceWorkflowInvocation invocation = this._workspaceWorkflow.InvocationParameters;
                PanelState panel = invocation.PanelIndex == 0 ? this._leftPanel : this._rightPanel;
                if (this.AreSamePath(panel.CurrentPath, invocation.ParentPath))
                {
                    panel.SelectedPaths.Clear();
                    if (panel.CursorIndex >= 0 && panel.CursorIndex < panel.Entries.Count)
                        panel.SelectedPaths.Add(panel.Entries[panel.CursorIndex].FullPath);
                }
                if (operation.Phase == WorkspaceOperationPhase.Succeeded)
                    this.NotifySuccess("Delete completed", operation.FilesProcessed + " item(s) deleted.");
                return;
            }
            if (operation.Kind is WorkspaceWorkflowKind.CopyEntries or WorkspaceWorkflowKind.MoveEntries or WorkspaceWorkflowKind.TransferEntries or WorkspaceWorkflowKind.BatchRename || BiviumWorkspaceService.IsFormKind(operation.Kind))
            {
                if (operation.Phase == WorkspaceOperationPhase.Succeeded)
                    this.NotifySuccess(operation.Kind + " completed", operation.FilesProcessed + " item(s) processed.");
                return;
            }
            if (operation.Phase == WorkspaceOperationPhase.Succeeded && this._workspaceWorkflow?.OperationId == operation.Id)
            {
                WorkspaceWorkflowInvocation invocation = this._workspaceWorkflow.InvocationParameters;
                PanelState panel = invocation.PanelIndex == 0 ? this._leftPanel : this._rightPanel;
                if (this.AreSamePath(panel.CurrentPath, invocation.ParentPath))
                {
                    for (int i = 0; i < panel.Entries.Count; i++)
                    {
                        if (panel.Entries[i].Name != this._workspaceWorkflow.Draft)
                            continue;
                        this.SetPanelFocusFromIndex(panel, i);
                        panel.SelectedPaths.Clear();
                        panel.SelectedPaths.Add(panel.Entries[i].FullPath);
                        this._scrollAfterRender = this._activePanel == invocation.PanelIndex;
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Initiates delete with confirmation dialog
        /// </summary>
        private void DoDelete()
        {
            if (!this.CanExecuteCommand("delete"))
                return;
            PanelState active = this.GetActivePanel();
            List<string> paths = this.GetSelectedOrCursorPaths(active);
            if (paths.Count > 0)
            {
                active.SelectedPaths = paths;
                WorkspaceWorkflowInvocation invocation = new WorkspaceWorkflowInvocation("", active.CurrentPath, this._activePanel, "Delete", "Delete " + paths.Count + " item(s)?", -1, paths.ToImmutableArray());
                this._workspaceWorkflow = this._workspaceService.BeginDeleteWorkflow(this.GetClientToken(), invocation);
                this._desktopWorkspace = this._workspaceService.GetSnapshot();
                this.StateHasChanged();
            }
        }

        /// <summary>
        /// Downloads the selected file or directory
        /// </summary>
        private void DoDownload()
        {
            if (!this.CanExecuteCommand("download"))
                return;
            PanelState active = this.GetActivePanel();
            List<string> paths = this.GetSelectedOrCursorPaths(active);

            if (paths.Count == 0)
            {
                return;
            }

            string url;

            if (paths.Count > 1)
            {
                List<string> queryParts = new List<string>();
                for (int i = 0; i < paths.Count; i++)
                {
                    queryParts.Add("path=" + Uri.EscapeDataString(paths[i]));
                }

                url = "/api/FileTransfer/download-zip-multi?" + string.Join("&", queryParts);
            }
            else
            {
                FileSystemEntry entry = this.GetEntryByPath(active, paths[0]);
                if (entry == null)
                {
                    return;
                }

                if (entry.IsDirectory)
                {
                    // Download directory as ZIP stream
                    url = "/api/FileTransfer/download-zip?path=" + Uri.EscapeDataString(entry.FullPath);
                }
                else
                {
                    // Download single file
                    url = "/api/FileTransfer/download?path=" + Uri.EscapeDataString(entry.FullPath);
                }
            }

            // Trigger download via JS navigation
            _ = this.JSRuntime.InvokeVoidAsync("open", url, "_blank");
        }

        /// <summary>
        /// Shows the upload dialog for the active panel directory
        /// </summary>
        private void DoUpload()
        {
            if (!this.CanExecuteCommand("upload"))
                return;
            PanelState active = this.GetActivePanel();
            this._workspaceUpload = this._workspaceService.BeginUpload(this.GetClientToken(), active.CurrentPath);
            this._desktopWorkspace = this._workspaceService.GetSnapshot();
            this.StateHasChanged();
        }

        /// <summary>
        /// Refreshes the active panel contents
        /// </summary>
        private void DoRefresh()
        {
            if (!this.CanExecuteCommand("refresh"))
                return;
            PanelState active = this.GetActivePanel();
            this.LoadPanelContents(active);
        }

        /// <summary>
        /// Toggles single/dual panel mode
        /// </summary>
        private void DoToggleSinglePanel()
        {
            if (!this.CanExecuteCommand("panels"))
                return;
            this._singlePanelMode = !this._singlePanelMode;

            // In single panel mode, ensure active panel is always left
            if (this._singlePanelMode)
                this._activePanel = 0;
        }

        /// <summary>
        /// Shows properties dialog for the selected entry
        /// </summary>
        private void DoProperties()
        {
            if (!this.CanExecuteCommand("properties"))
                return;
            PanelState active = this.GetActivePanel();
            FileSystemEntry entry = this.GetSingleTargetEntry(active, "Properties");
            if (entry != null)
            {
                this._workspaceWorkflow = this._workspaceService.BeginEntryFormWorkflow(this.GetClientToken(), WorkspaceWorkflowKind.Properties, entry, this._activePanel);
            }
        }

        /// <summary>
        /// Shows permissions dialog for the selected entry
        /// </summary>
        private void DoPermissions()
        {
            if (!this.CanExecuteCommand("permissions"))
                return;
            PanelState active = this.GetActivePanel();
            FileSystemEntry entry = this.GetSingleTargetEntry(active, "Permissions");
            if (entry != null)
            {
                this._workspaceWorkflow = this._workspaceService.BeginEntryFormWorkflow(this.GetClientToken(), WorkspaceWorkflowKind.Permissions, entry, this._activePanel);
            }
        }

        /// <summary>
        /// Toggles the terminal panel
        /// </summary>
        private async System.Threading.Tasks.Task DoToggleTerminalAsync()
        {
            if (!this.CanExecuteCommand("terminal"))
                return;
            if (this._terminalPanel != null)
            {
                await this._terminalPanel.ToggleAsync();
            }
        }

        /// <summary>
        /// Returns whether the terminal window is visible
        /// </summary>
        /// <returns>True if terminal window is visible</returns>
        private bool IsTerminalVisible()
        {
            return this._terminalPanel != null && this._terminalPanel.IsVisible();
        }

        /// <summary>
        /// Returns whether the terminal window is minimized
        /// </summary>
        /// <returns>True if terminal window is minimized</returns>
        private bool IsTerminalMinimized()
        {
            return this._terminalPanel != null && this._terminalPanel.IsMinimized();
        }

        /// <summary>
        /// Validates, persists and applies the chosen theme in the Radzen variant
        /// </summary>
        /// <param name="theme">Name of the chosen theme</param>
        private async System.Threading.Tasks.Task DoThemeChange(string theme)
        {
            if (!this.CanExecuteCommand("theme"))
                return;

            string normalizedTheme;
            if (!RadzenThemeCatalog.TryNormalize(theme, out normalizedTheme))
                return;

            if (this._jsModule == null)
            {
                this._jsModule = await this.JSRuntime.InvokeAsync<IJSObjectReference>("import", "./js/interop.js");
            }

            // Writing appsettings.json and the authentication and lease checks live only in SettingsController:
            // there is no settings service to invoke directly, so the circuit goes through the same HTTP endpoint.
            string json = JsonSerializer.Serialize(normalizedTheme);
            JsFetchResult response = await this._jsModule.InvokeAsync<JsFetchResult>("putJsonResult", "/api/Settings/theme", json, this._attachmentId, this._leaseGeneration);
            if (!response.Ok)
            {
                this._progressText = response.Status == 409 ? "Theme change rejected: this browser no longer controls the workspace." : "Unable to save theme.";
                this.NotifyError("Theme not changed", this._progressText);
                this.StateHasChanged();
                return;
            }

            if (!this.CanMutateWorkspace())
                return;

            this._currentTheme = normalizedTheme;
            this._themeService.SetTheme(normalizedTheme);
            this.NotifySuccess("Theme changed", normalizedTheme);
            this.StateHasChanged();
        }

        /// <summary>
        /// Shows the settings dialog for editing editable extensions
        /// </summary>
        private void DoEditorExtensions()
        {
            if (!this.CanExecuteCommand("editor-extensions"))
                return;
            List<string> extensions = this._settings.CurrentValue.EditableExtensions;
            this._workspaceWorkflow = this._workspaceService.BeginFormWorkflow(this.GetClientToken(), WorkspaceWorkflowKind.EditorExtensions, new WorkspaceWorkflowInvocation("", "", this._activePanel, "Editor Extensions", "", -1), string.Join("\n", extensions));
        }

        /// <summary>
        /// Shows default creation permissions settings
        /// </summary>
        private void DoCreationPermissions()
        {
            if (!this.CanExecuteCommand("creation-permissions"))
                return;
            this._workspaceWorkflow = this._workspaceService.BeginFormWorkflow(this.GetClientToken(), WorkspaceWorkflowKind.CreationPermissions, new WorkspaceWorkflowInvocation("", "", this._activePanel, "Default Creation Permissions", "", -1), JsonSerializer.Serialize(this._settings.CurrentValue.DefaultCreationPermissions ?? new DefaultCreationPermissionsSettings()));
        }

        /// <summary>
        /// Shows the authentication settings dialog
        /// </summary>
        private async System.Threading.Tasks.Task DoAuthenticationSettings()
        {
            if (!this.CanExecuteCommand("authentication"))
                return;
            await this._authSettingsDialog.Show();
        }

        /// <summary>
        /// Logs out the current administrator
        /// </summary>
        private async System.Threading.Tasks.Task DoLogout()
        {
            if (!this.CanExecuteCommand("logout"))
                return;
            if (this._jsModule == null)
            {
                this._jsModule = await this.JSRuntime.InvokeAsync<IJSObjectReference>("import", "./js/interop.js");
            }

            bool success = await this._jsModule.InvokeAsync<bool>("postJson", "/api/Auth/logout", "{}");
            if (success)
            {
                await this._jsModule.InvokeVoidAsync("reloadPage");
            }
        }

        /// <summary>
        /// Terminates every PTY and restores the workspace defaults after explicit confirmation
        /// </summary>
        private System.Threading.Tasks.Task DoResetWorkspace()
        {
            if (!this.CanExecuteCommand("reset"))
                return System.Threading.Tasks.Task.CompletedTask;
            const string message = "Reset workspace? This terminates every terminal process and clears saved panel and window state.";
            this.BeginWorkspaceConfirmation(WorkspaceWorkflowKind.ResetWorkspace, "Reset workspace", message);
            return System.Threading.Tasks.Task.CompletedTask;
        }

        /// <summary>Opens only the closed workflow; effects and answers belong to the workspace service</summary>
        /// <param name="kind">Editor alert or reset intent</param>
        /// <param name="title">Captured title</param>
        /// <param name="message">Immutable text</param>
        private void BeginWorkspaceConfirmation(WorkspaceWorkflowKind kind, string title, string message)
        {
            this._workspaceWorkflow = this._workspaceService.BeginConfirmationWorkflow(this.GetClientToken(), kind, title, message);
            this._desktopWorkspace = this._workspaceService.GetSnapshot();
            this.StateHasChanged();
        }

        /// <summary>Feedback only for the already consumed reset; does not terminate PTY and does not re-invoke reset</summary>
        private void HandleWorkspaceResetConfirmed()
        {
            if (this._isDisposed || !this.CanPublishWorkspace())
                return;
            this._progressText = "Workspace reset completed.";
            this.NotifySuccess("Workspace reset", this._progressText);
            this.StateHasChanged();
        }

        /// <summary>Waits only for the local takeover confirmation, with ownership limited to the current opening</summary>
        /// <param name="title">Dialog title</param>
        /// <param name="message">Message to confirm</param>
        /// <param name="confirmText">Label of the confirmed action</param>
        /// <returns>True only for explicit confirmation</returns>
        private async System.Threading.Tasks.Task<bool> ConfirmWorkspaceActionAsync(string title, string message, string confirmText)
        {
            if (this._isDisposed || this._workspaceConfirmationLifetime != null)
                return false;
            ConfirmOptions options = new ConfirmOptions
            {
                Width = "min(92vw, 48rem)",
                CloseDialogOnEsc = true,
                CloseDialogOnOverlayClick = true,
                AutoFocusFirstElement = true,
                OkButtonText = confirmText,
                CancelButtonText = "Cancel"
            };
            using (RadzenDialogLifetime lifetime = new RadzenDialogLifetime(this._dialogService))
            {
                this._workspaceConfirmationLifetime = lifetime;
                lifetime.Begin(options);
                try
                {
                    object result = await this._dialogService.Confirm(message, title, options);
                    lifetime.Complete(options);
                    return result is bool confirmed && confirmed;
                }
                finally
                {
                    this._workspaceConfirmationLifetime = null;
                }
            }
        }

        /// <summary>
        /// Extracts the selected archive file into the current directory
        /// </summary>
        private void DoExtract()
        {
            if (!this.CanExecuteCommand("extract"))
                return;
            PanelState active = this.GetActivePanel();
            List<string> archivePaths = this.GetArchivePathsForOperation(active);

            if (archivePaths.Count == 0)
            {
                this.ShowOperationWarning("Extract unavailable", "Selected file is not a supported archive.");
                return;
            }

            string destinationDir = active.CurrentPath;
            this.BeginWorkspaceExtraction(destinationDir, archivePaths, false);
        }

        /// <summary>
        /// Extracts archive into a subfolder named after the archive
        /// </summary>
        private void DoExtractToFolder()
        {
            if (!this.CanExecuteCommand("extract"))
                return;
            PanelState active = this.GetActivePanel();
            List<string> archivePaths = this.GetArchivePathsForOperation(active);

            if (archivePaths.Count == 0)
            {
                this.ShowOperationWarning("Extract unavailable", "Selected file is not a supported archive.");
                return;
            }

            string destinationDir = active.CurrentPath;
            this.BeginWorkspaceExtraction(destinationDir, archivePaths, true);
        }

        /// <summary>The command gesture allows the same runner without new confirmations</summary>
        private void BeginWorkspaceExtraction(string destination, List<string> paths, bool ownFolder)
        {
            WorkspaceWorkflowInvocation invocation = new WorkspaceWorkflowInvocation("", destination, this._activePanel, "Extract", "", -1, paths.ToImmutableArray(), ExtractToOwnFolder: ownFolder);
            this._workspaceWorkflow = this._workspaceService.BeginFormWorkflow(this.GetClientToken(), WorkspaceWorkflowKind.Extract, invocation, "");
        }

        /// <summary>
        /// Gets explicitly selected paths, or the cursor entry when nothing is selected
        /// </summary>
        /// <param name="active">Active panel state</param>
        /// <returns>Selected paths or cursor path</returns>
        private List<string> GetSelectedOrCursorPaths(PanelState active)
        {
            List<string> result = new List<string>(active.SelectedPaths);

            if (result.Count == 0 && active.CursorIndex >= 0 && active.CursorIndex < active.Entries.Count)
            {
                result.Add(active.Entries[active.CursorIndex].FullPath);
            }

            return result;
        }

        /// <summary>
        /// Gets a single target entry from selection or cursor
        /// </summary>
        /// <param name="active">Active panel state</param>
        /// <param name="operationName">Operation name for error dialog</param>
        /// <returns>Target entry, or null if no single target is available</returns>
        private FileSystemEntry GetSingleTargetEntry(PanelState active, string operationName)
        {
            FileSystemEntry result = null;

            if (active.SelectedPaths.Count > 1)
            {
                return result;
            }
            else if (active.SelectedPaths.Count == 1)
            {
                result = this.GetEntryByPath(active, active.SelectedPaths[0]);
            }
            else if (active.CursorIndex >= 0 && active.CursorIndex < active.Entries.Count)
            {
                result = active.Entries[active.CursorIndex];
            }

            return result;
        }

        /// <summary>
        /// Finds a visible entry by full path and syncs the cursor to it
        /// </summary>
        /// <param name="active">Active panel state</param>
        /// <param name="path">Entry path</param>
        /// <returns>File system entry, or null if not found</returns>
        private FileSystemEntry GetEntryByPath(PanelState active, string path)
        {
            FileSystemEntry result = null;

            for (int i = 0; i < active.Entries.Count; i++)
            {
                if (string.Equals(active.Entries[i].FullPath, path, this.GetPathComparison()))
                {
                    result = active.Entries[i];
                    active.CursorIndex = i;
                    break;
                }
            }

            return result;
        }

        /// <summary>
        /// Finds a visible entry by full path without changing cursor state
        /// </summary>
        /// <param name="active">Active panel state</param>
        /// <param name="path">Entry path</param>
        /// <returns>File system entry, or null if not found</returns>
        private FileSystemEntry FindEntryByPath(PanelState active, string path)
        {
            FileSystemEntry result = null;

            for (int i = 0; i < active.Entries.Count; i++)
            {
                if (string.Equals(active.Entries[i].FullPath, path, this.GetPathComparison()))
                {
                    result = active.Entries[i];
                    break;
                }
            }

            return result;
        }

        /// <summary>
        /// Gets archive paths from the current selection or cursor entry
        /// </summary>
        /// <param name="active">Active panel state</param>
        /// <returns>Archive paths to extract</returns>
        private List<string> GetArchivePathsForOperation(PanelState active)
        {
            List<string> result = this.GetSelectedArchivePaths(active);

            if (active.SelectedPaths.Count > 0)
            {
                if (result.Count != active.SelectedPaths.Count)
                {
                    result.Clear();
                }

                return result;
            }

            if (active.CursorIndex >= 0 && active.CursorIndex < active.Entries.Count)
            {
                FileSystemEntry entry = active.Entries[active.CursorIndex];
                if (!entry.IsDirectory && this._archiveService.IsArchive(entry.FullPath))
                {
                    result.Add(entry.FullPath);
                }
            }

            return result;
        }

        /// <summary>
        /// Gets archive paths from the current selection
        /// </summary>
        /// <param name="active">Active panel state</param>
        /// <returns>Selected archive paths</returns>
        private List<string> GetSelectedArchivePaths(PanelState active)
        {
            List<string> result = new List<string>();

            for (int i = 0; i < active.SelectedPaths.Count; i++)
            {
                string selectedPath = active.SelectedPaths[i];
                if (File.Exists(selectedPath) && this._archiveService.IsArchive(selectedPath))
                {
                    result.Add(selectedPath);
                }
            }

            return result;
        }

        /// <summary>
        /// Returns archive file name without single or compound archive extension
        /// </summary>
        /// <param name="fileName">Archive file name</param>
        /// <returns>Base archive name</returns>
        private string GetArchiveBaseName(string fileName)
        {
            string result = Path.GetFileNameWithoutExtension(fileName);
            string lower = fileName.ToLowerInvariant();
            string[] doubleExts = { ".tar.gz", ".tar.bz2", ".tar.xz", ".tar.zst" };

            for (int i = 0; i < doubleExts.Length; i++)
            {
                string doubleExt = doubleExts[i];
                if (lower.EndsWith(doubleExt))
                {
                    result = fileName.Substring(0, fileName.Length - doubleExt.Length);
                    break;
                }
            }

            return result;
        }

        /// <summary>
        /// Shows the compress dialog for the selected entries
        /// </summary>
        private void DoCompress()
        {
            if (!this.CanExecuteCommand("compress"))
                return;
            PanelState active = this.GetActivePanel();
            List<string> paths = this.GetSelectedOrCursorPaths(active);

            if (paths.Count == 0)
            {
                return;
            }

            active.SelectedPaths = paths;

            // Suggest a base name from selection
            string baseName = "archive";

            if (paths.Count == 1)
            {
                baseName = Path.GetFileNameWithoutExtension(paths[0]);

                // For directories, use the directory name
                if (Directory.Exists(paths[0]))
                {
                    baseName = Path.GetFileName(paths[0]);
                }
            }

            WorkspaceWorkflowInvocation invocation = new WorkspaceWorkflowInvocation("", active.CurrentPath, this._activePanel, "Compress", "", -1, paths.ToImmutableArray());
            this._workspaceWorkflow = this._workspaceService.BeginFormWorkflow(this.GetClientToken(), WorkspaceWorkflowKind.Compress, invocation, JsonSerializer.Serialize(new WorkspaceCompressDraft(ArchiveFormat.Zip, baseName + ".zip", baseName)));
        }

        /// <summary>
        /// Opens the advanced renamer dialog with selected files
        /// </summary>
        private void DoAdvancedRename()
        {
            if (!this.CanExecuteCommand("advanced-rename"))
                return;
            PanelState active = this.GetActivePanel();
            List<string> paths = this.GetSelectedOrCursorPaths(active);

            if (paths.Count == 0)
            {
                return;
            }

            active.SelectedPaths = paths;

            // Collect file entries for renaming
            List<FileSystemEntry> renameEntries = new List<FileSystemEntry>();

            if (active.SelectedPaths.Count == 1)
            {
                // Single selection: if directory, collect all files recursively
                string selectedPath = active.SelectedPaths[0];
                bool isDir = false;

                for (int i = 0; i < active.Entries.Count; i++)
                {
                    if (string.Equals(active.Entries[i].FullPath, selectedPath, this.GetPathComparison()) && active.Entries[i].IsDirectory)
                    {
                        isDir = true;
                        break;
                    }
                }

                if (isDir)
                {
                    // Recursive file collection
                    this.CollectFilesRecursively(selectedPath, renameEntries);
                }
                else
                {
                    // Single file
                    for (int i = 0; i < active.Entries.Count; i++)
                    {
                        if (string.Equals(active.Entries[i].FullPath, selectedPath, this.GetPathComparison()) && !active.Entries[i].IsDirectory)
                        {
                            renameEntries.Add(active.Entries[i]);
                            break;
                        }
                    }
                }
            }
            else
            {
                // Multiple selection: collect only files (skip directories)
                StringComparer pathComparer = this.GetPathComparison() == StringComparison.OrdinalIgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
                HashSet<string> selectedPathSet = new HashSet<string>(active.SelectedPaths, pathComparer);
                for (int i = 0; i < active.Entries.Count; i++)
                {
                    if (!active.Entries[i].IsDirectory && selectedPathSet.Contains(active.Entries[i].FullPath))
                    {
                        renameEntries.Add(active.Entries[i]);
                    }
                }
            }

            if (renameEntries.Count > 0)
            {
                this._renamerDialog.Show(renameEntries);
                this.StateHasChanged();
            }
        }

        /// <summary>
        /// Recursively collects all files from a directory
        /// </summary>
        /// <param name="directoryPath">Directory to scan</param>
        /// <param name="result">List to add file entries to</param>
        private void CollectFilesRecursively(string directoryPath, List<FileSystemEntry> result)
        {
            List<FileSystemEntry> contents = this._fileSystemService.GetDirectoryContents(directoryPath);

            for (int i = 0; i < contents.Count; i++)
            {
                if (contents[i].IsDirectory)
                {
                    // Recurse into subdirectory
                    this.CollectFilesRecursively(contents[i].FullPath, result);
                }
                else
                {
                    result.Add(contents[i]);
                }
            }
        }

        #endregion

        #region Dialog Callbacks

        /// <summary>
        /// Handles editor dialog close
        /// </summary>
        /// <param name="saved">True if file was saved</param>
        private void HandleEditorDialogClose(bool saved)
        {
            // Refresh active panel to reflect any saved changes
            if (saved && this.CanMutateWorkspace())
            {
                PanelState active = this.GetActivePanel();
                this.LoadPanelContents(active);
                this.NotifySuccess("File saved", "The active panel has been refreshed.");
            }
        }

        /// <summary>Executes on the local editor the choice consumed by the close question</summary>
        /// <param name="choice">Save or discard</param>
        private async System.Threading.Tasks.Task HandleEditorCloseResolved(string choice)
        {
            if (this._editorDialog != null)
                await this._editorDialog.CompleteCloseAsync(choice == BiviumWorkspaceService.EDITOR_CLOSE_SAVE);
        }

        /// <summary>
        /// Handles settings dialog close
        /// </summary>
        private async System.Threading.Tasks.Task HandleSettingsDialogClose()
        {
            await this.RefreshAuthenticationState();
            this.StateHasChanged();
        }

        /// <summary>
        /// Handles a completed login
        /// </summary>
        private async System.Threading.Tasks.Task HandleLoginComplete()
        {
            await this.RefreshAuthenticationState();
        }

        /// <summary>
        /// Handles renamer dialog close
        /// </summary>
        /// <param name="renamed">True if files were renamed</param>
        private void HandleRenamerDialogClose(bool renamed)
        {
            if (renamed && this.CanMutateWorkspace())
            {
                this.RefreshVisiblePanels();
                this.NotifySuccess("Rename completed", "Visible panels have been refreshed.");
                this.StateHasChanged();
            }
        }

        /// <summary>
        /// Handles terminal panel close
        /// </summary>
        private void HandleTerminalClose()
        {
            this.StateHasChanged();
        }


        #endregion

        #region Keyboard Handling

        /// <summary>Receives only DOM context capabilities, without transferring domain rules to the JS</summary>
        /// <param name="general">Forwardable ordinary keys</param>
        /// <param name="control">Forwardable Ctrl combinations</param>
        /// <param name="navigation">Forwardable keys of the file surface</param>
        /// <param name="panelSwitch">Forwardable Tab</param>
        /// <param name="terminal">Forwardable F12 with the existing precedence</param>
        /// <param name="modal">Visible blocking modal</param>
        /// <param name="activeWindowId">Highest visible modeless window in the JS stack</param>
        [JSInvokable]
        public void OnKeyboardContextChanged(bool general, bool control, bool navigation, bool panelSwitch, bool terminal, bool modal, string activeWindowId)
        {
            if (this._isDisposed)
                return;
            if (this._keyboardGeneral == general && this._keyboardControl == control && this._keyboardNavigation == navigation
                && this._keyboardPanelSwitch == panelSwitch && this._keyboardTerminal == terminal && this._keyboardModal == modal && this._activeWindowId == activeWindowId)
                return;
            this._keyboardGeneral = general;
            this._keyboardControl = control;
            this._keyboardNavigation = navigation;
            this._keyboardPanelSwitch = panelSwitch;
            this._keyboardTerminal = terminal;
            this._keyboardModal = modal;
            this._activeWindowId = activeWindowId ?? "";
            this.StateHasChanged();
        }

        /// <summary>
        /// Registers unload presence before exposing the interactive workspace
        /// </summary>
        private async System.Threading.Tasks.Task InitializeClientInteropAsync()
        {
            this._jsModule = await this.JSRuntime.InvokeAsync<IJSObjectReference>("import", "./js/interop.js");
            if (this._isDisposed)
            {
                await this._jsModule.DisposeAsync();
                this._jsModule = null;
                return;
            }
            this._dotNetRef = DotNetObjectReference.Create(this);
            await this._jsModule.InvokeVoidAsync("startWorkspacePresence", this._dotNetRef, this._attachmentId, this._leaseGeneration);
            if (this._isDisposed)
                return;
            this._presenceInitialized = true;
            this.StateHasChanged();
            await this._jsModule.InvokeVoidAsync("captureKeyboard", this._dotNetRef);
            await this._jsModule.InvokeVoidAsync("initLongPress");
        }

        /// <summary>
        /// Handles keyboard events from JS interop
        /// </summary>
        /// <param name="key">Key name</param>
        /// <param name="ctrl">Ctrl key held</param>
        /// <param name="shift">Shift key held</param>
        /// <param name="alt">Alt key held</param>
        [JSInvokable]
        public async System.Threading.Tasks.Task OnKeyDown(string key, bool ctrl, bool shift, bool alt)
        {
            if (this._isDisposed || !this._hasActiveLease || !this._workspaceService.ValidateMutation(this.GetClientToken()))
                return;

            // Ctrl+?: about dialog
            if (key == "?" && ctrl && !alt)
            {
                this.DoAbout();
                this.StateHasChanged();
                return;
            }

            // F12: toggle terminal
            if (key == "F12" && !ctrl && !shift && !alt)
            {
                await this.DoToggleTerminalAsync();
                this.StateHasChanged();
                return;
            }

            if (!this.IsCommandWorkflowAvailable())
                return;

            // Tab: switch panels (only in dual panel mode)
            if (key == "Tab" && !ctrl && !shift && !alt && !this._singlePanelMode)
            {
                this._activePanel = this._activePanel == 0 ? 1 : 0;
                this.StateHasChanged();
                return;
            }

            // Ctrl+O: toggle single/dual panel mode
            if (key == "o" && ctrl && !shift && !alt)
            {
                this.DoToggleSinglePanel();
                this.StateHasChanged();
                return;
            }

            // F2: rename
            if (key == "F2" && !ctrl && !shift && !alt)
            {
                this.DoRename();
                this.StateHasChanged();
                return;
            }

            // Ctrl+F2: advanced rename
            if (key == "F2" && ctrl && !shift && !alt)
            {
                this.DoAdvancedRename();
                this.StateHasChanged();
                return;
            }

            // F4: edit file
            if (key == "F4" && !ctrl && !shift && !alt)
            {
                this.DoEdit();
                this.StateHasChanged();
                return;
            }

            // F5: refresh
            if (key == "F5" && !ctrl && !shift && !alt)
            {
                this.DoRefresh();
                this.StateHasChanged();
                return;
            }

            // Delete: delete
            if (key == "Delete" && !ctrl && !shift && !alt)
            {
                this.DoDelete();
                this.StateHasChanged();
                return;
            }

            // Shift+F4: new file
            if (key == "F4" && !ctrl && shift && !alt)
            {
                this.DoNewFile();
                this.StateHasChanged();
                return;
            }

            // Ctrl+A: select all
            if (key == "a" && ctrl && !shift && !alt)
            {
                this.DoSelectAll();
                this.StateHasChanged();
                return;
            }

            // Ctrl+C: copy
            if (key == "c" && ctrl && !shift && !alt)
            {
                this.DoCopy();
                this.StateHasChanged();
                return;
            }

            // Ctrl+X: cut
            if (key == "x" && ctrl && !shift && !alt)
            {
                this.DoCut();
                this.StateHasChanged();
                return;
            }

            // Ctrl+V: paste
            if (key == "v" && ctrl && !shift && !alt)
            {
                this.DoPaste();
                this.StateHasChanged();
                return;
            }

            // Ctrl+P: permissions
            if (key == "p" && ctrl && !shift && !alt)
            {
                this.DoPermissions();
                this.StateHasChanged();
                return;
            }

            // F7: new folder
            if (key == "F7" && !ctrl && !shift && !alt)
            {
                this.DoNewFolder();
                this.StateHasChanged();
                return;
            }

            // Alt+Enter: properties
            if (key == "Enter" && !ctrl && !shift && alt)
            {
                this.DoProperties();
                this.StateHasChanged();
                return;
            }

            // Enter: open directory
            if (key == "Enter" && !ctrl && !shift && !alt)
            {
                this.DoOpen();
                this.StateHasChanged();
                return;
            }

            // Backspace: navigate to parent
            if (key == "Backspace" && !ctrl && !shift && !alt)
            {
                PanelState active = this.GetActivePanel();
                string parentPath = this._fileSystemService.GetParentPath(active.CurrentPath);
                if (!string.IsNullOrEmpty(parentPath))
                {
                    this.NavigateActivePanel(parentPath);
                    this.StateHasChanged();
                }
                return;
            }

            // Arrow Up: move cursor up
            if (key == "ArrowUp" && !ctrl && !alt)
            {
                PanelState active = this.GetActivePanel();
                if (active.Entries.Count > 0)
                {
                    int targetIndex = Math.Max(0, active.CursorIndex - 1);
                    this.MoveSelectionFocus(active, targetIndex, shift);
                    this.StateHasChangedAndScroll();
                }
                return;
            }

            // Arrow Down: move cursor down
            if (key == "ArrowDown" && !ctrl && !alt)
            {
                PanelState active = this.GetActivePanel();
                if (active.Entries.Count > 0)
                {
                    int targetIndex = Math.Min(active.Entries.Count - 1, active.CursorIndex + 1);
                    this.MoveSelectionFocus(active, targetIndex, shift);
                    this.StateHasChangedAndScroll();
                }
                return;
            }

            // Home: cursor to first entry
            if (key == "Home" && !ctrl && !alt)
            {
                PanelState active = this.GetActivePanel();
                if (active.Entries.Count > 0)
                    this.MoveSelectionFocus(active, 0, shift);
                this.StateHasChangedAndScroll();
                return;
            }

            // End: cursor to last entry
            if (key == "End" && !ctrl && !alt)
            {
                PanelState active = this.GetActivePanel();
                if (active.Entries.Count > 0)
                    this.MoveSelectionFocus(active, active.Entries.Count - 1, shift);
                this.StateHasChangedAndScroll();
                return;
            }

            // Escape: deselect all, close context menu
            if (key == "Escape")
            {
                this.CloseContextMenu();
                PanelState active = this.GetActivePanel();
                active.SelectedPaths.Clear();
                this.StateHasChanged();
                return;
            }

            // Shift+F10: context menu at cursor
            if (key == "F10" && !ctrl && shift && !alt)
            {
                PanelState active = this.GetActivePanel();
                if (active.CursorIndex >= 0 && active.CursorIndex < active.Entries.Count)
                {
                    if (active.SelectedPaths.Count == 0)
                    {
                        active.SelectedPaths.Add(active.Entries[active.CursorIndex].FullPath);
                    }

                    // Position context menu at a default location
                    ContextMenuEventArgs contextArgs = new ContextMenuEventArgs();
                    contextArgs.X = 100;
                    contextArgs.Y = 100;
                    contextArgs.Entry = active.Entries[active.CursorIndex];
                    this.HandleContextMenuRequest(contextArgs, this._activePanel);
                    this.StateHasChanged();
                }
                return;
            }

            // PageUp: jump cursor up by visible page
            if (key == "PageUp" && !ctrl && !alt)
            {
                PanelState active = this.GetActivePanel();
                int pageSize = this.GetActivePageSize();
                if (active.Entries.Count > 0)
                    this.MoveSelectionFocus(active, Math.Max(0, active.CursorIndex - pageSize), shift);
                this.StateHasChangedAndScroll();
                return;
            }

            // PageDown: jump cursor down by visible page
            if (key == "PageDown" && !ctrl && !alt)
            {
                PanelState active = this.GetActivePanel();
                int pageSize = this.GetActivePageSize();
                if (active.Entries.Count > 0)
                    this.MoveSelectionFocus(active, Math.Min(active.Entries.Count - 1, active.CursorIndex + pageSize), shift);
                this.StateHasChangedAndScroll();
                return;
            }

            // Letter/digit: incrementally search the first matching entry
            if (key.Length == 1 && !ctrl && !alt)
            {
                char ch = key[0];
                bool isLetterOrDigit = (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9');

                if (isLetterOrDigit)
                {
                    PanelState active = this.GetActivePanel();
                    DateTime now = DateTime.UtcNow;

                    if (this._typeSearchPanelIndex != this._activePanel || this._typeSearchPath != active.CurrentPath)
                        this._typeSearchPrefix = "";

                    this._typeSearchPrefix = BuildTypeSearchPrefix(this._typeSearchPrefix, this._typeSearchLastInputUtc, key, now);
                    this._typeSearchLastInputUtc = now;
                    this._typeSearchPanelIndex = this._activePanel;
                    this._typeSearchPath = active.CurrentPath;
                    this.ScheduleTypeSearchExpiry();

                    bool matched = false;

                    for (int i = 0; i < active.Entries.Count; i++)
                    {
                        if (active.Entries[i].Name.StartsWith(this._typeSearchPrefix, StringComparison.OrdinalIgnoreCase))
                        {
                            active.CursorIndex = i;
                            active.SelectedPaths.Clear();
                            active.SelectedPaths.Add(active.Entries[i].FullPath);
                            active.SelectionAnchorPath = active.Entries[i].FullPath;
                            matched = true;
                            this.StateHasChangedAndScroll();
                            break;
                        }
                    }

                    // Without a match nothing moves, but the typed prefix still has to reach the status bar
                    if (!matched)
                        this.StateHasChanged();

                    return;
                }
            }
        }

        /// <summary>
        /// Moves focus and applies either a single selection or the exact anchor-to-focus range
        /// </summary>
        /// <param name="panel">Panel receiving the keyboard movement</param>
        /// <param name="targetIndex">New focus index</param>
        /// <param name="extendRange">Whether the stable anchor must be retained</param>
        private void MoveSelectionFocus(PanelState panel, int targetIndex, bool extendRange)
        {
            if (targetIndex < 0 || targetIndex >= panel.Entries.Count)
                return;

            if (!extendRange)
            {
                this.SetPanelFocusFromIndex(panel, targetIndex);
                panel.SelectionAnchorPath = panel.Entries[targetIndex].FullPath;
                panel.SelectedPaths.Clear();
                panel.SelectedPaths.Add(panel.SelectionAnchorPath);
                return;
            }

            int anchorIndex = this.FindPanelEntryIndex(panel, panel.SelectionAnchorPath);
            if (anchorIndex < 0)
            {
                anchorIndex = panel.CursorIndex >= 0 && panel.CursorIndex < panel.Entries.Count ? panel.CursorIndex : targetIndex;
                panel.SelectionAnchorPath = panel.Entries[anchorIndex].FullPath;
            }

            this.SetPanelFocusFromIndex(panel, targetIndex);
            panel.SelectedPaths.Clear();
            int start = Math.Min(anchorIndex, targetIndex);
            int end = Math.Max(anchorIndex, targetIndex);
            for (int i = start; i <= end; i++)
                panel.SelectedPaths.Add(panel.Entries[i].FullPath);
        }

        /// <summary>
        /// Builds the status bar center text, showing the incremental search while it is active
        /// </summary>
        /// <returns>Text displayed in the middle of the status bar</returns>
        private string GetStatusCenterText()
        {
            if (!string.IsNullOrEmpty(this._typeSearchPrefix))
                return "Search: " + this._typeSearchPrefix;

            if (!string.IsNullOrEmpty(this._progressText))
                return this._progressText;

            WorkspaceOperationSnapshot operation = this._desktopWorkspace?.Operation;
            if (operation != null)
            {
                if (operation.IsRunning)
                    return operation.Phase == WorkspaceOperationPhase.CancellationRequested ? "Cancelling..." : !string.IsNullOrEmpty(operation.Stage) ? operation.Stage + " " + operation.ProgressCurrent + (operation.ProgressTotal > 0 ? "/" + operation.ProgressTotal : "") : operation.Kind == WorkspaceWorkflowKind.DeleteEntries ? "Deleting... " + operation.FilesProcessed + " completed, " + operation.FilesFailed + " failed" : operation.Kind + "...";
                if (!string.IsNullOrEmpty(operation.ErrorMessage))
                    return operation.ErrorMessage;
                if (operation.Phase == WorkspaceOperationPhase.Succeeded)
                    return operation.Kind + " completed";
            }
            if (!string.IsNullOrEmpty(this._workspaceWorkflow?.ErrorMessage))
                return this._workspaceWorkflow.ErrorMessage;
            return this._progressText;
        }

        /// <summary>
        /// Clears the incremental search feedback once the prefix has expired
        /// </summary>
        private void ScheduleTypeSearchExpiry()
        {
            DateTime scheduledFor = this._typeSearchLastInputUtc;

            _ = this.InvokeAsync(async () =>
            {
                await System.Threading.Tasks.Task.Delay(TYPE_SEARCH_TIMEOUT_MS);

                if (this._isDisposed || this._typeSearchLastInputUtc != scheduledFor)
                    return;

                this._typeSearchPrefix = "";
                this.StateHasChanged();
            });
        }

        /// <summary>
        /// Builds the incremental entry-search prefix for a character key
        /// </summary>
        /// <param name="currentPrefix">Prefix accumulated so far</param>
        /// <param name="lastInputUtc">Time of the previous character</param>
        /// <param name="key">Current character key</param>
        /// <param name="nowUtc">Time of the current character</param>
        /// <returns>Updated search prefix</returns>
        internal static string BuildTypeSearchPrefix(string currentPrefix, DateTime lastInputUtc, string key, DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(currentPrefix) || (nowUtc - lastInputUtc).TotalMilliseconds >= TYPE_SEARCH_TIMEOUT_MS)
                return key;

            return currentPrefix + key;
        }

        /// <summary>
        /// Updates UI and scrolls the cursor row into view
        /// </summary>
        private void StateHasChangedAndScroll()
        {
            this._scrollAfterRender = true;
            this.StateHasChanged();
        }

        /// <summary>
        /// Shows the final success outcome without replacing the status bar text
        /// </summary>
        /// <param name="summary">Short title</param>
        /// <param name="detail">Outcome detail</param>
        private void NotifySuccess(string summary, string detail)
        {
            this.NotificationService.Notify(NotificationSeverity.Success, summary, detail, 4000);
        }

        /// <summary>
        /// Shows a final warning without replacing the status bar text
        /// </summary>
        /// <param name="summary">Short title</param>
        /// <param name="detail">Warning detail</param>
        private void NotifyWarning(string summary, string detail)
        {
            this.NotificationService.Notify(NotificationSeverity.Warning, summary, detail, 6000);
        }

        /// <summary>
        /// Shows a final error without replacing the status bar text
        /// </summary>
        /// <param name="summary">Short title</param>
        /// <param name="detail">Error detail</param>
        private void NotifyError(string summary, string detail)
        {
            this.NotificationService.Notify(NotificationSeverity.Error, summary, detail, 8000);
        }

        /// <summary>
        /// Presents a final warning through the channel provided by the UI variant
        /// </summary>
        /// <param name="summary">Short title</param>
        /// <param name="detail">Warning detail</param>
        private void ShowOperationWarning(string summary, string detail)
        {
            this.NotifyWarning(summary, detail);
        }

        #endregion

        #region IAsyncDisposable

        /// <summary>
        /// Cleanup JS interop references
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (this._isDisposed)
                return;
            this._isDisposed = true;
            this._handoffLifetimeCancellation.Cancel();
            this._handoffDrainCancellation?.Cancel();
            this._handoffLifetimeCancellation.Dispose();
            this._workspaceConfirmationLifetime?.Dispose();

            try
            {
                try
                {
                    this.PersistWorkspacePanels();
                }
                finally
                {
                    this._leaseRevocationRegistration.Dispose();
                    try
                    {
                        this._workspaceChangeSubscription?.Dispose();
                        this._workspaceChangeSubscription = null;
                    }
                    finally
                    {
                        if (this._clientAttached)
                        {
                            this._workspaceService.DetachClient(this._attachmentId);
                            this._clientAttached = false;
                        }
                    }
                }

                if (this._jsModule != null)
                {
                    try
                    {
                        try
                        {
                            await this._jsModule.InvokeVoidAsync("stopWorkspacePresence");
                        }
                        finally
                        {
                            try
                            {
                                await this._jsModule.InvokeVoidAsync("disposeKeyboardCapture");
                            }
                            finally
                            {
                                try
                                {
                                    await this._jsModule.InvokeVoidAsync("disposeLongPress");
                                }
                                finally
                                {
                                    await this._jsModule.DisposeAsync();
                                }
                            }
                        }
                    }
                    catch (Exception ex) when (ex is JSDisconnectedException || ex is OperationCanceledException)
                    {
                    }
                }
            }
            finally
            {
                if (this._dotNetRef != null)
                {
                    this._dotNetRef.Dispose();
                    this._dotNetRef = null;
                }

                if (this._settingsChangeSubscription != null)
                {
                    this._settingsChangeSubscription.Dispose();
                    this._settingsChangeSubscription = null;
                }
            }
        }

        #endregion

        /// <summary>
        /// Distinguishes ordinary navigation from transfers between the history stacks
        /// </summary>
        private enum PanelNavigationKind
        {
            /// <summary>
            /// New destination
            /// </summary>
            Ordinary,

            /// <summary>
            /// Previous destination
            /// </summary>
            Back,

            /// <summary>
            /// Next destination
            /// </summary>
            Forward
        }
    }
}
