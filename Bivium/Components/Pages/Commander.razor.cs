using Microsoft.AspNetCore.Components;
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

        #endregion

        #region Constants

        /// <summary>
        /// Maximum file size for editor (5 MB)
        /// </summary>
        private const long MAX_EDITOR_SIZE = 5L * 1024 * 1024;

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

        #endregion

        #region Overrides

        /// <summary>
        /// Initialize panels with user home directory
        /// </summary>
        protected override void OnInitialized()
        {
            // BIVIUM_HOME environment variable overrides default home
            string homePath = Environment.GetEnvironmentVariable("BIVIUM_HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            this._leftPanel = new PanelState(homePath);
            this._rightPanel = new PanelState(homePath);

            this.LoadPanelContents(this._leftPanel);
            this.LoadPanelContents(this._rightPanel);
        }

        /// <summary>
        /// Setup keyboard capture after first render
        /// </summary>
        protected override void OnAfterRender(bool firstRender)
        {
            if (firstRender)
            {
                _ = this.InitializeKeyboardCapture();
            }
        }

        #endregion

        #region Panel Navigation

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
        private void HandleContextMenuRequest(ContextMenuEventArgs args)
        {
            this._contextMenuX = args.X;
            this._contextMenuY = args.Y;
            this._contextMenuVisible = true;

            // Determine context flags for archive/compress menu items
            PanelState active = this.GetActivePanel();
            this._contextMenuHasSelection = active.SelectedPaths.Count > 0;
            this._contextMenuIsArchive = false;

            this._contextMenuArchiveBaseName = "";

            if (active.CursorIndex >= 0 && active.CursorIndex < active.Entries.Count)
            {
                FileSystemEntry entry = active.Entries[active.CursorIndex];
                if (!entry.IsDirectory)
                {
                    this._contextMenuIsArchive = this._archiveService.IsArchive(entry.FullPath);
                    if (this._contextMenuIsArchive)
                    {
                        // Strip archive extensions (.tar.gz, .tar.bz2, etc.)
                        string name = entry.Name;
                        string lower = name.ToLowerInvariant();
                        string[] doubleExts = { ".tar.gz", ".tar.bz2", ".tar.xz", ".tar.zst" };
                        bool found = false;
                        foreach (string ext in doubleExts)
                        {
                            if (lower.EndsWith(ext))
                            {
                                this._contextMenuArchiveBaseName = name.Substring(0, name.Length - ext.Length);
                                found = true;
                                break;
                            }
                        }
                        if (!found)
                        {
                            this._contextMenuArchiveBaseName = Path.GetFileNameWithoutExtension(name);
                        }
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
            if (active.CursorIndex >= 0 && active.CursorIndex < active.Entries.Count)
            {
                FileSystemEntry entry = active.Entries[active.CursorIndex];

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
                        string content = this._fileOperationService.ReadFileText(entry.FullPath, MAX_EDITOR_SIZE);
                        this._editorDialog.Show(entry.FullPath, content);
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
            this._clipboard.Paths = new List<string>(active.SelectedPaths);
            this._clipboard.IsCut = false;
        }

        /// <summary>
        /// Cuts selected entries to internal clipboard
        /// </summary>
        private void DoCut()
        {
            PanelState active = this.GetActivePanel();
            this._clipboard.Paths = new List<string>(active.SelectedPaths);
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

                // Show initial progress
                this._progressText = isCut ? "Moving..." : "Copying...";

                // Progress callback updates status bar
                Action<int, int, string> onProgress = (current, total, fileName) =>
                {
                    this._progressText = (isCut ? "Moving" : "Copying") + " " + current + "/" + total + ": " + fileName;
                    _ = this.InvokeAsync(() => this.StateHasChanged());
                };

                // Run file operation on background thread
                Thread worker = new Thread(() =>
                {
                    FileOperationResult result;

                    if (isCut)
                    {
                        result = this._fileOperationService.MoveEntriesWithProgress(paths, destinationDir, onProgress);
                    }
                    else
                    {
                        result = this._fileOperationService.CopyEntriesWithProgress(paths, destinationDir, onProgress);
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

                        // Refresh both panels after paste
                        this.LoadPanelContents(this._leftPanel);
                        this.LoadPanelContents(this._rightPanel);

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
            if (active.CursorIndex >= 0 && active.CursorIndex < active.Entries.Count)
            {
                FileSystemEntry entry = active.Entries[active.CursorIndex];
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
            if (active.SelectedPaths.Count > 0)
            {
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
            if (active.CursorIndex >= 0 && active.CursorIndex < active.Entries.Count)
            {
                FileSystemEntry entry = active.Entries[active.CursorIndex];
                string url;

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

                // Trigger download via JS navigation
                _ = this.JSRuntime.InvokeVoidAsync("open", url, "_blank");
            }
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
        /// Shows properties dialog for the selected entry
        /// </summary>
        private void DoProperties()
        {
            PanelState active = this.GetActivePanel();
            if (active.CursorIndex >= 0 && active.CursorIndex < active.Entries.Count)
            {
                FileSystemEntry entry = active.Entries[active.CursorIndex];
                this._propertiesDialog.Show(entry);
            }
        }

        /// <summary>
        /// Shows permissions dialog for the selected entry
        /// </summary>
        private void DoPermissions()
        {
            PanelState active = this.GetActivePanel();
            if (active.CursorIndex >= 0 && active.CursorIndex < active.Entries.Count)
            {
                FileSystemEntry entry = active.Entries[active.CursorIndex];
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

            if (active.CursorIndex < 0 || active.CursorIndex >= active.Entries.Count)
            {
                return;
            }

            FileSystemEntry entry = active.Entries[active.CursorIndex];

            if (entry.IsDirectory || !this._archiveService.IsArchive(entry.FullPath))
            {
                this._confirmDialog.Show("Extract", "Selected file is not a supported archive.", "OK", "");
                return;
            }

            string archivePath = entry.FullPath;
            string destinationDir = active.CurrentPath;

            // Show initial progress
            this._progressText = "Extracting...";

            // Progress callback
            Action<int, int, string> onProgress = (current, total, fileName) =>
            {
                this._progressText = "Extracting " + current + "/" + total + ": " + fileName;
                _ = this.InvokeAsync(() => this.StateHasChanged());
            };

            // Run on background thread
            Thread worker = new Thread(() =>
            {
                FileOperationResult result = this._archiveService.ExtractArchive(archivePath, destinationDir, onProgress);

                _ = this.InvokeAsync(() =>
                {
                    this._progressText = "";
                    this.LoadPanelContents(this._leftPanel);
                    this.LoadPanelContents(this._rightPanel);

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

            if (active.CursorIndex < 0 || active.CursorIndex >= active.Entries.Count)
            {
                return;
            }

            FileSystemEntry entry = active.Entries[active.CursorIndex];

            if (entry.IsDirectory || !this._archiveService.IsArchive(entry.FullPath))
            {
                this._confirmDialog.Show("Extract", "Selected file is not a supported archive.", "OK", "");
                return;
            }

            string archivePath = entry.FullPath;
            string folderName = this._contextMenuArchiveBaseName;
            string destinationDir = Path.Combine(active.CurrentPath, folderName);

            // Create the destination folder
            FileOperationResult dirResult = this._fileOperationService.CreateDirectory(active.CurrentPath, folderName);
            if (!dirResult.Success)
            {
                this._confirmDialog.Show("Error", dirResult.ErrorMessage, "OK", "");
                return;
            }

            // Show initial progress
            this._progressText = "Extracting to " + folderName + "/...";

            // Progress callback
            Action<int, int, string> onProgress = (current, total, fileName) =>
            {
                this._progressText = "Extracting " + current + "/" + total + ": " + fileName;
                _ = this.InvokeAsync(() => this.StateHasChanged());
            };

            // Run on background thread
            Thread worker = new Thread(() =>
            {
                FileOperationResult result = this._archiveService.ExtractArchive(archivePath, destinationDir, onProgress);

                _ = this.InvokeAsync(() =>
                {
                    this._progressText = "";
                    this.LoadPanelContents(this._leftPanel);
                    this.LoadPanelContents(this._rightPanel);

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
        /// Shows the compress dialog for the selected entries
        /// </summary>
        private void DoCompress()
        {
            PanelState active = this.GetActivePanel();

            if (active.SelectedPaths.Count == 0)
            {
                return;
            }

            // Suggest a base name from selection
            string baseName = "archive";

            if (active.SelectedPaths.Count == 1)
            {
                baseName = Path.GetFileNameWithoutExtension(active.SelectedPaths[0]);

                // For directories, use the directory name
                if (Directory.Exists(active.SelectedPaths[0]))
                {
                    baseName = Path.GetFileName(active.SelectedPaths[0]);
                }
            }

            this._compressDialog.Show(baseName);
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

                if (!result.Success)
                {
                    this._pendingOperation = "";
                    this._confirmDialog.Show("Error", result.ErrorMessage, "OK", "");
                }
            }

            this._pendingOperation = "";
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
        /// Handles editor save event
        /// </summary>
        /// <param name="args">Tuple of file path and content</param>
        private void HandleEditorSave((string FilePath, string Content) args)
        {
            FileOperationResult result = this._fileOperationService.WriteFileText(args.FilePath, args.Content);
            if (!result.Success)
            {
                this._confirmDialog.Show("Save Error", result.ErrorMessage, "OK", "");
            }
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
                this.LoadPanelContents(this._leftPanel);
                this.LoadPanelContents(this._rightPanel);
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
            string outputPath = Path.Combine(active.CurrentPath, result.OutputName);
            ArchiveFormat format = result.Format;

            // Show initial progress
            this._progressText = "Compressing...";

            // Progress callback
            Action<int, int, string> onProgress = (current, total, fileName) =>
            {
                this._progressText = "Compressing " + current + "/" + total + ": " + fileName;
                _ = this.InvokeAsync(() => this.StateHasChanged());
            };

            // Run on background thread
            Thread worker = new Thread(() =>
            {
                FileOperationResult opResult = this._archiveService.CreateArchive(outputPath, paths, format, onProgress);

                _ = this.InvokeAsync(() =>
                {
                    this._progressText = "";
                    this.LoadPanelContents(this._leftPanel);
                    this.LoadPanelContents(this._rightPanel);

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
        /// Handles settings dialog close
        /// </summary>
        private void HandleSettingsDialogClose()
        {
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

            // Tab: switch panels
            if (key == "Tab" && !ctrl && !shift && !alt)
            {
                this._activePanel = this._activePanel == 0 ? 1 : 0;
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
                    // Position context menu at a default location
                    this._contextMenuX = 100;
                    this._contextMenuY = 100;
                    this._contextMenuVisible = true;
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
                _ = this._jsModule.InvokeVoidAsync("scrollCursorIntoView");
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
        }

        #endregion
    }
}
