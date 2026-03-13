using Microsoft.AspNetCore.Components;
using Bivium.Models;
using Bivium.Services;

namespace Bivium.Components.Tree
{
    /// <summary>
    /// Directory tree view - renders root nodes and manages navigation
    /// </summary>
    public partial class DirectoryTree : ComponentBase
    {
        #region Parameters

        /// <summary>
        /// Root path to display in the tree
        /// </summary>
        [Parameter]
        public string RootPath { get; set; } = "";

        /// <summary>
        /// Currently active path (highlighted in tree)
        /// </summary>
        [Parameter]
        public string CurrentPath { get; set; } = "";

        /// <summary>
        /// Callback when a directory is selected in the tree
        /// </summary>
        [Parameter]
        public EventCallback<string> OnDirectorySelected { get; set; }

        #endregion

        #region Class Variables

        /// <summary>
        /// Root entries for the tree
        /// </summary>
        private List<FileSystemEntry> _roots;

        /// <summary>
        /// Previous root path for change detection
        /// </summary>
        private string _previousRootPath = "";

        #endregion

        #region Overrides

        /// <summary>
        /// Load root entries on initialization
        /// </summary>
        protected override void OnInitialized()
        {
            this.LoadRoots();
        }

        /// <summary>
        /// Reload roots if the root path changes
        /// </summary>
        protected override void OnParametersSet()
        {
            // Only reload if the drive/root changed, not on every path change
            string currentRoot = Path.GetPathRoot(this.RootPath);
            string previousRoot = Path.GetPathRoot(this._previousRootPath);

            if (!string.Equals(currentRoot, previousRoot, StringComparison.OrdinalIgnoreCase))
            {
                this.LoadRoots();
            }

            this._previousRootPath = this.RootPath;
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Loads the root level entries for the tree
        /// </summary>
        private void LoadRoots()
        {
            // Show drive roots as top-level entries
            this._roots = this.FileSystemService.GetDriveRoots();
        }

        /// <summary>
        /// Handles directory selection from a tree node
        /// </summary>
        /// <param name="path">Selected directory path</param>
        private void HandleDirectorySelected(string path)
        {
            this.OnDirectorySelected.InvokeAsync(path);
        }

        #endregion
    }
}
