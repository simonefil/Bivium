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
            if (string.Equals(this.CurrentPath, this._previousCurrentPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            this._previousCurrentPath = this.CurrentPath;

            if (!string.IsNullOrEmpty(this.CurrentPath))
            {
                // Expand this node if the current path starts with this node's path
                bool isAncestor = this.CurrentPath.StartsWith(this.Entry.FullPath, StringComparison.OrdinalIgnoreCase)
                    && this.CurrentPath.Length > this.Entry.FullPath.Length;
                bool isExactMatch = string.Equals(this.CurrentPath, this.Entry.FullPath, StringComparison.OrdinalIgnoreCase);

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
        private void OnClick()
        {
            this.OnDirectorySelected.InvokeAsync(this.Entry.FullPath);
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
            bool result = string.Equals(this.Entry.FullPath, this.CurrentPath, StringComparison.OrdinalIgnoreCase);
            return result;
        }

        #endregion
    }
}
