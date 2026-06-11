using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using Bivium.Models;
using Bivium.Services;
using Bivium.Components.Shared;
using Bivium.Components.Panel;

namespace Bivium.Components.Pages
{
    /// <summary>
    /// Main commander page - orchestrates dual-panel file manager
    /// </summary>
    public partial class Commander : ComponentBase, IDisposable
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
        /// Source file paths that have an overwrite conflict
        /// </summary>
        private List<string> _pendingPasteConflictPaths = new List<string>();

        /// <summary>
        /// Source file paths approved for overwrite
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
        /// Whether keyboard capture has been initialized
        /// </summary>
        private bool _keyboardInitialized = false;

        #endregion

        #region Overrides

        /// <summary>
        /// Initialize panels with user home directory
        /// </summary>
        protected override async System.Threading.Tasks.Task OnInitializedAsync()
        {
            this._settingsChangeSubscription = this._settings.OnChange(this.HandleSettingsChanged);
            await this.RefreshAuthenticationState();
        }

        /// <summary>
        /// Setup keyboard capture after first render
        /// </summary>
        protected override void OnAfterRender(bool firstRender)
        {
            if (!this._keyboardInitialized && this._canAccess)
            {
                this._keyboardInitialized = true;
                _ = this.InitializeKeyboardCapture();
            }

            // Scroll cursor into view after DOM is ready
            if (this._scrollAfterRender)
            {
                this._scrollAfterRender = false;
                if (this._jsModule != null)
                {
                    _ = this._jsModule.InvokeVoidAsync("scrollCursorIntoView", this.GetActivePanel().CursorIndex);
                }
            }
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
            _ = this.InvokeAsync(async () => { await this.RefreshAuthenticationState(); this.StateHasChanged(); });
        }

        #endregion

        #region Panel Navigation

        /// <summary>
        /// Initializes panels once access is allowed
        /// </summary>
        private void InitializePanels()
        {
            if (this._panelsInitialized)
            {
                return;
            }

            string homePath = Environment.GetEnvironmentVariable("BIVIUM_HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            this._leftPanel = new PanelState(homePath);
            this._rightPanel = new PanelState(homePath);
            this.RefreshVisiblePanels();
            this._panelsInitialized = true;
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
            this._activePanel = index;
        }

        /// <summary>
        /// Navigates the active panel to a new directory
        /// </summary>
        /// <param name="path">New directory path</param>
        private void NavigateActivePanel(string path)
        {
            PanelState active = this.GetActivePanel();
            active.CurrentPath = path;
            active.CursorIndex = 0;
            active.SelectedPaths.Clear();
            this.LoadPanelContents(active);
        }

        /// <summary>
        /// Handles navigation in the left panel
        /// </summary>
        /// <param name="path">New directory path</param>
        private void HandleLeftNavigate(string path)
        {
            this._leftPanel.CurrentPath = path;
            this._leftPanel.CursorIndex = 0;
            this._leftPanel.SelectedPaths.Clear();
            this.LoadPanelContents(this._leftPanel);
        }

        /// <summary>
        /// Handles navigation in the right panel
        /// </summary>
        /// <param name="path">New directory path</param>
        private void HandleRightNavigate(string path)
        {
            this._rightPanel.CurrentPath = path;
            this._rightPanel.CursorIndex = 0;
            this._rightPanel.SelectedPaths.Clear();
            this.LoadPanelContents(this._rightPanel);
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
            this._leftPanel.CurrentSort = sort;
            this.SortEntries(this._leftPanel);
        }

        /// <summary>
        /// Handles sort change in the right panel
        /// </summary>
        /// <param name="sort">New sort configuration</param>
        private void HandleRightSortChanged(SortColumn sort)
        {
            this._rightPanel.CurrentSort = sort;
            this.SortEntries(this._rightPanel);
        }

        /// <summary>
        /// Handles cursor change in the left panel
        /// </summary>
        /// <param name="index">New cursor index</param>
        private void HandleLeftCursorChanged(int index)
        {
            this._leftPanel.CursorIndex = index;
        }

        /// <summary>
        /// Handles cursor change in the right panel
        /// </summary>
        /// <param name="index">New cursor index</param>
        private void HandleRightCursorChanged(int index)
        {
            this._rightPanel.CursorIndex = index;
        }

        #endregion

        #region Panel Data

        /// <summary>
        /// Loads directory contents into a panel
        /// </summary>
        /// <param name="panel">Panel to load</param>
        private void LoadPanelContents(PanelState panel)
        {
            panel.Entries = this._fileSystemService.GetDirectoryContents(panel.CurrentPath);
            this.SortEntries(panel);
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

                this._leftPanel.Entries = new List<FileSystemEntry>(entries);
                this.SortEntries(this._leftPanel);

                this._rightPanel.Entries = new List<FileSystemEntry>(entries);
                this.SortEntries(this._rightPanel);
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
        private void SortEntries(PanelState panel)
        {
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
        /// Opens the selected entry (navigates into directory)
        /// </summary>
        private void DoOpen()
        {
            PanelState active = this.GetActivePanel();
            if (active.CursorIndex >= 0 && active.CursorIndex < active.Entries.Count)
            {
                FileSystemEntry entry = active.Entries[active.CursorIndex];
                if (entry.IsDirectory)
                {
                    this.NavigateActivePanel(entry.FullPath);
                }
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
            {
                // Only edit files, not directories
                if (!entry.IsDirectory)
                {
                    // Check extension is in editable list
                    string ext = Path.GetExtension(entry.Name).ToLowerInvariant();
                    List<string> editableExtensions = this._settings.CurrentValue.EditableExtensions;
                    bool isEditable = false;
                    for (int i = 0; i < editableExtensions.Count; i++)
                    {
                        if (editableExtensions[i].ToLowerInvariant() == ext)
                        {
                            isEditable = true;
                            break;
                        }
                    }

                    if (!isEditable)
                    {
                        this._confirmDialog.Show("Edit", "Extension '" + ext + "' is not in the editable extensions list.", "OK", "");
                    }
                    else if (entry.SizeBytes > MAX_EDITOR_SIZE)
                    {
                        this._confirmDialog.Show("Edit", "File is too large to edit (max 5 MB).", "OK", "");
                    }
                    else
                    {
                        // Read file content and open editor
                        FileTextResult readResult = this._fileOperationService.ReadFileText(entry.FullPath, MAX_EDITOR_SIZE);
                        if (readResult.Success)
                        {
                            this._editorDialog.Show(entry.FullPath, readResult.Content);
                        }
                        else
                        {
                            this._confirmDialog.Show("Edit", readResult.ErrorMessage, "OK", "");
                        }
                    }
                }
            }
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

                List<string> conflictPaths = this.GetPasteFileConflicts(paths, destinationDir);
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
        /// Starts a paste operation after overwrite decisions have been resolved
        /// </summary>
        /// <param name="paths">Source paths to process</param>
        /// <param name="destinationDir">Destination directory</param>
        /// <param name="isCut">True when moving, false when copying</param>
        /// <param name="overwritePaths">Source paths approved for overwrite</param>
        private void StartPasteOperation(List<string> paths, string destinationDir, bool isCut, List<string> overwritePaths)
        {
            if (paths.Count == 0)
            {
                return;
            }

            List<string> operationPaths = new List<string>(paths);
            List<string> operationOverwritePaths = new List<string>(overwritePaths);

            // Show initial progress
            this._progressText = isCut ? "Moving..." : "Copying...";

            Action<int, int, string> onProgress = this.CreateThrottledProgressCallback(isCut ? "Moving" : "Copying");

            // Run file operation on background thread
            Thread worker = new Thread(() =>
            {
                FileOperationResult result;

                if (isCut)
                {
                    result = this._fileOperationService.MoveEntriesWithProgress(operationPaths, destinationDir, onProgress, operationOverwritePaths);
                }
                else
                {
                    result = this._fileOperationService.CopyEntriesWithProgress(operationPaths, destinationDir, onProgress, operationOverwritePaths);
                }

                // Update UI on the render thread
                _ = this.InvokeAsync(() =>
                {
                    // Clear clipboard on successful cut
                    if (isCut && result.Success)
                    {
                        this._clipboard.Clear();
                    }

                    // Clear progress text
                    this._progressText = "";

                    this.RefreshVisiblePanels();

                    if (!result.Success)
                    {
                        this._pendingOperation = "";
                        this._confirmDialog.Show("Error", result.ErrorMessage, "OK", "");
                    }

                    this.StateHasChanged();
                });
            });

            worker.IsBackground = true;
            worker.Start();
        }

        /// <summary>
        /// Finds paste file conflicts that need an overwrite decision
        /// </summary>
        /// <param name="paths">Source paths from clipboard</param>
        /// <param name="destinationDir">Destination directory</param>
        /// <returns>Source file paths with destination conflicts</returns>
        private List<string> GetPasteFileConflicts(List<string> paths, string destinationDir)
        {
            List<string> result = new List<string>();

            for (int i = 0; i < paths.Count; i++)
            {
                string source = paths[i];
                if (!File.Exists(source))
                {
                    continue;
                }

                string destPath = Path.Combine(destinationDir, Path.GetFileName(source));
                if (!this.AreSamePath(source, destPath) && File.Exists(destPath))
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
        /// <param name="conflictPaths">Source file paths with conflicts</param>
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
            string message = "A file named '" + Path.GetFileName(sourcePath) + "' already exists in the destination.\n\n"
                + "Source: " + sourcePath + "\n"
                + "Destination: " + destinationPath + "\n\n"
                + "Overwrite it?";

            this._overwriteDialog.Show("Overwrite file", message);
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
            bool result = string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                this.GetPathComparison());
            return result;
        }

        /// <summary>
        /// Gets the appropriate path comparison for the current platform
        /// </summary>
        /// <returns>String comparison for filesystem paths</returns>
        private StringComparison GetPathComparison()
        {
            StringComparison result = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return result;
        }

        /// <summary>
        /// Selects all entries in the active panel
        /// </summary>
        private void DoSelectAll()
        {
            PanelState active = this.GetActivePanel();
            active.SelectedPaths.Clear();
            for (int i = 0; i < active.Entries.Count; i++)
            {
                active.SelectedPaths.Add(active.Entries[i].FullPath);
            }
        }

        /// <summary>
        /// Initiates new file creation with input dialog
        /// </summary>
        private void DoNewFile()
        {
            this._pendingOperation = "newfile";
            this._inputDialog.Show("New File", "File name:", "");
        }

        /// <summary>
        /// Initiates new folder creation with input dialog
        /// </summary>
        private void DoNewFolder()
        {
            this._pendingOperation = "mkdir";
            this._inputDialog.Show("New Folder", "Folder name:", "");
        }

        /// <summary>
        /// Initiates rename with input dialog
        /// </summary>
        private void DoRename()
        {
            PanelState active = this.GetActivePanel();
            FileSystemEntry entry = this.GetSingleTargetEntry(active, "Rename");
            if (entry != null)
            {
                this._pendingOperation = "rename";
                this._inputDialog.Show("Rename", "New name:", entry.Name);
            }
        }

        /// <summary>
        /// Initiates delete with confirmation dialog
        /// </summary>
        private void DoDelete()
        {
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
        private void DoToggleTerminal()
        {
            if (this._terminalPanel != null)
            {
                this._terminalPanel.Toggle();
            }
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
                this._jsModule = await this.JSRuntime.InvokeAsync<IJSObjectReference>("import", "./js/interop.js");
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
        /// Extracts the selected archive file into the current directory
        /// </summary>
        private void DoExtract()
        {
            PanelState active = this.GetActivePanel();
            List<string> archivePaths = this.GetArchivePathsForOperation(active);

            if (archivePaths.Count == 0)
            {
                this._confirmDialog.Show("Extract", "Selected file is not a supported archive.", "OK", "");
                return;
            }

            string destinationDir = active.CurrentPath;

            // Show initial progress
            this._progressText = "Extracting...";

            Action<int, int, string> onProgress = this.CreateThrottledProgressCallback("Extracting");

            // Run on background thread
            Thread worker = new Thread(() =>
            {
                FileOperationResult result = this.ExtractArchives(archivePaths, destinationDir, false, onProgress);

                _ = this.InvokeAsync(() =>
                {
                    this._progressText = "";
                    this.RefreshVisiblePanels();

                    if (!result.Success)
                    {
                        this._pendingOperation = "";
                        this._confirmDialog.Show("Error", result.ErrorMessage, "OK", "");
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
            PanelState active = this.GetActivePanel();
            List<string> archivePaths = this.GetArchivePathsForOperation(active);

            if (archivePaths.Count == 0)
            {
                this._confirmDialog.Show("Extract", "Selected file is not a supported archive.", "OK", "");
                return;
            }

            string destinationDir = active.CurrentPath;

            // Show initial progress
            this._progressText = archivePaths.Count == 1
                ? "Extracting to " + this.GetArchiveBaseName(Path.GetFileName(archivePaths[0])) + "/..."
                : "Extracting to */...";

            Action<int, int, string> onProgress = this.CreateThrottledProgressCallback("Extracting");

            // Run on background thread
            Thread worker = new Thread(() =>
            {
                FileOperationResult result = this.ExtractArchives(archivePaths, destinationDir, true, onProgress);

                _ = this.InvokeAsync(() =>
                {
                    this._progressText = "";
                    this.RefreshVisiblePanels();

                    if (!result.Success)
                    {
                        this._pendingOperation = "";
                        this._confirmDialog.Show("Error", result.ErrorMessage, "OK", "");
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
        /// <returns>Operation result</returns>
        private FileOperationResult ExtractArchives(List<string> archivePaths, string destinationDir, bool extractToOwnFolder, Action<int, int, string> onProgress)
        {
            int processed = 0;

            for (int i = 0; i < archivePaths.Count; i++)
            {
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

                FileOperationResult result = this._archiveService.ExtractArchive(archivePath, currentDestination, onProgress);
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
            if (confirmed && this._pendingOperation == "delete")
            {
                PanelState active = this.GetActivePanel();
                FileOperationResult result = this._fileOperationService.DeleteEntries(active.SelectedPaths);

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
                    this._confirmDialog.Show("Error", result.ErrorMessage, "OK", "");
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
            if (!string.IsNullOrEmpty(value))
            {
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
                            active.CursorIndex = i;
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
                    this._confirmDialog.Show("Error", result.ErrorMessage, "OK", "");
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
            if (saved)
            {
                PanelState active = this.GetActivePanel();
                this.LoadPanelContents(active);
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
            if (saved)
            {
                PanelState active = this.GetActivePanel();
                this.LoadPanelContents(active);
            }
        }

        /// <summary>
        /// Handles upload dialog close
        /// </summary>
        /// <param name="uploaded">True if file was uploaded</param>
        private void HandleUploadDialogClose(bool uploaded)
        {
            // Refresh both panels after upload
            if (uploaded)
            {
                this.RefreshVisiblePanels();
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

            PanelState active = this.GetActivePanel();
            List<string> paths = new List<string>(active.SelectedPaths);

            string outputName = result.OutputName.Trim();
            if (!this.IsValidOutputFileName(outputName))
            {
                this._confirmDialog.Show("Compress", "Archive name must be a file name, not a path.", "OK", "");
                return;
            }

            string outputPath = Path.Combine(active.CurrentPath, outputName);
            ArchiveFormat format = result.Format;

            // Show initial progress
            this._progressText = "Compressing...";

            Action<int, int, string> onProgress = this.CreateThrottledProgressCallback("Compressing");

            // Run on background thread
            Thread worker = new Thread(() =>
            {
                FileOperationResult opResult = this._archiveService.CreateArchive(outputPath, paths, format, onProgress);

                _ = this.InvokeAsync(() =>
                {
                    this._progressText = "";
                    this.RefreshVisiblePanels();

                    if (!opResult.Success)
                    {
                        this._pendingOperation = "";
                        this._confirmDialog.Show("Error", opResult.ErrorMessage, "OK", "");
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
            if (renamed)
            {
                this.RefreshVisiblePanels();
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

        /// <summary>
        /// Initializes the JS keyboard capture
        /// </summary>
        private async System.Threading.Tasks.Task InitializeKeyboardCapture()
        {
            this._dotNetRef = DotNetObjectReference.Create(this);
            this._jsModule = await this.JSRuntime.InvokeAsync<IJSObjectReference>("import", "./js/interop.js");
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
        public void OnKeyDown(string key, bool ctrl, bool shift, bool alt)
        {
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
                this.DoToggleTerminal();
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
                if (active.CursorIndex > 0)
                {
                    active.CursorIndex--;
                    if (!shift)
                    {
                        active.SelectedPaths.Clear();
                    }
                    if (active.CursorIndex < active.Entries.Count)
                    {
                        string path = active.Entries[active.CursorIndex].FullPath;
                        if (!active.SelectedPaths.Contains(path))
                        {
                            active.SelectedPaths.Add(path);
                        }
                    }
                    this.StateHasChangedAndScroll();
                }
                return;
            }

            // Arrow Down: move cursor down
            if (key == "ArrowDown" && !ctrl && !alt)
            {
                PanelState active = this.GetActivePanel();
                if (active.CursorIndex < active.Entries.Count - 1)
                {
                    active.CursorIndex++;
                    if (!shift)
                    {
                        active.SelectedPaths.Clear();
                    }
                    if (active.CursorIndex < active.Entries.Count)
                    {
                        string path = active.Entries[active.CursorIndex].FullPath;
                        if (!active.SelectedPaths.Contains(path))
                        {
                            active.SelectedPaths.Add(path);
                        }
                    }
                    this.StateHasChangedAndScroll();
                }
                return;
            }

            // Home: cursor to first entry
            if (key == "Home" && !ctrl && !shift && !alt)
            {
                PanelState active = this.GetActivePanel();
                active.CursorIndex = 0;
                active.SelectedPaths.Clear();
                if (active.Entries.Count > 0)
                {
                    active.SelectedPaths.Add(active.Entries[0].FullPath);
                }
                this.StateHasChangedAndScroll();
                return;
            }

            // End: cursor to last entry
            if (key == "End" && !ctrl && !shift && !alt)
            {
                PanelState active = this.GetActivePanel();
                if (active.Entries.Count > 0)
                {
                    active.CursorIndex = active.Entries.Count - 1;
                    active.SelectedPaths.Clear();
                    active.SelectedPaths.Add(active.Entries[active.CursorIndex].FullPath);
                }
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
                int pageSize = 20;
                active.CursorIndex = Math.Max(0, active.CursorIndex - pageSize);
                if (!shift)
                {
                    active.SelectedPaths.Clear();
                }
                if (active.CursorIndex < active.Entries.Count)
                {
                    string path = active.Entries[active.CursorIndex].FullPath;
                    if (!active.SelectedPaths.Contains(path))
                    {
                        active.SelectedPaths.Add(path);
                    }
                }
                this.StateHasChangedAndScroll();
                return;
            }

            // PageDown: jump cursor down by visible page
            if (key == "PageDown" && !ctrl && !alt)
            {
                PanelState active = this.GetActivePanel();
                int pageSize = 20;
                active.CursorIndex = Math.Min(active.Entries.Count - 1, active.CursorIndex + pageSize);
                if (!shift)
                {
                    active.SelectedPaths.Clear();
                }
                if (active.CursorIndex >= 0 && active.CursorIndex < active.Entries.Count)
                {
                    string path = active.Entries[active.CursorIndex].FullPath;
                    if (!active.SelectedPaths.Contains(path))
                    {
                        active.SelectedPaths.Add(path);
                    }
                }
                this.StateHasChangedAndScroll();
                return;
            }

            // Single letter/digit: jump to first matching entry
            if (key.Length == 1 && !ctrl && !alt)
            {
                char ch = key[0];
                bool isLetterOrDigit = (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9');

                if (isLetterOrDigit)
                {
                    PanelState active = this.GetActivePanel();

                    for (int i = 0; i < active.Entries.Count; i++)
                    {
                        if (active.Entries[i].Name.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                        {
                            active.CursorIndex = i;
                            active.SelectedPaths.Clear();
                            active.SelectedPaths.Add(active.Entries[i].FullPath);
                            this.StateHasChangedAndScroll();
                            break;
                        }
                    }

                    return;
                }
            }
        }

        /// <summary>
        /// Updates UI and scrolls the cursor row into view
        /// </summary>
        private void StateHasChangedAndScroll()
        {
            this.StateHasChanged();

            // Scroll cursor into view after render
            if (this._jsModule != null)
            {
                _ = this._jsModule.InvokeVoidAsync("scrollCursorIntoView", this.GetActivePanel().CursorIndex);
            }
        }

        #endregion

        #region IDisposable

        /// <summary>
        /// Cleanup JS interop references
        /// </summary>
        public void Dispose()
        {
            if (this._dotNetRef != null)
            {
                this._dotNetRef.Dispose();
            }
            if (this._settingsChangeSubscription != null)
            {
                this._settingsChangeSubscription.Dispose();
            }
        }

        #endregion
    }
}
