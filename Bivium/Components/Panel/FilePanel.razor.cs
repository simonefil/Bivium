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
    public partial class FilePanel : ComponentBase
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
        private void HandlePathKeyDown(KeyboardEventArgs args)
        {
            if (args.Key == "Enter")
            {
                // Navigate to the typed path
                this.CommitPathEdit();
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
        private void CommitPathEdit()
        {
            string path = this._editPath.Trim();
            this._autocompleteMatches.Clear();

            // Navigate but keep the input focused
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
            {
                this.OnNavigate.InvokeAsync(path);
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
                this.CommitPathEdit();
            }
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Handles click on the panel to activate it
        /// </summary>
        private void OnPanelClick()
        {
            if (!this.IsActive)
            {
                this.OnActivated.InvokeAsync();
            }
        }

        /// <summary>
        /// Handles navigation from the tree view
        /// </summary>
        /// <param name="path">Selected directory path</param>
        private void HandleTreeNavigation(string path)
        {
            this.OnNavigate.InvokeAsync(path);
        }

        /// <summary>
        /// Handles navigation from the file list (double-click directory)
        /// </summary>
        /// <param name="path">Selected directory path</param>
        private void HandleFileListNavigation(string path)
        {
            this.OnNavigate.InvokeAsync(path);
        }

        /// <summary>
        /// Handles sort column change from file list header
        /// </summary>
        /// <param name="sort">New sort configuration</param>
        private void HandleSortChanged(SortColumn sort)
        {
            this.OnSortChanged.InvokeAsync(sort);
        }

        /// <summary>
        /// Handles selection change from file list
        /// </summary>
        /// <param name="selectedPaths">Updated list of selected paths</param>
        private void HandleSelectionChanged(List<string> selectedPaths)
        {
            this.OnSelectionChanged.InvokeAsync(selectedPaths);
        }

        /// <summary>
        /// Handles cursor index change from keyboard navigation
        /// </summary>
        /// <param name="index">New cursor index</param>
        private void HandleCursorChanged(int index)
        {
            this.OnCursorChanged.InvokeAsync(index);
        }

        /// <summary>
        /// Handles context menu request from file list
        /// </summary>
        /// <param name="args">Context menu event data</param>
        private void HandleContextMenu(ContextMenuEventArgs args)
        {
            this.OnContextMenu.InvokeAsync(args);
        }

        /// <summary>
        /// Handles right-click on the panel background (empty area)
        /// </summary>
        /// <param name="args">Mouse event args with coordinates</param>
        private void HandlePanelContextMenu(MouseEventArgs args)
        {
            // Fire context menu without a specific entry selected
            ContextMenuEventArgs contextArgs = new ContextMenuEventArgs();
            contextArgs.X = args.ClientX;
            contextArgs.Y = args.ClientY;
            contextArgs.Entry = null;
            this.OnContextMenu.InvokeAsync(contextArgs);
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
