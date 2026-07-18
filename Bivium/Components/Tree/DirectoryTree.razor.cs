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

        /// <summary>
        /// Semantic paths of expanded nodes
        /// </summary>
        [Parameter]
        public HashSet<string> ExpandedDirectoryPaths { get; set; } = new HashSet<string>();

        /// <summary>
        /// Callback when a node is expanded or collapsed
        /// </summary>
        [Parameter]
        public EventCallback<DirectoryTreeExpansionChange> OnExpansionChanged { get; set; }

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
        private async System.Threading.Tasks.Task HandleDirectorySelected(string path)
        {
            await this.OnDirectorySelected.InvokeAsync(path);
        }

        #endregion
    }

    /// <summary>
    /// Describes a semantic directory-tree expansion change
    /// </summary>
    public sealed class DirectoryTreeExpansionChange
    {
        /// <summary>
        /// Full directory path
        /// </summary>
        public string Path { get; set; } = "";

        /// <summary>
        /// True when the node is expanded
        /// </summary>
        public bool Expanded { get; set; }
    }
}
