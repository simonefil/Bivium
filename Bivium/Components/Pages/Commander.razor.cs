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
        /// Stato di rendering del tema per il circuito corrente
        /// </summary>
        [Inject]
        private Radzen.ThemeService _themeService { get; set; }

        /// <summary>Servizio scoped per conferme native anche senza lease attiva</summary>
        [Inject]
        private DialogService _dialogService { get; set; }

        #endregion

        #region Constants

        /// <summary>
        /// Maximum file size for editor (5 MB)
        /// </summary>
        private const long MAX_EDITOR_SIZE = 5L * 1024 * 1024;

        /// <summary>
        /// Minimum interval between progress UI updates
        /// </summary>
        private const int PROGRESS_UPDATE_INTERVAL_MS = 100;

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
        /// Pending operation type for dialog callbacks
        /// </summary>
        private string _pendingOperation = "";

        /// <summary>
        /// Reference to confirm dialog component
        /// </summary>
        private ConfirmDialog _confirmDialog;

        /// <summary>
        /// Reference to overwrite dialog component
        /// </summary>
        private OverwriteDialog _overwriteDialog;

        /// <summary>
        /// Reference to input dialog component
        /// </summary>
        private InputDialog _inputDialog;

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
        /// Cancellation source of the running file operation, null when none is running
        /// </summary>
        private CancellationTokenSource _operationCancellation;

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
        /// Percentuale persistita occupata dal pannello sinistro nel layout Radzen
        /// </summary>
        private double _outerPanelSizePercent = 50;

        /// <summary>
        /// Indice del pannello compresso nel layout Radzen, oppure -1
        /// </summary>
        private int _collapsedPanelIndex = -1;

        /// <summary>
        /// CSS class for the panels area
        /// </summary>
        private string _panelsAreaClass = "panels-area";

        /// <summary>
        /// Flag to scroll cursor into view after next render
        /// </summary>
        private bool _scrollAfterRender = false;

        /// <summary>
        /// Source paths waiting for overwrite decisions before paste
        /// </summary>
        private List<string> _pendingPastePaths = new List<string>();

        /// <summary>
        /// Source paths that have an overwrite conflict
        /// </summary>
        private List<string> _pendingPasteConflictPaths = new List<string>();

        /// <summary>
        /// Source paths approved for overwrite
        /// </summary>
        private List<string> _pendingPasteOverwritePaths = new List<string>();

        /// <summary>
        /// Source paths skipped during overwrite prompts
        /// </summary>
        private List<string> _pendingPasteSkippedPaths = new List<string>();

        /// <summary>
        /// Destination directory for the pending paste operation
        /// </summary>
        private string _pendingPasteDestinationDir = "";

        /// <summary>
        /// Whether the pending paste operation is a cut/move
        /// </summary>
        private bool _pendingPasteIsCut = false;

        /// <summary>
        /// Current conflict index for the pending overwrite prompt sequence
        /// </summary>
        private int _pendingPasteConflictIndex = 0;

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

        /// <summary>Conferma workspace posseduta, anche durante takeover senza lease</summary>
        private RadzenDialogLifetime _workspaceConfirmationLifetime;

        /// <summary>
        /// Tema selezionato dal Commander
        /// </summary>
        private string _currentTheme = RadzenThemeCatalog.DEFAULT_THEME;

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
            if (!this._clientAttached)
                return;

            string activeIp = this._observedLease?.RemoteIp ?? "unknown";
            string confirmationMessage = "Hai Bivium già aperto da IP " + activeIp + ". Vuoi rendere attiva questa sessione?";
            bool confirmed = await this.ConfirmWorkspaceActionAsync("Activate this session", confirmationMessage, "Activate");
            if (!confirmed || this._isDisposed || !this._clientAttached)
                return;

            long expectedGeneration = this._observedLease?.Generation ?? 0;
            WorkspaceAttachResult result = this._workspaceService.TryTakeover(this._attachmentId, expectedGeneration);
            this.ApplyAttachResult(result);
            await this.UpdateWorkspacePresenceLeaseAsync();
            if (this._hasActiveLease)
                this.InitializePanels();
            this.StateHasChanged();
        }

        /// <summary>
        /// Reacts immediately to revocation, detach or takeover
        /// </summary>
        private void HandleWorkspaceLeaseChanged(BiviumWorkspaceSnapshot workspace)
        {
            if (this._isDisposed || workspace == null)
                return;

            ActiveClientLeaseSnapshot lease = workspace.ActiveClientLease;
            bool hasControl = lease != null && lease.AttachmentId == this._attachmentId;
            bool changed = hasControl != this._hasActiveLease || (hasControl && lease.Generation != this._leaseGeneration);
            this._observedLease = lease;
            this._workspaceRevision = Math.Max(this._workspaceRevision, workspace.Revision);
            if (!changed)
                return;

            this._hasActiveLease = hasControl;
            this._leaseGeneration = hasControl ? lease.Generation : 0;
            this.ConfigureLeaseRevocation();
            if (!hasControl)
                this._panelsInitialized = false;
            _ = this.InvokeAsync(async () =>
            {
                await this.UpdateWorkspacePresenceLeaseAsync();
                if (this._hasActiveLease)
                    this.InitializePanels();
                this.StateHasChanged();
            });
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
            bool result = this._hasActiveLease && this._workspaceService.ValidateMutation(this.GetClientToken());
            if (!result)
            {
                this._hasActiveLease = false;
                this._leaseGeneration = 0;
            }

            return result;
        }

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
            this._panelsAreaClass = this._singlePanelMode ? "panels-area single-panel" : "panels-area";
            this._workspaceRevision = workspace.Revision;

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
        /// Returns the inactive panel state
        /// </summary>
        /// <returns>Inactive panel state</returns>
        private PanelState GetInactivePanel()
        {
            PanelState result = this._activePanel == 0 ? this._rightPanel : this._leftPanel;
            return result;
        }

        /// <summary>
        /// Sets the active panel
        /// </summary>
        /// <param name="index">Panel index (0=left, 1=right)</param>
        private void SetActivePanel(int index)
        {
            if (this.CanMutateWorkspace())
                this._activePanel = Math.Clamp(index, 0, 1);
        }

        /// <summary>
        /// Alterna il pannello visibile nella sola proiezione responsive
        /// </summary>
        private void SwitchResponsivePanel()
        {
            if (this.CanMutateWorkspace() && !this._singlePanelMode)
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
            if (!this.CanMutateWorkspace())
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
        /// Handles selection change in the left panel
        /// </summary>
        /// <param name="paths">New selection</param>
        private void HandleLeftSelectionChanged(List<string> paths)
        {
            this._leftPanel.SelectedPaths = paths;
        }

        /// <summary>
        /// Handles selection change in the right panel
        /// </summary>
        /// <param name="paths">New selection</param>
        private void HandleRightSelectionChanged(List<string> paths)
        {
            this._rightPanel.SelectedPaths = paths;
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
        /// Handles cursor change in the left panel
        /// </summary>
        /// <param name="index">New cursor index</param>
        private void HandleLeftCursorChanged(int index)
        {
            this.SetPanelFocusFromIndex(this._leftPanel, index);
        }

        /// <summary>
        /// Handles cursor change in the right panel
        /// </summary>
        /// <param name="index">New cursor index</param>
        private void HandleRightCursorChanged(int index)
        {
            this.SetPanelFocusFromIndex(this._rightPanel, index);
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
        /// Stores the semantic selection anchor of the left panel
        /// </summary>
        /// <param name="path">Anchor full path</param>
        private void HandleLeftSelectionAnchorChanged(string path)
        {
            this._leftPanel.SelectionAnchorPath = path ?? "";
        }

        /// <summary>
        /// Stores the semantic selection anchor of the right panel
        /// </summary>
        /// <param name="path">Anchor full path</param>
        private void HandleRightSelectionAnchorChanged(string path)
        {
            this._rightPanel.SelectionAnchorPath = path ?? "";
        }

        /// <summary>
        /// Accepts a completed left-panel column resize while this circuit owns the lease
        /// </summary>
        /// <param name="ratios">Five normalized column widths</param>
        private void HandleLeftColumnRatiosChanged(double[] ratios)
        {
            if (this.CanMutateWorkspace())
                this.ApplyColumnRatios(this._leftPanel, ratios);
        }

        /// <summary>
        /// Accepts a completed right-panel column resize while this circuit owns the lease
        /// </summary>
        /// <param name="ratios">Five normalized column widths</param>
        private void HandleRightColumnRatiosChanged(double[] ratios)
        {
            if (this.CanMutateWorkspace())
                this.ApplyColumnRatios(this._rightPanel, ratios);
        }

        /// <summary>
        /// Validates and stores one complete normalized column configuration
        /// </summary>
        /// <param name="panel">Target panel</param>
        /// <param name="ratios">Five positive finite widths</param>
        private void ApplyColumnRatios(PanelState panel, double[] ratios)
        {
            if (panel.Columns.Count > 0)
                return;
            if (ratios == null || ratios.Length != 5)
                return;

            double total = 0;
            for (int i = 0; i < ratios.Length; i++)
            {
                if (!double.IsFinite(ratios[i]) || ratios[i] <= 0)
                    return;
                total += ratios[i];
            }

            if (!double.IsFinite(total) || total <= 0)
                return;

            panel.NameColumnRatio = ratios[0] / total;
            panel.SizeColumnRatio = ratios[1] / total;
            panel.DateColumnRatio = ratios[2] / total;
            panel.AttributesColumnRatio = ratios[3] / total;
            panel.OwnerColumnRatio = ratios[4] / total;
        }

        /// <summary>
        /// Accepts only a complete measured layout while this circuit owns workspace mutation
        /// </summary>
        /// <param name="panel">Target panel</param>
        /// <param name="columns">Ordered complete column layout</param>
        private void HandleColumnLayoutRequested(PanelState panel, IEnumerable<FileListColumnState> columns)
        {
            if (!this.CanMutateWorkspace())
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
            if (this.CanMutateWorkspace())
                this._leftPanel.ScrollAnchorPath = path;
        }

        /// <summary>
        /// Persists the semantic scroll anchor of the right panel
        /// </summary>
        /// <param name="path">First visible entry path</param>
        private void HandleRightScrollAnchorChanged(string path)
        {
            if (this.CanMutateWorkspace())
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
        /// Marks a semantic directory-tree expansion change for workspace persistence
        /// </summary>
        private void HandlePanelTreeExpansionChanged()
        {
            // EventCallback triggers the render that persists the updated expanded-directory snapshot
        }

        /// <summary>
        /// Applica una modifica semantica all'albero sinistro dopo la rivalidazione del lease
        /// </summary>
        /// <param name="change">Modifica richiesta dalla variante Radzen</param>
        private void HandleLeftTreeExpansionChanged(DirectoryTreeExpansionChange change)
        {
            this.HandleTreeExpansionChanged(this._leftPanel, change);
        }

        /// <summary>
        /// Applica una modifica semantica all'albero destro dopo la rivalidazione del lease
        /// </summary>
        /// <param name="change">Modifica richiesta dalla variante Radzen</param>
        private void HandleRightTreeExpansionChanged(DirectoryTreeExpansionChange change)
        {
            this.HandleTreeExpansionChanged(this._rightPanel, change);
        }

        /// <summary>
        /// Riduce una modifica di espansione nel PanelState autoritativo
        /// </summary>
        /// <param name="panel">Pannello destinatario</param>
        /// <param name="change">Modifica semantica richiesta</param>
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
        /// Applica il layout verticale del pannello sinistro
        /// </summary>
        /// <param name="change">Geometria richiesta</param>
        private void HandleLeftTreeLayoutChanged(PanelTreeLayoutChange change)
        {
            this.HandleTreeLayoutChanged(this._leftPanel, change);
        }

        /// <summary>
        /// Applica il layout verticale del pannello destro
        /// </summary>
        /// <param name="change">Geometria richiesta</param>
        private void HandleRightTreeLayoutChanged(PanelTreeLayoutChange change)
        {
            this.HandleTreeLayoutChanged(this._rightPanel, change);
        }

        /// <summary>
        /// Valida e riduce una modifica del layout verticale
        /// </summary>
        /// <param name="panel">Pannello destinatario</param>
        /// <param name="change">Geometria richiesta</param>
        private void HandleTreeLayoutChanged(PanelState panel, PanelTreeLayoutChange change)
        {
            if (change == null || !this.CanMutateWorkspace())
                return;
            panel.TreeSizePercent = this.ClampTreeSize(change.SizePercent);
            panel.TreeCollapsed = change.Collapsed;
        }

        /// <summary>
        /// Persiste la nuova proporzione orizzontale dei pannelli
        /// </summary>
        /// <param name="args">Dati conclusivi del resize Radzen</param>
        private void HandleOuterSplitterResize(RadzenSplitterResizeEventArgs args)
        {
            if (!this.CanMutateWorkspace())
                return;
            double leftSize = args.PaneIndex == 0 ? args.NewSize : 100 - args.NewSize;
            this._outerPanelSizePercent = this.ClampOuterPanelSize(leftSize);
        }

        /// <summary>
        /// Persiste il pannello compresso dall'utente
        /// </summary>
        /// <param name="args">Pannello compresso da Radzen</param>
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
        /// Rimuove lo stato di compressione quando il pannello viene espanso
        /// </summary>
        /// <param name="args">Pannello espanso da Radzen</param>
        private void HandleOuterSplitterExpand(RadzenSplitterEventArgs args)
        {
            if (this.CanMutateWorkspace() && this._collapsedPanelIndex == args.PaneIndex)
                this._collapsedPanelIndex = -1;
        }

        /// <summary>
        /// Forza il render proprietario dopo una mutazione già passata dal reducer
        /// </summary>
        private void HandlePanelUiStateChanged()
        {
        }

        /// <summary>
        /// Limita la geometria orizzontale a valori finiti e utilizzabili
        /// </summary>
        /// <param name="value">Percentuale proposta</param>
        /// <returns>Percentuale valida tra 20 e 80</returns>
        private double ClampOuterPanelSize(double value)
        {
            return double.IsFinite(value) ? Math.Clamp(value, 20, 80) : 50;
        }

        /// <summary>
        /// Limita la geometria verticale a valori finiti e utilizzabili
        /// </summary>
        /// <param name="value">Percentuale proposta</param>
        /// <returns>Percentuale valida tra 15 e 85</returns>
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
        /// Creates a progress callback that throttles UI renders during high-volume file operations
        /// </summary>
        /// <param name="operationName">Operation label</param>
        /// <returns>Progress callback</returns>
        private Action<int, int, string> CreateThrottledProgressCallback(string operationName)
        {
            DateTime lastUpdate = DateTime.MinValue;
            object updateLock = new object();

            Action<int, int, string> result = (current, total, fileName) =>
            {
                DateTime now = DateTime.UtcNow;
                bool shouldUpdate = false;

                lock (updateLock)
                {
                    if ((now - lastUpdate).TotalMilliseconds >= PROGRESS_UPDATE_INTERVAL_MS || current >= total)
                    {
                        lastUpdate = now;
                        shouldUpdate = true;
                    }
                }

                if (shouldUpdate)
                {
                    this._progressText = operationName + " " + current + "/" + total + ": " + fileName;
                    _ = this.InvokeAsync(() => this.StateHasChanged());
                }
            };

            return result;
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
        /// Restituisce l'entry identificata dal focus semantico, con fallback per il cursore legacy
        /// </summary>
        /// <param name="panel">Pannello da consultare</param>
        /// <returns>Entry focalizzata oppure null se non disponibile</returns>
        private FileSystemEntry GetFocusedEntry(PanelState panel)
        {
            int index = this.FindPanelEntryIndex(panel, panel.FocusedPath);
            if (index < 0 && string.IsNullOrEmpty(panel.FocusedPath))
                index = panel.CursorIndex;
            return index >= 0 && index < panel.Entries.Count ? panel.Entries[index] : null;
        }

        /// <summary>
        /// Applica una richiesta atomica mantenendo selezione, focus e anchor sotto il controllo di Commander
        /// </summary>
        /// <param name="panel">Pannello destinatario</param>
        /// <param name="request">Interazione richiesta dalla superficie UI</param>
        private void HandleFileListInteraction(PanelState panel, FileListInteractionRequest request)
        {
            if (panel == null || request == null || !this.CanMutateWorkspace())
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

        /// <summary>
        /// Handles context menu request from a panel
        /// </summary>
        /// <param name="args">Context menu event data</param>
        /// <param name="panelIndex">Panel index (0=left, 1=right)</param>
        private void HandleContextMenuRequest(ContextMenuEventArgs args, int panelIndex)
        {
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

            // Adjust menu position after render to keep it within viewport
            _ = this.AdjustContextMenuAsync();
        }

        /// <summary>
        /// Adjusts context menu position after render
        /// </summary>
        private async System.Threading.Tasks.Task AdjustContextMenuAsync()
        {
            await System.Threading.Tasks.Task.Delay(50);
            if (this._jsModule != null)
            {
                await this._jsModule.InvokeVoidAsync("adjustContextMenuPosition");
            }
        }

        /// <summary>
        /// Closes the context menu
        /// </summary>
        private void CloseContextMenu()
        {
            this._contextMenuVisible = false;
        }

        #endregion

        #region File Operations

        /// <summary>
        /// Activates the focused entry using Explorer directory/editor behavior
        /// </summary>
        private void DoOpen()
        {
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
                    this._confirmDialog.Show("Edit", "Extension '" + extension + "' is not in the editable extensions list.", "OK", "");
                return;
            }
            if (entry.SizeBytes > MAX_EDITOR_SIZE)
            {
                if (!silentPolicyRejection)
                    this._confirmDialog.Show("Edit", "File is too large to edit (max 5 MB).", "OK", "");
                return;
            }

            FileTextResult readResult = this._fileOperationService.ReadFileText(entry.FullPath, MAX_EDITOR_SIZE);
            if (readResult.Success)
                this._editorDialog.Show(entry.FullPath, readResult.Content);
            else
                this._confirmDialog.Show("Edit", readResult.ErrorMessage, "OK", "");
        }

        /// <summary>
        /// Copies selected entries to internal clipboard
        /// </summary>
        private void DoCopy()
        {
            PanelState active = this.GetActivePanel();
            this._clipboard.Paths = this.GetSelectedOrCursorPaths(active);
            this._clipboard.IsCut = false;
        }

        /// <summary>
        /// Cuts selected entries to internal clipboard
        /// </summary>
        private void DoCut()
        {
            PanelState active = this.GetActivePanel();
            this._clipboard.Paths = this.GetSelectedOrCursorPaths(active);
            this._clipboard.IsCut = true;
        }

        /// <summary>
        /// Pastes from clipboard into the active panel's directory
        /// </summary>
        private void DoPaste()
        {
            if (this._clipboard.HasEntries())
            {
                PanelState active = this.GetActivePanel();
                string destinationDir = active.CurrentPath;
                List<string> paths = new List<string>(this._clipboard.Paths);
                bool isCut = this._clipboard.IsCut;

                List<string> conflictPaths = this.GetPasteConflicts(paths, destinationDir);
                if (conflictPaths.Count > 0)
                {
                    this.PreparePendingPaste(paths, conflictPaths, destinationDir, isCut);
                    this.ShowNextPasteOverwritePrompt();
                    return;
                }

                this.StartPasteOperation(paths, destinationDir, isCut, new List<string>());
            }
        }

        /// <summary>
        /// Creates the cancellation source of a long file operation, linked to lease revocation
        /// </summary>
        /// <param name="revocationToken">Lease revocation token of the current client</param>
        /// <returns>Cancellation source owned by the running operation</returns>
        private CancellationTokenSource BeginCancellableOperation(CancellationToken revocationToken)
        {
            CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(revocationToken);
            this._operationCancellation = source;
            return source;
        }

        /// <summary>
        /// Releases the cancellation source of a finished file operation
        /// </summary>
        /// <param name="source">Cancellation source created for the operation</param>
        private void EndCancellableOperation(CancellationTokenSource source)
        {
            if (this._operationCancellation == source)
                this._operationCancellation = null;

            source.Dispose();
        }

        /// <summary>
        /// Cancels the running file operation, keeping whatever has already been written
        /// </summary>
        private void CancelActiveOperation()
        {
            if (this._operationCancellation == null)
                return;

            this._operationCancellation.Cancel();
            this._progressText = "Cancelling...";
        }

        /// <summary>
        /// Starts a paste operation after overwrite decisions have been resolved
        /// </summary>
        /// <param name="paths">Source paths to process</param>
        /// <param name="destinationDir">Destination directory</param>
        /// <param name="isCut">True when moving, false when copying</param>
        /// <param name="overwritePaths">Source paths approved for overwrite</param>
        private void StartPasteOperation(List<string> paths, string destinationDir, bool isCut, List<string> overwritePaths)
        {
            if (paths.Count == 0 || !this.CanMutateWorkspace())
            {
                return;
            }

            List<string> operationPaths = new List<string>(paths);
            List<string> operationOverwritePaths = new List<string>(overwritePaths);
            CancellationToken revocationToken = this._workspaceService.GetRevocationToken(this.GetClientToken());
            CancellationTokenSource operationCancellation = this.BeginCancellableOperation(revocationToken);
            CancellationToken cancellationToken = operationCancellation.Token;

            // Show initial progress
            this._progressText = isCut ? "Moving..." : "Copying...";

            Action<int, int, string> onProgress = this.CreateThrottledProgressCallback(isCut ? "Moving" : "Copying");

            // Run file operation on background thread
            Thread worker = new Thread(() =>
            {
                FileOperationResult result;

                try
                {
                    if (isCut)
                    {
                        result = this._fileOperationService.MoveEntriesWithProgress(operationPaths, destinationDir, onProgress, operationOverwritePaths, cancellationToken);
                    }
                    else
                    {
                        result = this._fileOperationService.CopyEntriesWithProgress(operationPaths, destinationDir, onProgress, operationOverwritePaths, cancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    result = FileOperationResult.Fail("Operation cancelled.");
                }

                // Update UI on the render thread
                _ = this.InvokeAsync(() =>
                {
                    this.EndCancellableOperation(operationCancellation);

                    if (this._isDisposed || revocationToken.IsCancellationRequested)
                    {
                        this._progressText = "";
                        return;
                    }
                    // Clear clipboard on successful cut
                    if (isCut && result.Success)
                    {
                        this._clipboard.Clear();
                    }

                    // A cancelled operation keeps what it already wrote, so the panels still need a refresh
                    this._progressText = cancellationToken.IsCancellationRequested ? "Operation cancelled, completed items kept." : "";

                    this.RefreshVisiblePanels();

                    if (cancellationToken.IsCancellationRequested)
                    {
                        this.NotifyWarning(isCut ? "Move cancelled" : "Copy cancelled", this._progressText);
                    }
                    else if (!result.Success)
                    {
                        this._pendingOperation = "";
                        this.ShowOperationError(isCut ? "Move failed" : "Copy failed", result.ErrorMessage);
                    }
                    else
                    {
                        this.NotifySuccess(isCut ? "Move completed" : "Copy completed", operationPaths.Count + " item(s) processed.");
                    }

                    this.StateHasChanged();
                });
            });

            worker.IsBackground = true;
            worker.Start();
        }

        /// <summary>
        /// Finds paste conflicts that need an overwrite decision
        /// </summary>
        /// <param name="paths">Source paths from clipboard</param>
        /// <param name="destinationDir">Destination directory</param>
        /// <returns>Source paths with destination conflicts</returns>
        private List<string> GetPasteConflicts(List<string> paths, string destinationDir)
        {
            List<string> result = new List<string>();

            for (int i = 0; i < paths.Count; i++)
            {
                string source = paths[i];
                bool sourceIsFile = File.Exists(source);
                bool sourceIsDirectory = Directory.Exists(source);
                if (!sourceIsFile && !sourceIsDirectory)
                {
                    continue;
                }

                string destPath = Path.Combine(destinationDir, Path.GetFileName(source));
                bool destinationExists = sourceIsFile ? File.Exists(destPath) : Directory.Exists(destPath);
                if (!this.AreSamePath(source, destPath) && destinationExists)
                {
                    result.Add(source);
                }
            }

            return result;
        }

        /// <summary>
        /// Stores paste state while overwrite prompts are shown
        /// </summary>
        /// <param name="paths">Source paths to paste</param>
        /// <param name="conflictPaths">Source paths with conflicts</param>
        /// <param name="destinationDir">Destination directory</param>
        /// <param name="isCut">True when moving, false when copying</param>
        private void PreparePendingPaste(List<string> paths, List<string> conflictPaths, string destinationDir, bool isCut)
        {
            this._pendingPastePaths = new List<string>(paths);
            this._pendingPasteConflictPaths = new List<string>(conflictPaths);
            this._pendingPasteOverwritePaths = new List<string>();
            this._pendingPasteSkippedPaths = new List<string>();
            this._pendingPasteDestinationDir = destinationDir;
            this._pendingPasteIsCut = isCut;
            this._pendingPasteConflictIndex = 0;
        }

        /// <summary>
        /// Shows the next overwrite prompt, or starts paste when all decisions are available
        /// </summary>
        private void ShowNextPasteOverwritePrompt()
        {
            if (this._pendingPasteConflictIndex >= this._pendingPasteConflictPaths.Count)
            {
                this.CompletePendingPaste();
                return;
            }

            string sourcePath = this._pendingPasteConflictPaths[this._pendingPasteConflictIndex];
            string destinationPath = Path.Combine(this._pendingPasteDestinationDir, Path.GetFileName(sourcePath));
            bool isDirectory = Directory.Exists(sourcePath);
            string entryType = isDirectory ? "directory" : "file";
            string overwriteQuestion = isDirectory ? "Merge it and overwrite conflicting contents?" : "Overwrite it?";
            string message = "A " + entryType + " named '" + Path.GetFileName(sourcePath) + "' already exists in the destination.\n\n"
                + "Source: " + sourcePath + "\n"
                + "Destination: " + destinationPath + "\n\n"
                + overwriteQuestion;

            this._overwriteDialog.Show("Overwrite " + entryType, message);
        }

        /// <summary>
        /// Completes the pending paste after overwrite prompts
        /// </summary>
        private void CompletePendingPaste()
        {
            List<string> paths = new List<string>();
            for (int i = 0; i < this._pendingPastePaths.Count; i++)
            {
                if (!this.ContainsPath(this._pendingPasteSkippedPaths, this._pendingPastePaths[i]))
                {
                    paths.Add(this._pendingPastePaths[i]);
                }
            }

            List<string> overwritePaths = new List<string>(this._pendingPasteOverwritePaths);
            string destinationDir = this._pendingPasteDestinationDir;
            bool isCut = this._pendingPasteIsCut;

            this.ClearPendingPaste();

            if (paths.Count > 0)
            {
                this.StartPasteOperation(paths, destinationDir, isCut, overwritePaths);
            }
        }

        /// <summary>
        /// Clears pending paste state
        /// </summary>
        private void ClearPendingPaste()
        {
            this._pendingPastePaths = new List<string>();
            this._pendingPasteConflictPaths = new List<string>();
            this._pendingPasteOverwritePaths = new List<string>();
            this._pendingPasteSkippedPaths = new List<string>();
            this._pendingPasteDestinationDir = "";
            this._pendingPasteIsCut = false;
            this._pendingPasteConflictIndex = 0;
        }

        /// <summary>
        /// Checks whether a path list contains a path using filesystem comparison rules
        /// </summary>
        /// <param name="paths">Path list</param>
        /// <param name="path">Path to find</param>
        /// <returns>True if the path is present</returns>
        private bool ContainsPath(List<string> paths, string path)
        {
            bool result = false;

            for (int i = 0; i < paths.Count; i++)
            {
                if (this.AreSamePath(paths[i], path))
                {
                    result = true;
                    break;
                }
            }

            return result;
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
            PanelState active = this.GetActivePanel();
            this.HandleFileListInteraction(active, new FileListInteractionRequest { Intent = FileListInteractionIntent.SelectAll });
        }

        /// <summary>
        /// Initiates new file creation with input dialog
        /// </summary>
        private void DoNewFile()
        {
            if (!this.CanMutateWorkspace())
                return;
            this._pendingOperation = "newfile";
            this._inputDialog.Show("New File", "File name:", "");
        }

        /// <summary>
        /// Initiates new folder creation with input dialog
        /// </summary>
        private void DoNewFolder()
        {
            if (!this.CanMutateWorkspace())
                return;
            this._pendingOperation = "mkdir";
            this._inputDialog.Show("New Folder", "Folder name:", "");
        }

        /// <summary>
        /// Initiates rename with input dialog
        /// </summary>
        private void DoRename()
        {
            if (!this.CanMutateWorkspace())
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

                this._pendingOperation = "rename";
                this._inputDialog.Show("Rename", "New name:", entry.Name, selectionLength);
            }
        }

        /// <summary>
        /// Initiates delete with confirmation dialog
        /// </summary>
        private void DoDelete()
        {
            if (!this.CanMutateWorkspace())
                return;
            PanelState active = this.GetActivePanel();
            List<string> paths = this.GetSelectedOrCursorPaths(active);
            if (paths.Count > 0)
            {
                active.SelectedPaths = paths;
                this._pendingOperation = "delete";
                this._confirmDialog.Show("Delete", "Delete " + active.SelectedPaths.Count + " item(s)?", "Delete", "Cancel");
            }
        }

        /// <summary>
        /// Downloads the selected file or directory
        /// </summary>
        private void DoDownload()
        {
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
            if (!this.CanMutateWorkspace())
                return;
            PanelState active = this.GetActivePanel();
            this._uploadDialog.Show(active.CurrentPath);
        }

        /// <summary>
        /// Refreshes the active panel contents
        /// </summary>
        private void DoRefresh()
        {
            PanelState active = this.GetActivePanel();
            this.LoadPanelContents(active);
        }

        /// <summary>
        /// Toggles single/dual panel mode
        /// </summary>
        private void DoToggleSinglePanel()
        {
            this._singlePanelMode = !this._singlePanelMode;

            // In single panel mode, ensure active panel is always left
            if (this._singlePanelMode)
            {
                this._activePanel = 0;
                this._panelsAreaClass = "panels-area single-panel";
            }
            else
            {
                this._panelsAreaClass = "panels-area";
            }
        }

        /// <summary>
        /// Shows properties dialog for the selected entry
        /// </summary>
        private void DoProperties()
        {
            PanelState active = this.GetActivePanel();
            FileSystemEntry entry = this.GetSingleTargetEntry(active, "Properties");
            if (entry != null)
            {
                this._propertiesDialog.Show(entry);
            }
        }

        /// <summary>
        /// Shows permissions dialog for the selected entry
        /// </summary>
        private void DoPermissions()
        {
            PanelState active = this.GetActivePanel();
            FileSystemEntry entry = this.GetSingleTargetEntry(active, "Permissions");
            if (entry != null)
            {
                this._permissionsDialog.Show(entry);
            }
        }

        /// <summary>
        /// Toggles the terminal panel
        /// </summary>
        private async System.Threading.Tasks.Task DoToggleTerminalAsync()
        {
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
        /// Valida, persiste e applica il tema scelto nella variante Radzen
        /// </summary>
        /// <param name="theme">Nome del tema scelto</param>
        private async System.Threading.Tasks.Task DoThemeChange(string theme)
        {
            if (!this.CanMutateWorkspace())
                return;

            string normalizedTheme;
            if (!RadzenThemeCatalog.TryNormalize(theme, out normalizedTheme))
                return;

            if (this._jsModule == null)
            {
                this._jsModule = await this.JSRuntime.InvokeAsync<IJSObjectReference>("import", "./js/interop.js?v=20260922-file-columns-v1");
            }

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
            List<string> extensions = this._settings.CurrentValue.EditableExtensions;
            this._settingsDialog.Show(extensions);
        }

        /// <summary>
        /// Shows default creation permissions settings
        /// </summary>
        private void DoCreationPermissions()
        {
            this._creationPermissionsDialog.Show(this._settings.CurrentValue.DefaultCreationPermissions);
        }

        /// <summary>
        /// Shows the authentication settings dialog
        /// </summary>
        private async System.Threading.Tasks.Task DoAuthenticationSettings()
        {
            await this._authSettingsDialog.Show();
        }

        /// <summary>
        /// Logs out the current administrator
        /// </summary>
        private async System.Threading.Tasks.Task DoLogout()
        {
            if (this._jsModule == null)
            {
                this._jsModule = await this.JSRuntime.InvokeAsync<IJSObjectReference>("import", "./js/interop.js?v=20260922-file-columns-v1");
            }

            bool success = await this._jsModule.InvokeAsync<bool>("postJson", "/api/Auth/logout", "{}");
            if (success)
            {
                await this._jsModule.InvokeVoidAsync("reloadPage");
            }
        }

        /// <summary>
        /// Exits the application (closes the browser tab via JS)
        /// </summary>
        private void DoExit()
        {
            _ = this.JSRuntime.InvokeVoidAsync("close");
        }

        /// <summary>
        /// Terminates every PTY and restores the workspace defaults after explicit confirmation
        /// </summary>
        private async System.Threading.Tasks.Task DoResetWorkspace()
        {
            if (!this.CanMutateWorkspace())
                return;
            const string message = "Reset workspace? This terminates every terminal process and clears saved panel and window state.";
            bool confirmed = await this.ConfirmWorkspaceActionAsync("Reset workspace", message, "Reset");
            if (!confirmed || this._isDisposed || !this.CanMutateWorkspace())
                return;

            WorkspaceClientToken token = this.GetClientToken();
            this._terminalRuntimeService.CloseAllSessions(token);
            BiviumWorkspaceSnapshot snapshot = this._workspaceService.ResetWorkspace(token);
            this._workspaceRevision = snapshot.Revision;
            this._panelsInitialized = false;
            this.InitializePanels();
            this._progressText = "Workspace reset completed.";
            this.NotifySuccess("Workspace reset", this._progressText);
            this.StateHasChanged();
        }

        /// <summary>Attende una conferma nativa con ownership limitata all'apertura corrente</summary>
        /// <param name="title">Titolo del dialog</param>
        /// <param name="message">Messaggio da confermare</param>
        /// <param name="confirmText">Etichetta dell'azione confermata</param>
        /// <returns>True soltanto per conferma esplicita</returns>
        private async System.Threading.Tasks.Task<bool> ConfirmWorkspaceActionAsync(string title, string message, string confirmText)
        {
            if (this._isDisposed || this._workspaceConfirmationLifetime != null)
                return false;
            ConfirmOptions options = new ConfirmOptions
            {
                Width = "min(92vw, 48rem)",
                WrapperCssClass = "bivium-modal-layer",
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
            if (!this.CanMutateWorkspace())
                return;
            PanelState active = this.GetActivePanel();
            List<string> archivePaths = this.GetArchivePathsForOperation(active);

            if (archivePaths.Count == 0)
            {
                this.ShowOperationWarning("Extract unavailable", "Selected file is not a supported archive.");
                return;
            }

            string destinationDir = active.CurrentPath;
            CancellationToken revocationToken = this._workspaceService.GetRevocationToken(this.GetClientToken());
            CancellationTokenSource operationCancellation = this.BeginCancellableOperation(revocationToken);
            CancellationToken cancellationToken = operationCancellation.Token;

            // Show initial progress
            this._progressText = "Extracting...";

            Action<int, int, string> onProgress = this.CreateThrottledProgressCallback("Extracting");

            // Run on background thread
            Thread worker = new Thread(() =>
            {
                FileOperationResult result;
                try
                {
                    result = this.ExtractArchives(archivePaths, destinationDir, false, onProgress, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    result = FileOperationResult.Fail("Operation cancelled.");
                }

                _ = this.InvokeAsync(() =>
                {
                    this.EndCancellableOperation(operationCancellation);

                    if (this._isDisposed || revocationToken.IsCancellationRequested)
                    {
                        this._progressText = "";
                        return;
                    }

                    // A cancelled operation keeps what it already wrote, so the panels still need a refresh
                    this._progressText = cancellationToken.IsCancellationRequested ? "Operation cancelled, completed items kept." : "";
                    this.RefreshVisiblePanels();

                    if (cancellationToken.IsCancellationRequested)
                    {
                        this.NotifyWarning("Extraction cancelled", this._progressText);
                    }
                    else if (!result.Success)
                    {
                        this._pendingOperation = "";
                        this.ShowOperationError("Extraction failed", result.ErrorMessage);
                    }
                    else
                    {
                        this.NotifySuccess("Extraction completed", archivePaths.Count + " archive(s) processed.");
                    }

                    this.StateHasChanged();
                });
            });

            worker.IsBackground = true;
            worker.Start();
        }

        /// <summary>
        /// Extracts archive into a subfolder named after the archive
        /// </summary>
        private void DoExtractToFolder()
        {
            if (!this.CanMutateWorkspace())
                return;
            PanelState active = this.GetActivePanel();
            List<string> archivePaths = this.GetArchivePathsForOperation(active);

            if (archivePaths.Count == 0)
            {
                this.ShowOperationWarning("Extract unavailable", "Selected file is not a supported archive.");
                return;
            }

            string destinationDir = active.CurrentPath;
            CancellationToken revocationToken = this._workspaceService.GetRevocationToken(this.GetClientToken());
            CancellationTokenSource operationCancellation = this.BeginCancellableOperation(revocationToken);
            CancellationToken cancellationToken = operationCancellation.Token;

            // Show initial progress
            this._progressText = archivePaths.Count == 1 ? "Extracting to " + this.GetArchiveBaseName(Path.GetFileName(archivePaths[0])) + "/..." : "Extracting to */...";

            Action<int, int, string> onProgress = this.CreateThrottledProgressCallback("Extracting");

            // Run on background thread
            Thread worker = new Thread(() =>
            {
                FileOperationResult result;
                try
                {
                    result = this.ExtractArchives(archivePaths, destinationDir, true, onProgress, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    result = FileOperationResult.Fail("Operation cancelled.");
                }

                _ = this.InvokeAsync(() =>
                {
                    this.EndCancellableOperation(operationCancellation);

                    if (this._isDisposed || revocationToken.IsCancellationRequested)
                    {
                        this._progressText = "";
                        return;
                    }

                    // A cancelled operation keeps what it already wrote, so the panels still need a refresh
                    this._progressText = cancellationToken.IsCancellationRequested ? "Operation cancelled, completed items kept." : "";
                    this.RefreshVisiblePanels();

                    if (cancellationToken.IsCancellationRequested)
                    {
                        this.NotifyWarning("Extraction cancelled", this._progressText);
                    }
                    else if (!result.Success)
                    {
                        this._pendingOperation = "";
                        this.ShowOperationError("Extraction failed", result.ErrorMessage);
                    }
                    else
                    {
                        this.NotifySuccess("Extraction completed", archivePaths.Count + " archive(s) processed.");
                    }

                    this.StateHasChanged();
                });
            });

            worker.IsBackground = true;
            worker.Start();
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
        /// Checks if the active target can be compressed using in-memory panel state
        /// </summary>
        /// <returns>True if a selected or cursor target exists</returns>
        private bool CanCompressActiveTarget()
        {
            PanelState active = this.GetActivePanel();
            bool result = active.SelectedPaths.Count > 0 || (active.CursorIndex >= 0 && active.CursorIndex < active.Entries.Count);
            return result;
        }

        /// <summary>
        /// Checks if the active target can be extracted using in-memory panel state
        /// </summary>
        /// <returns>True if selection or cursor contains extractable archives</returns>
        private bool CanExtractActiveTarget()
        {
            PanelState active = this.GetActivePanel();
            bool result = false;

            if (active.SelectedPaths.Count > 0)
            {
                int archiveCount = 0;
                for (int i = 0; i < active.SelectedPaths.Count; i++)
                {
                    FileSystemEntry entry = this.FindEntryByPath(active, active.SelectedPaths[i]);
                    if (entry != null && !entry.IsDirectory && this._archiveService.IsArchive(entry.FullPath))
                    {
                        archiveCount++;
                    }
                }

                result = archiveCount == active.SelectedPaths.Count;
            }
            else if (active.CursorIndex >= 0 && active.CursorIndex < active.Entries.Count)
            {
                FileSystemEntry entry = active.Entries[active.CursorIndex];
                result = !entry.IsDirectory && this._archiveService.IsArchive(entry.FullPath);
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
                if (active.Entries[i].FullPath == path)
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
                if (active.Entries[i].FullPath == path)
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
        /// Extracts one or more archives
        /// </summary>
        /// <param name="archivePaths">Archive paths to extract</param>
        /// <param name="destinationDir">Destination directory</param>
        /// <param name="extractToOwnFolder">If true, each archive is extracted to its own folder</param>
        /// <param name="onProgress">Progress callback</param>
        /// <param name="cancellationToken">Cancellation token for lease revocation</param>
        /// <returns>Operation result</returns>
        private FileOperationResult ExtractArchives(List<string> archivePaths, string destinationDir, bool extractToOwnFolder, Action<int, int, string> onProgress, CancellationToken cancellationToken)
        {
            int processed = 0;

            for (int i = 0; i < archivePaths.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string archivePath = archivePaths[i];
                string currentDestination = destinationDir;

                if (extractToOwnFolder)
                {
                    string folderName = this.GetArchiveBaseName(Path.GetFileName(archivePath));
                    currentDestination = Path.Combine(destinationDir, folderName);

                    try
                    {
                        Directory.CreateDirectory(currentDestination);
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        return FileOperationResult.Fail("Access denied: " + ex.Message);
                    }
                    catch (IOException ex)
                    {
                        return FileOperationResult.Fail("I/O error: " + ex.Message);
                    }
                }

                FileOperationResult result = this._archiveService.ExtractArchive(archivePath, currentDestination, onProgress, cancellationToken);
                if (!result.Success)
                {
                    return result;
                }

                processed++;
            }

            return FileOperationResult.Ok(processed);
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
            if (!this.CanMutateWorkspace())
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

            this._compressDialog.Show(baseName);
        }

        /// <summary>
        /// Opens the advanced renamer dialog with selected files
        /// </summary>
        private void DoAdvancedRename()
        {
            if (!this.CanMutateWorkspace())
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
                    if (active.Entries[i].FullPath == selectedPath && active.Entries[i].IsDirectory)
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
                        if (active.Entries[i].FullPath == selectedPath && !active.Entries[i].IsDirectory)
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
                HashSet<string> selectedPathSet = new HashSet<string>(active.SelectedPaths);
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
        /// Handles confirm dialog result
        /// </summary>
        /// <param name="confirmed">True if confirmed</param>
        private void HandleConfirmDialogClose(bool confirmed)
        {
            if (confirmed && this._pendingOperation == "delete" && this.CanMutateWorkspace())
            {
                PanelState active = this.GetActivePanel();
                int deleteCount = active.SelectedPaths.Count;
                CancellationToken cancellationToken = this._workspaceService.GetRevocationToken(this.GetClientToken());
                FileOperationResult result;
                try
                {
                    result = this._fileOperationService.DeleteEntries(active.SelectedPaths, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    this._pendingOperation = "";
                    return;
                }

                active.SelectedPaths.Clear();
                this.LoadPanelContents(active);

                // Clamp cursor to valid range after deletion
                if (active.CursorIndex >= active.Entries.Count && active.Entries.Count > 0)
                {
                    active.CursorIndex = active.Entries.Count - 1;
                }

                // Select the entry at cursor position
                if (active.CursorIndex >= 0 && active.CursorIndex < active.Entries.Count)
                {
                    active.SelectedPaths.Add(active.Entries[active.CursorIndex].FullPath);
                }

                if (!result.Success)
                {
                    this._pendingOperation = "";
                    this.ShowOperationError("Delete failed", result.ErrorMessage);
                }
                else
                {
                    this.NotifySuccess("Delete completed", deleteCount + " item(s) deleted.");
                }
            }

            this._pendingOperation = "";
        }

        /// <summary>
        /// Handles overwrite dialog result during paste
        /// </summary>
        /// <param name="choice">Overwrite choice</param>
        private void HandleOverwriteDialogClose(OverwriteChoice choice)
        {
            if (!this.CanMutateWorkspace())
            {
                this.ClearPendingPaste();
                return;
            }

            if (this._pendingPasteConflictPaths.Count == 0 || this._pendingPasteConflictIndex >= this._pendingPasteConflictPaths.Count)
            {
                this.ClearPendingPaste();
                return;
            }

            string currentPath = this._pendingPasteConflictPaths[this._pendingPasteConflictIndex];

            if (choice == OverwriteChoice.Yes)
            {
                this._pendingPasteOverwritePaths.Add(currentPath);
                this._pendingPasteConflictIndex++;
            }
            else if (choice == OverwriteChoice.YesToAll)
            {
                for (int i = this._pendingPasteConflictIndex; i < this._pendingPasteConflictPaths.Count; i++)
                {
                    this._pendingPasteOverwritePaths.Add(this._pendingPasteConflictPaths[i]);
                }

                this._pendingPasteConflictIndex = this._pendingPasteConflictPaths.Count;
            }
            else
            {
                this._pendingPasteSkippedPaths.Add(currentPath);
                this._pendingPasteConflictIndex++;
            }

            this.ShowNextPasteOverwritePrompt();
        }

        /// <summary>
        /// Handles input dialog result
        /// </summary>
        /// <param name="value">Input value (empty if cancelled)</param>
        private void HandleInputDialogClose(string value)
        {
            if (!string.IsNullOrEmpty(value) && this.CanMutateWorkspace())
            {
                string pendingOperation = this._pendingOperation;
                PanelState active = this.GetActivePanel();
                FileOperationResult result = new FileOperationResult();

                if (this._pendingOperation == "rename")
                {
                    if (active.CursorIndex >= 0 && active.CursorIndex < active.Entries.Count)
                    {
                        string path = active.Entries[active.CursorIndex].FullPath;
                        result = this._fileOperationService.RenameEntry(path, value);
                    }
                }
                else if (this._pendingOperation == "mkdir")
                {
                    result = this._fileOperationService.CreateDirectory(active.CurrentPath, value);
                }
                else if (this._pendingOperation == "newfile")
                {
                    result = this._fileOperationService.CreateFile(active.CurrentPath, value);
                }

                this.LoadPanelContents(active);

                // Move cursor to the created/renamed entry and scroll to it
                if (result.Success)
                {
                    for (int i = 0; i < active.Entries.Count; i++)
                    {
                        if (active.Entries[i].Name == value)
                        {
                            this.SetPanelFocusFromIndex(active, i);
                            active.SelectedPaths.Clear();
                            active.SelectedPaths.Add(active.Entries[i].FullPath);
                            this._scrollAfterRender = true;
                            break;
                        }
                    }
                }

                if (!result.Success && !string.IsNullOrEmpty(result.ErrorMessage))
                {
                    this._pendingOperation = "";
                    this.ShowOperationError("File operation failed", result.ErrorMessage);
                }
                else if (result.Success)
                {
                    string summary = pendingOperation == "rename" ? "Rename completed" : pendingOperation == "mkdir" ? "Folder created" : "File created";
                    this.NotifySuccess(summary, value);
                }
            }

            this._pendingOperation = "";
        }

        /// <summary>
        /// Handles properties dialog close
        /// </summary>
        private void HandlePropertiesDialogClose()
        {
            // Properties is read-only, nothing to do
        }

        /// <summary>
        /// Handles permissions dialog close
        /// </summary>
        /// <param name="saved">True if permissions were saved</param>
        private void HandlePermissionsDialogClose(bool saved)
        {
            // Refresh active panel to reflect permission changes
            if (saved && this.CanMutateWorkspace())
            {
                PanelState active = this.GetActivePanel();
                this.LoadPanelContents(active);
                this.NotifySuccess("Permissions updated", active.Entries.Count + " item(s) reloaded.");
            }
        }

        /// <summary>
        /// Handles about dialog close
        /// </summary>
        private void HandleAboutDialogClose()
        {
        }

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

        /// <summary>
        /// Handles upload dialog close
        /// </summary>
        /// <param name="uploaded">True if file was uploaded</param>
        private void HandleUploadDialogClose(bool uploaded)
        {
            // Refresh both panels after upload
            if (uploaded && this.CanMutateWorkspace())
            {
                this.RefreshVisiblePanels();
                this.NotifySuccess("Upload completed", "Visible panels have been refreshed.");
                this.StateHasChanged();
            }
        }

        /// <summary>
        /// Handles compress dialog close and starts compression
        /// </summary>
        /// <param name="result">Tuple of selected format and output file name</param>
        private void HandleCompressDialogClose((ArchiveFormat Format, string OutputName) result)
        {
            // Empty name means cancelled
            if (string.IsNullOrEmpty(result.OutputName))
            {
                return;
            }
            if (!this.CanMutateWorkspace())
                return;

            PanelState active = this.GetActivePanel();
            List<string> paths = new List<string>(active.SelectedPaths);

            string outputName = result.OutputName.Trim();
            if (!this.IsValidOutputFileName(outputName))
            {
                this.ShowOperationWarning("Compression not started", "Archive name must be a file name, not a path.");
                return;
            }

            string outputPath = Path.Combine(active.CurrentPath, outputName);
            ArchiveFormat format = result.Format;
            CancellationToken revocationToken = this._workspaceService.GetRevocationToken(this.GetClientToken());
            CancellationTokenSource operationCancellation = this.BeginCancellableOperation(revocationToken);
            CancellationToken cancellationToken = operationCancellation.Token;

            // Show initial progress
            this._progressText = "Compressing...";

            Action<int, int, string> onProgress = this.CreateThrottledProgressCallback("Compressing");

            // Run on background thread
            Thread worker = new Thread(() =>
            {
                FileOperationResult opResult;
                try
                {
                    opResult = this._archiveService.CreateArchive(outputPath, paths, format, onProgress, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    opResult = FileOperationResult.Fail("Operation cancelled.");
                }

                _ = this.InvokeAsync(() =>
                {
                    this.EndCancellableOperation(operationCancellation);

                    if (this._isDisposed || revocationToken.IsCancellationRequested)
                    {
                        this._progressText = "";
                        return;
                    }

                    // A cancelled operation keeps the partial archive, so the panels still need a refresh
                    this._progressText = cancellationToken.IsCancellationRequested ? "Operation cancelled, incomplete archive kept." : "";
                    this.RefreshVisiblePanels();

                    if (cancellationToken.IsCancellationRequested)
                    {
                        this.NotifyWarning("Compression cancelled", this._progressText);
                    }
                    else if (!opResult.Success)
                    {
                        this._pendingOperation = "";
                        this.ShowOperationError("Compression failed", opResult.ErrorMessage);
                    }
                    else
                    {
                        this.NotifySuccess("Compression completed", outputName);
                    }

                    this.StateHasChanged();
                });
            });

            worker.IsBackground = true;
            worker.Start();
        }

        /// <summary>
        /// Validates a user-entered output file name
        /// </summary>
        /// <param name="fileName">File name to validate</param>
        /// <returns>True if the value is a plain file name</returns>
        private bool IsValidOutputFileName(string fileName)
        {
            bool result = !string.IsNullOrWhiteSpace(fileName)
                && fileName == Path.GetFileName(fileName)
                && fileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
            return result;
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

        /// <summary>
        /// Handles terminal state changes
        /// </summary>
        private void HandleTerminalStateChanged()
        {
            this.StateHasChanged();
        }

        #endregion

        #region Keyboard Handling

        /// <summary>
        /// Registers unload presence before exposing the interactive workspace
        /// </summary>
        private async System.Threading.Tasks.Task InitializeClientInteropAsync()
        {
            this._jsModule = await this.JSRuntime.InvokeAsync<IJSObjectReference>("import", "./js/interop.js?v=20260922-file-columns-v1");
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
            if (!this._hasActiveLease || !this._workspaceService.ValidateMutation(this.GetClientToken()))
                return;

            // Ctrl+?: about dialog
            if (key == "?" && ctrl && !alt)
            {
                this._aboutDialog.Show();
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

            // Ctrl+N: new file
            if (key == "n" && ctrl && !shift && !alt)
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

            // Ctrl+Shift+N: new folder
            if (key == "N" && ctrl && shift && !alt)
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
                this._contextMenuVisible = false;
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
        /// Mostra l'esito positivo conclusivo senza sostituire il testo della status bar
        /// </summary>
        /// <param name="summary">Titolo sintetico</param>
        /// <param name="detail">Dettaglio dell'esito</param>
        private void NotifySuccess(string summary, string detail)
        {
            this.NotificationService.Notify(NotificationSeverity.Success, summary, detail, 4000);
        }

        /// <summary>
        /// Mostra un warning conclusivo senza sostituire il testo della status bar
        /// </summary>
        /// <param name="summary">Titolo sintetico</param>
        /// <param name="detail">Dettaglio del warning</param>
        private void NotifyWarning(string summary, string detail)
        {
            this.NotificationService.Notify(NotificationSeverity.Warning, summary, detail, 6000);
        }

        /// <summary>
        /// Mostra un errore conclusivo senza sostituire il testo della status bar
        /// </summary>
        /// <param name="summary">Titolo sintetico</param>
        /// <param name="detail">Dettaglio dell'errore</param>
        private void NotifyError(string summary, string detail)
        {
            this.NotificationService.Notify(NotificationSeverity.Error, summary, detail, 8000);
        }

        /// <summary>
        /// Presenta un warning conclusivo con il canale previsto dalla variante UI
        /// </summary>
        /// <param name="summary">Titolo sintetico</param>
        /// <param name="detail">Dettaglio del warning</param>
        private void ShowOperationWarning(string summary, string detail)
        {
            this.NotifyWarning(summary, detail);
        }

        /// <summary>
        /// Presenta un errore conclusivo con il canale previsto dalla variante UI
        /// </summary>
        /// <param name="summary">Titolo sintetico</param>
        /// <param name="detail">Dettaglio dell'errore</param>
        private void ShowOperationError(string summary, string detail)
        {
            this.NotifyError(summary, detail);
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
        /// Distingue la navigazione ordinaria dai trasferimenti tra gli stack della cronologia
        /// </summary>
        private enum PanelNavigationKind
        {
            /// <summary>
            /// Nuova destinazione
            /// </summary>
            Ordinary,

            /// <summary>
            /// Destinazione precedente
            /// </summary>
            Back,

            /// <summary>
            /// Destinazione successiva
            /// </summary>
            Forward
        }
    }
}
