using Microsoft.AspNetCore.Components;
using Bivium.Models;
using Bivium.Services;

namespace Bivium.Components.Tree
{
    /// <summary>
    /// Single node in the directory tree - recursive component
    /// </summary>
    public partial class DirectoryTreeNode : ComponentBase
    {
        #region Parameters

        /// <summary>
        /// The directory entry for this node
        /// </summary>
        [Parameter]
        public FileSystemEntry Entry { get; set; }

        /// <summary>
        /// Currently active path (for highlighting)
        /// </summary>
        [Parameter]
        public string CurrentPath { get; set; } = "";

        /// <summary>
        /// Callback when a directory is selected
        /// </summary>
        [Parameter]
        public EventCallback<string> OnDirectorySelected { get; set; }

        /// <summary>
        /// Nesting depth for indentation
        /// </summary>
        [Parameter]
        public int Depth { get; set; } = 0;

        #endregion

        #region Class Variables

        /// <summary>
        /// Whether this node is expanded
        /// </summary>
        private bool _isExpanded = false;

        /// <summary>
        /// Child directory entries (loaded lazily)
        /// </summary>
        private List<FileSystemEntry> _children;

        /// <summary>
        /// Previous current path for change detection
        /// </summary>
        private string _previousCurrentPath = "";

        #endregion

        #region Overrides

        /// <summary>
        /// Auto-expand nodes that are ancestors of the current path
        /// </summary>
        protected override void OnParametersSet()
        {
            // Only process when CurrentPath actually changed
            if (string.Equals(this.CurrentPath, this._previousCurrentPath, this.GetPathComparison()))
            {
                return;
            }

            this._previousCurrentPath = this.CurrentPath;

            if (!string.IsNullOrEmpty(this.CurrentPath) && this.Entry != null)
            {
                // Expand this node only for exact matches or real path descendants
                bool isAncestor = this.IsAncestorPath(this.Entry.FullPath, this.CurrentPath);
                bool isExactMatch = this.AreSamePath(this.CurrentPath, this.Entry.FullPath);

                if ((isAncestor || isExactMatch) && !this._isExpanded)
                {
                    this._isExpanded = true;
                    this.LoadChildren();
                }
                else if (isAncestor && this._isExpanded)
                {
                    // Refresh children to pick up new subdirectories
                    this.LoadChildren();
                }
            }
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Handles click on the expand/collapse toggle
        /// </summary>
        private void OnToggleClick()
        {
            this._isExpanded = !this._isExpanded;

            // Lazy load children on first expand
            if (this._isExpanded && this._children == null)
            {
                this.LoadChildren();
            }
        }

        /// <summary>
        /// Handles click on the node row - navigates the file list
        /// </summary>
        private async System.Threading.Tasks.Task OnClick()
        {
            await this.OnDirectorySelected.InvokeAsync(this.Entry.FullPath);
        }

        /// <summary>
        /// Loads child directories from the filesystem
        /// </summary>
        private void LoadChildren()
        {
            this._children = this.FileSystemService.GetSubDirectories(this.Entry.FullPath);
        }

        /// <summary>
        /// Checks if this node's path matches the current active path
        /// </summary>
        /// <returns>True if this is the current directory</returns>
        private bool IsCurrentPath()
        {
            bool result = this.Entry != null && !string.IsNullOrEmpty(this.CurrentPath) && this.AreSamePath(this.Entry.FullPath, this.CurrentPath);
            return result;
        }

        /// <summary>
        /// Checks whether a path is a descendant of an ancestor path
        /// </summary>
        /// <param name="ancestorPath">Candidate ancestor path</param>
        /// <param name="currentPath">Current path</param>
        /// <returns>True if current path is below ancestor path</returns>
        private bool IsAncestorPath(string ancestorPath, string currentPath)
        {
            bool result = false;

            string ancestorFull = this.NormalizePath(ancestorPath);
            string currentFull = this.NormalizePath(currentPath);

            if (!string.Equals(ancestorFull, currentFull, this.GetPathComparison()))
            {
                if (!ancestorFull.EndsWith(Path.DirectorySeparatorChar))
                {
                    ancestorFull += Path.DirectorySeparatorChar;
                }

                result = currentFull.StartsWith(ancestorFull, this.GetPathComparison());
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
            bool result = string.Equals(this.NormalizePath(left), this.NormalizePath(right), this.GetPathComparison());
            return result;
        }

        /// <summary>
        /// Normalizes a path for comparison
        /// </summary>
        /// <param name="path">Path to normalize</param>
        /// <returns>Normalized full path without trailing separators</returns>
        private string NormalizePath(string path)
        {
            string result = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
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

        #endregion
    }
}
