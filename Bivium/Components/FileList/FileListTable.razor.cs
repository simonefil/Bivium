using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.Virtualization;
using Bivium.Models;
using Bivium.Components.Panel;
using Bivium.Services;

namespace Bivium.Components.FileList
{
    /// <summary>
    /// File list table with sortable columns and row selection
    /// </summary>
    public partial class FileListTable : ComponentBase
    {
        #region Injected Services

        /// <summary>
        /// File system service for parent path resolution
        /// </summary>
        [Inject]
        private IFileSystemService _fileSystemService { get; set; }

        #endregion

        #region Parameters

        /// <summary>
        /// List of file system entries to display
        /// </summary>
        [Parameter]
        public List<FileSystemEntry> Entries { get; set; } = new List<FileSystemEntry>();

        /// <summary>
        /// Current sort configuration
        /// </summary>
        [Parameter]
        public SortColumn CurrentSort { get; set; } = new SortColumn();

        /// <summary>
        /// List of selected entry paths
        /// </summary>
        [Parameter]
        public List<string> SelectedPaths { get; set; } = new List<string>();

        /// <summary>
        /// Current cursor index for keyboard navigation
        /// </summary>
        [Parameter]
        public int CursorIndex { get; set; } = 0;

        /// <summary>
        /// Current directory path
        /// </summary>
        [Parameter]
        public string CurrentPath { get; set; } = "";

        /// <summary>
        /// Callback when sort column changes
        /// </summary>
        [Parameter]
        public EventCallback<SortColumn> OnSortChanged { get; set; }

        /// <summary>
        /// Callback when selection changes
        /// </summary>
        [Parameter]
        public EventCallback<List<string>> OnSelectionChanged { get; set; }

        /// <summary>
        /// Callback when navigating to a directory
        /// </summary>
        [Parameter]
        public EventCallback<string> OnNavigate { get; set; }

        /// <summary>
        /// Callback when cursor index changes
        /// </summary>
        [Parameter]
        public EventCallback<int> OnCursorChanged { get; set; }

        /// <summary>
        /// Callback when context menu is requested
        /// </summary>
        [Parameter]
        public EventCallback<ContextMenuEventArgs> OnContextMenu { get; set; }

        #endregion

        #region Class Variables

        /// <summary>
        /// Whether the current directory has a parent
        /// </summary>
        private bool _hasParent = false;

        /// <summary>
        /// Index of the last clicked row (for shift+click range selection)
        /// </summary>
        private int _lastClickedIndex = 0;

        /// <summary>
        /// Fast lookup for selected paths during row rendering
        /// </summary>
        private HashSet<string> _selectedPathSet = new HashSet<string>();

        #endregion

        #region Overrides

        /// <summary>
        /// Update parent directory availability when parameters change
        /// </summary>
        protected override void OnParametersSet()
        {
            string parentPath = this._fileSystemService.GetParentPath(this.CurrentPath);
            this._hasParent = !string.IsNullOrEmpty(parentPath);
            this._selectedPathSet = new HashSet<string>(this.SelectedPaths);
        }

        #endregion

        #region Private Methods - Virtualization

        /// <summary>
        /// Provides visible file rows to the virtualized table body
        /// </summary>
        /// <param name="request">Virtualization request</param>
        /// <returns>Requested rows and total row count</returns>
        private System.Threading.Tasks.ValueTask<ItemsProviderResult<FileListRowItem>> GetRows(ItemsProviderRequest request)
        {
            int startIndex = Math.Min(request.StartIndex, this.Entries.Count);
            int count = Math.Min(request.Count, this.Entries.Count - startIndex);
            List<FileListRowItem> rows = new List<FileListRowItem>();

            for (int i = 0; i < count; i++)
            {
                int entryIndex = startIndex + i;
                rows.Add(new FileListRowItem(entryIndex, this.Entries[entryIndex]));
            }

            ItemsProviderResult<FileListRowItem> result = new ItemsProviderResult<FileListRowItem>(rows, this.Entries.Count);
            return new System.Threading.Tasks.ValueTask<ItemsProviderResult<FileListRowItem>>(result);
        }

        #endregion

        #region Private Methods - Sort

        /// <summary>
        /// Toggles sort on a column (click same column toggles direction)
        /// </summary>
        /// <param name="field">Sort field clicked</param>
        private async System.Threading.Tasks.Task ToggleSort(SortField field)
        {
            SortColumn newSort = new SortColumn();

            if (this.CurrentSort.Field == field)
            {
                // Same column - toggle direction
                SortDirection newDirection = this.CurrentSort.Direction == SortDirection.Ascending
                    ? SortDirection.Descending
                    : SortDirection.Ascending;
                newSort.Field = field;
                newSort.Direction = newDirection;
            }
            else
            {
                // Different column - ascending
                newSort.Field = field;
                newSort.Direction = SortDirection.Ascending;
            }

            await this.OnSortChanged.InvokeAsync(newSort);
        }

        /// <summary>
        /// Returns the sort indicator character for a column header
        /// </summary>
        /// <param name="field">Sort field</param>
        /// <returns>Sort indicator markup string</returns>
        private string GetSortIndicator(SortField field)
        {
            string result = "";

            if (this.CurrentSort.Field == field)
            {
                result = this.CurrentSort.Direction == SortDirection.Ascending ? "^" : "v";
            }

            return result;
        }

        #endregion

        #region Private Methods - Selection

        /// <summary>
        /// Handles a row click with support for Ctrl and Shift modifiers
        /// </summary>
        /// <param name="index">Clicked row index</param>
        /// <param name="args">Mouse event args with modifier keys</param>
        private async System.Threading.Tasks.Task HandleRowClick(int index, MouseEventArgs args)
        {
            List<string> newSelection = new List<string>();

            if (index < 0 || index >= this.Entries.Count)
            {
                await this.OnSelectionChanged.InvokeAsync(newSelection);
                await this.OnCursorChanged.InvokeAsync(0);
                return;
            }

            string clickedPath = this.Entries[index].FullPath;

            if (args.CtrlKey)
            {
                // Ctrl+click: toggle individual item
                newSelection.AddRange(this.SelectedPaths);
                if (this._selectedPathSet.Contains(clickedPath))
                {
                    newSelection.Remove(clickedPath);
                }
                else
                {
                    newSelection.Add(clickedPath);
                }
            }
            else if (args.ShiftKey)
            {
                // Shift+click: range selection from last clicked to current
                HashSet<string> newSelectionSet = new HashSet<string>();
                int start = Math.Min(this._lastClickedIndex, index);
                int end = Math.Max(this._lastClickedIndex, index);

                for (int i = start; i <= end; i++)
                {
                    if (i >= 0 && i < this.Entries.Count)
                    {
                        string path = this.Entries[i].FullPath;
                        if (!newSelectionSet.Contains(path))
                        {
                            newSelection.Add(path);
                            newSelectionSet.Add(path);
                        }
                    }
                }
            }
            else
            {
                // Normal click: single selection
                newSelection.Add(clickedPath);
            }

            this._lastClickedIndex = index;
            await this.OnSelectionChanged.InvokeAsync(newSelection);
            await this.OnCursorChanged.InvokeAsync(index);
        }

        /// <summary>
        /// Handles double-click on a row (navigate into directory)
        /// </summary>
        /// <param name="index">Double-clicked row index</param>
        private async System.Threading.Tasks.Task HandleRowDoubleClick(int index)
        {
            if (index >= 0 && index < this.Entries.Count)
            {
                FileSystemEntry entry = this.Entries[index];
                if (entry.IsDirectory)
                {
                    await this.OnNavigate.InvokeAsync(entry.FullPath);
                }
            }
        }

        /// <summary>
        /// Navigates to the parent directory
        /// </summary>
        private async System.Threading.Tasks.Task NavigateToParent()
        {
            string parentPath = this._fileSystemService.GetParentPath(this.CurrentPath);
            if (!string.IsNullOrEmpty(parentPath))
            {
                await this.OnNavigate.InvokeAsync(parentPath);
            }
        }

        /// <summary>
        /// Handles right-click context menu on a row
        /// </summary>
        /// <param name="index">Right-clicked row index</param>
        /// <param name="args">Mouse event args with coordinates</param>
        private async System.Threading.Tasks.Task HandleRowContextMenu(int index, MouseEventArgs args)
        {
            // Select the row if not already selected
            if (index >= 0 && index < this.Entries.Count)
            {
                string clickedPath = this.Entries[index].FullPath;

                if (!this._selectedPathSet.Contains(clickedPath))
                {
                    List<string> newSelection = new List<string>();
                    newSelection.Add(clickedPath);
                    await this.OnSelectionChanged.InvokeAsync(newSelection);
                }

                await this.OnCursorChanged.InvokeAsync(index);

                // Fire context menu event
                ContextMenuEventArgs contextArgs = new ContextMenuEventArgs();
                contextArgs.X = args.ClientX;
                contextArgs.Y = args.ClientY;
                contextArgs.Entry = this.Entries[index];
                await this.OnContextMenu.InvokeAsync(contextArgs);
            }
        }

        #endregion

        #region Classes

        /// <summary>
        /// Row item carrying the original entry index
        /// </summary>
        private class FileListRowItem
        {
            #region Properties

            /// <summary>
            /// Entry index in the full list
            /// </summary>
            public int Index { get; set; } = 0;

            /// <summary>
            /// File system entry
            /// </summary>
            public FileSystemEntry Entry { get; set; } = new FileSystemEntry();

            #endregion

            #region Constructor

            /// <summary>
            /// Creates a virtualized row item
            /// </summary>
            /// <param name="index">Entry index</param>
            /// <param name="entry">File system entry</param>
            public FileListRowItem(int index, FileSystemEntry entry)
            {
                this.Index = index;
                this.Entry = entry;
            }

            #endregion
        }

        #endregion
    }
}
