using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Bivium.Models;
using Bivium.Services;

namespace Bivium.Components.Panel
{
    /// <summary>
    /// A single file panel containing tree view and file list
    /// </summary>
    public partial class FilePanel : ComponentBase, IAsyncDisposable
    {
        #region Injected Services

        /// <summary>
        /// File system service for autocomplete
        /// </summary>
        [Inject]
        private IFileSystemService _fileSystemService { get; set; }

        /// <summary>
        /// JS runtime for focusing input
        /// </summary>
        [Inject]
        private IJSRuntime _jsRuntime { get; set; }

        #endregion

        #region Parameters

        /// <summary>
        /// Panel state (current path, entries, selection, sort)
        /// </summary>
        [Parameter]
        public PanelState State { get; set; }

        /// <summary>
        /// Whether this panel is currently active/focused
        /// </summary>
        [Parameter]
        public bool IsActive { get; set; } = false;

        /// <summary>
        /// Unique identifier for this panel
        /// </summary>
        [Parameter]
        public string PanelId { get; set; } = "";

        /// <summary>
        /// Callback when user navigates to a directory
        /// </summary>
        [Parameter]
        public EventCallback<string> OnNavigate { get; set; }

        /// <summary>
        /// Callback when selection changes
        /// </summary>
        [Parameter]
        public EventCallback<List<string>> OnSelectionChanged { get; set; }

        /// <summary>
        /// Callback when sort changes
        /// </summary>
        [Parameter]
        public EventCallback<SortColumn> OnSortChanged { get; set; }

        /// <summary>
        /// Callback when cursor index changes
        /// </summary>
        [Parameter]
        public EventCallback<int> OnCursorChanged { get; set; }

        /// <summary>
        /// Callback when panel is clicked (to activate it)
        /// </summary>
        [Parameter]
        public EventCallback OnActivated { get; set; }

        /// <summary>
        /// Callback when context menu is requested
        /// </summary>
        [Parameter]
        public EventCallback<ContextMenuEventArgs> OnContextMenu { get; set; }

        /// <summary>
        /// Callback when the semantic file-list scroll anchor changes
        /// </summary>
        [Parameter]
        public EventCallback<string> OnScrollAnchorChanged { get; set; }

        /// <summary>
        /// Callback when directory-tree expansion changes
        /// </summary>
        [Parameter]
        public EventCallback OnTreeExpansionChanged { get; set; }

        /// <summary>
        /// Callback when the number of file rows visible in the panel changes
        /// </summary>
        [Parameter]
        public EventCallback<int> OnPageSizeChanged { get; set; }

        #endregion

        #region Class Variables

        /// <summary>
        /// Whether the path bar is in edit mode
        /// </summary>
        private bool _isEditingPath = false;

        /// <summary>
        /// Current text in the path bar input
        /// </summary>
        private string _editPath = "";

        /// <summary>
        /// Autocomplete matches for the current input
        /// </summary>
        private List<string> _autocompleteMatches = new List<string>();

        /// <summary>
        /// Current index in the autocomplete matches list
        /// </summary>
        private int _autocompleteIndex = 0;

        /// <summary>
        /// Parent directory used for current autocomplete session
        /// </summary>
        private string _autocompleteParentDir = "";

        /// <summary>
        /// JavaScript module used for scroll persistence
        /// </summary>
        private IJSObjectReference _jsModule;

        /// <summary>
        /// JavaScript callback reference
        /// </summary>
        private DotNetObjectReference<FilePanel> _dotNetReference;

        /// <summary>
        /// Last anchor restored into this component instance
        /// </summary>
        private string _restoredScrollAnchorPath = "";

        #endregion

        #region Overrides

        /// <summary>
        /// Registers semantic scroll tracking and restores the saved anchor
        /// </summary>
        /// <param name="firstRender">True on the first render</param>
        protected override async System.Threading.Tasks.Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender)
            {
                this._jsModule = await this._jsRuntime.InvokeAsync<IJSObjectReference>("import", "./js/interop.js");
                this._dotNetReference = DotNetObjectReference.Create(this);
                await this._jsModule.InvokeVoidAsync("registerFilePanelScroll", this.PanelId, this._dotNetReference);
            }

            if (this._jsModule != null && !string.IsNullOrEmpty(this.State.ScrollAnchorPath) && this.State.ScrollAnchorPath != this._restoredScrollAnchorPath)
            {
                this._restoredScrollAnchorPath = this.State.ScrollAnchorPath;
                int anchorIndex = this.State.Entries.FindIndex(entry => entry.FullPath == this.State.ScrollAnchorPath);
                await this._jsModule.InvokeVoidAsync("restoreFilePanelScrollAnchor", this.PanelId, this.State.ScrollAnchorPath, anchorIndex);
            }
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Receives the first visible semantic row from JavaScript
        /// </summary>
        /// <param name="path">Full path of the first visible entry</param>
        /// <returns>Asynchronous callback task</returns>
        [JSInvokable]
        public async System.Threading.Tasks.Task OnFileListScrollAnchorChanged(string path)
        {
            if (!string.IsNullOrEmpty(path) && path != this.State.ScrollAnchorPath)
            {
                this.State.ScrollAnchorPath = path;
                this._restoredScrollAnchorPath = path;
                await this.OnScrollAnchorChanged.InvokeAsync(path);
            }
        }

        /// <summary>
        /// Receives the measured file list page size from JavaScript
        /// </summary>
        /// <param name="pageSize">Number of file rows visible in the panel</param>
        /// <returns>Asynchronous callback task</returns>
        [JSInvokable]
        public async System.Threading.Tasks.Task OnFileListPageSizeChanged(int pageSize)
        {
            if (pageSize > 0)
            {
                await this.OnPageSizeChanged.InvokeAsync(pageSize);
            }
        }

        #endregion

        #region Private Methods - Path Bar

        /// <summary>
        /// Activates the path bar edit mode
        /// </summary>
        private void StartPathEdit()
        {
            this._isEditingPath = true;
            this._editPath = this.State.CurrentPath;
            this._autocompleteMatches.Clear();
            this._autocompleteIndex = 0;

            // Focus the input after render
            _ = this.FocusPathInputAsync();
        }

        /// <summary>
        /// Focuses the path input element after render
        /// </summary>
        private async System.Threading.Tasks.Task FocusPathInputAsync()
        {
            // Small delay to let Blazor render the input
            await System.Threading.Tasks.Task.Delay(50);
            string inputId = this.PanelId + "-path-input";
            await this._jsRuntime.InvokeVoidAsync("eval", "document.getElementById('" + inputId + "')?.focus()");
        }

        /// <summary>
        /// Cancels path bar editing and restores display mode
        /// </summary>
        private void CancelPathEdit()
        {
            this._isEditingPath = false;
            this._autocompleteMatches.Clear();
            this._autocompleteIndex = 0;
        }

        /// <summary>
        /// Handles text input changes in the path bar
        /// </summary>
        /// <param name="args">Change event args</param>
        private void HandlePathInput(ChangeEventArgs args)
        {
            this._editPath = args.Value.ToString();

            // Clear autocomplete when user types manually
            this._autocompleteMatches.Clear();
            this._autocompleteIndex = 0;
        }

        /// <summary>
        /// Handles blur on path input - cancel edit only if not clicking autocomplete
        /// </summary>
        private void HandlePathBlur()
        {
            // Small delay to allow autocomplete click to fire first
            _ = this.DelayedBlurAsync();
        }

        /// <summary>
        /// Delayed blur to allow autocomplete mousedown to fire
        /// </summary>
        private async System.Threading.Tasks.Task DelayedBlurAsync()
        {
            await System.Threading.Tasks.Task.Delay(150);
            if (this._isEditingPath)
            {
                this.CancelPathEdit();
                this.StateHasChanged();
            }
        }

        /// <summary>
        /// Handles keyboard input in the path bar
        /// </summary>
        /// <param name="args">Keyboard event args</param>
        private async System.Threading.Tasks.Task HandlePathKeyDown(KeyboardEventArgs args)
        {
            if (args.Key == "Enter")
            {
                // Navigate to the typed path
                await this.CommitPathEdit();
            }
            else if (args.Key == "Escape")
            {
                // Cancel editing
                this.CancelPathEdit();
            }
            else if (args.Key == "Tab")
            {
                // Tab preventDefault is handled in interop.js
                this.HandlePathAutocomplete();
            }
        }

        /// <summary>
        /// Commits the path bar edit and navigates to the typed path (keeps edit mode)
        /// </summary>
        private async System.Threading.Tasks.Task CommitPathEdit()
        {
            string path = this._editPath.Trim();
            this._autocompleteMatches.Clear();

            // Navigate but keep the input focused
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
            {
                await this.OnNavigate.InvokeAsync(path);
            }
        }

        /// <summary>
        /// Handles Tab key for path autocomplete
        /// </summary>
        private void HandlePathAutocomplete()
        {
            // If cycling through existing matches, advance to next
            if (this._autocompleteMatches.Count > 1)
            {
                this._autocompleteIndex = (this._autocompleteIndex + 1) % this._autocompleteMatches.Count;
                string selected = this._autocompleteMatches[this._autocompleteIndex];
                this._editPath = this._autocompleteParentDir + selected + Path.DirectorySeparatorChar;
                return;
            }

            // Parse input: split into parent directory and partial name
            string input = this._editPath.TrimEnd('/', '\\');
            string parentDir = "";
            string prefix = "";
            int lastSep = input.LastIndexOfAny(new char[] { '/', '\\' });

            if (lastSep >= 0)
            {
                parentDir = input.Substring(0, lastSep + 1);
                prefix = input.Substring(lastSep + 1);
            }
            else
            {
                // No separator: search in current directory
                parentDir = this.State.CurrentPath;
                if (!parentDir.EndsWith(Path.DirectorySeparatorChar))
                {
                    parentDir += Path.DirectorySeparatorChar;
                }
                prefix = input;
            }

            if (!Directory.Exists(parentDir))
            {
                return;
            }

            // Query matching subdirectories
            List<string> matches = this._fileSystemService.GetMatchingDirectories(parentDir, prefix);

            if (matches.Count == 0)
            {
                // No matches
                return;
            }

            // Save parent for cycling
            this._autocompleteParentDir = parentDir;

            if (matches.Count == 1)
            {
                // Single match: complete and clear dropdown
                this._editPath = parentDir + matches[0] + Path.DirectorySeparatorChar;
                this._autocompleteMatches.Clear();
                this._autocompleteIndex = 0;
            }
            else
            {
                // Multiple matches: complete common prefix, show dropdown
                string commonPrefix = this.GetCommonPrefix(matches);
                this._autocompleteMatches = matches;
                this._autocompleteIndex = 0;
                this._editPath = parentDir + commonPrefix;
            }
        }

        /// <summary>
        /// Finds the longest common prefix among a list of strings
        /// </summary>
        /// <param name="strings">List of strings</param>
        /// <returns>Common prefix</returns>
        private string GetCommonPrefix(List<string> strings)
        {
            string result = strings[0];

            for (int i = 1; i < strings.Count; i++)
            {
                int len = Math.Min(result.Length, strings[i].Length);
                int j = 0;

                while (j < len && char.ToLowerInvariant(result[j]) == char.ToLowerInvariant(strings[i][j]))
                {
                    j++;
                }

                result = result.Substring(0, j);
            }

            return result;
        }

        /// <summary>
        /// Selects an autocomplete item from the dropdown
        /// </summary>
        /// <param name="index">Index of the selected item</param>
        private void SelectAutocompleteItem(int index)
        {
            if (index >= 0 && index < this._autocompleteMatches.Count)
            {
                string selected = this._autocompleteMatches[index];
                this._editPath = this._autocompleteParentDir + selected + Path.DirectorySeparatorChar;
                this._autocompleteMatches.Clear();
                this._autocompleteIndex = 0;

                // Navigate directly
                _ = this.CommitPathEdit();
            }
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Handles click on the panel to activate it
        /// </summary>
        private async System.Threading.Tasks.Task OnPanelClick()
        {
            if (!this.IsActive)
            {
                await this.OnActivated.InvokeAsync();
            }
        }

        /// <summary>
        /// Handles navigation from the tree view
        /// </summary>
        /// <param name="path">Selected directory path</param>
        private async System.Threading.Tasks.Task HandleTreeNavigation(string path)
        {
            await this.OnNavigate.InvokeAsync(path);
        }

        /// <summary>
        /// Persists a semantic directory-tree expansion change
        /// </summary>
        /// <param name="change">Expansion change</param>
        private async System.Threading.Tasks.Task HandleTreeExpansionChanged(Bivium.Components.Tree.DirectoryTreeExpansionChange change)
        {
            await this.OnTreeExpansionChanged.InvokeAsync();
        }

        /// <summary>
        /// Handles navigation from the file list (double-click directory)
        /// </summary>
        /// <param name="path">Selected directory path</param>
        private async System.Threading.Tasks.Task HandleFileListNavigation(string path)
        {
            await this.OnNavigate.InvokeAsync(path);
        }

        /// <summary>
        /// Handles sort column change from file list header
        /// </summary>
        /// <param name="sort">New sort configuration</param>
        private async System.Threading.Tasks.Task HandleSortChanged(SortColumn sort)
        {
            await this.OnSortChanged.InvokeAsync(sort);
        }

        /// <summary>
        /// Handles selection change from file list
        /// </summary>
        /// <param name="selectedPaths">Updated list of selected paths</param>
        private async System.Threading.Tasks.Task HandleSelectionChanged(List<string> selectedPaths)
        {
            await this.OnSelectionChanged.InvokeAsync(selectedPaths);
        }

        /// <summary>
        /// Handles cursor index change from keyboard navigation
        /// </summary>
        /// <param name="index">New cursor index</param>
        private async System.Threading.Tasks.Task HandleCursorChanged(int index)
        {
            await this.OnCursorChanged.InvokeAsync(index);
        }

        /// <summary>
        /// Handles context menu request from file list
        /// </summary>
        /// <param name="args">Context menu event data</param>
        private async System.Threading.Tasks.Task HandleContextMenu(ContextMenuEventArgs args)
        {
            await this.OnContextMenu.InvokeAsync(args);
        }

        /// <summary>
        /// Handles right-click on the panel background (empty area)
        /// </summary>
        /// <param name="args">Mouse event args with coordinates</param>
        private async System.Threading.Tasks.Task HandlePanelContextMenu(MouseEventArgs args)
        {
            // Fire context menu without a specific entry selected
            ContextMenuEventArgs contextArgs = new ContextMenuEventArgs();
            contextArgs.X = args.ClientX;
            contextArgs.Y = args.ClientY;
            contextArgs.Entry = null;
            await this.OnContextMenu.InvokeAsync(contextArgs);
        }

        /// <summary>
        /// Releases JavaScript scroll tracking resources
        /// </summary>
        /// <returns>Asynchronous disposal operation</returns>
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (this._jsModule != null)
                {
                    await this._jsModule.InvokeVoidAsync("unregisterFilePanelScroll", this.PanelId);
                    await this._jsModule.DisposeAsync();
                }
            }
            catch (JSDisconnectedException)
            {
            }

            this._dotNetReference?.Dispose();
        }

        #endregion
    }

    /// <summary>
    /// Event arguments for context menu requests
    /// </summary>
    public class ContextMenuEventArgs
    {
        /// <summary>
        /// Mouse X coordinate for positioning
        /// </summary>
        public double X { get; set; } = 0;

        /// <summary>
        /// Mouse Y coordinate for positioning
        /// </summary>
        public double Y { get; set; } = 0;

        /// <summary>
        /// The file system entry that was right-clicked
        /// </summary>
        public FileSystemEntry Entry { get; set; }
    }
}
