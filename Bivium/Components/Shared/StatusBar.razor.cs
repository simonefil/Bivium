using Microsoft.AspNetCore.Components;
using Bivium.Models;
using Bivium.Services;
using System.Collections.Generic;

namespace Bivium.Components.Shared
{
    /// <summary>
    /// Status bar showing selection info, progress text, and disk space
    /// </summary>
    public partial class StatusBar : ComponentBase
    {
        #region Parameters

        /// <summary>Open windows projected by the Commander</summary>
        [Parameter]
        public IReadOnlyList<DesktopWindowState> Windows { get; set; } = new List<DesktopWindowState>();

        /// <summary>Request to restore and activate the window</summary>
        [Parameter]
        public EventCallback<string> OnWindowActivate { get; set; }

        /// <summary>Current availability of the activation</summary>
        [Parameter]
        public bool CanActivateWindows { get; set; }

        /// <summary>
        /// List of selected file paths
        /// </summary>
        [Parameter]
        public List<string> SelectedPaths { get; set; } = new List<string>();

        /// <summary>
        /// Current directory path of the active panel
        /// </summary>
        [Parameter]
        public string CurrentPath { get; set; } = "";

        /// <summary>
        /// All entries in the active panel
        /// </summary>
        [Parameter]
        public List<FileSystemEntry> Entries { get; set; } = new List<FileSystemEntry>();

        /// <summary>
        /// Progress text to display in the center area
        /// </summary>
        [Parameter]
        public string ProgressText { get; set; } = "";

        /// <summary>
        /// Whether a cancellable operation is running
        /// </summary>
        [Parameter]
        public bool CanCancelOperation { get; set; } = false;

        /// <summary>
        /// Callback invoked when the user cancels the running operation
        /// </summary>
        [Parameter]
        public EventCallback OnCancelOperation { get; set; }

        #endregion

        #region Class Variables

        /// <summary>
        /// Formatted selection info string
        /// </summary>
        private string _selectionInfo = "Ready";

        /// <summary>
        /// Formatted disk space info string
        /// </summary>
        private string _diskInfo = "";

        /// <summary>
        /// Progress text for display
        /// </summary>
        private string _progressText = "";

        /// <summary>
        /// Last path used to calculate disk info
        /// </summary>
        private string _lastDiskInfoPath = "";

        /// <summary>
        /// Last list used for the free space: a new list follows refreshes and operations
        /// </summary>
        private List<FileSystemEntry> _lastDiskInfoEntries;

        #endregion

        #region Overrides

        /// <summary>
        /// Update display when parameters change
        /// </summary>
        protected override void OnParametersSet()
        {
            this._progressText = this.ProgressText;
            this.UpdateSelectionInfo();
            this.UpdateDiskInfo();
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Updates the selection info text
        /// </summary>
        private void UpdateSelectionInfo()
        {
            if (this.SelectedPaths.Count == 0)
            {
                this._selectionInfo = this.Entries.Count + " items";
            }
            else
            {
                HashSet<string> selectedPathSet = new HashSet<string>(this.SelectedPaths);

                // Calculate total size of selected files
                long totalSize = 0;
                for (int i = 0; i < this.Entries.Count; i++)
                {
                    if (selectedPathSet.Contains(this.Entries[i].FullPath))
                    {
                        totalSize += this.Entries[i].SizeBytes;
                    }
                }

                this._selectionInfo = this.SelectedPaths.Count + " selected, " + ByteSizeFormatter.Format(totalSize);
            }
        }

        /// <summary>
        /// Updates the disk space info text
        /// </summary>
        private void UpdateDiskInfo()
        {
            if (!string.IsNullOrEmpty(this.CurrentPath) && (this.CurrentPath != this._lastDiskInfoPath || !ReferenceEquals(this.Entries, this._lastDiskInfoEntries)))
            {
                long free = this.FileSystemService.GetAvailableDiskSpace(this.CurrentPath);
                long total = this.FileSystemService.GetTotalDiskSpace(this.CurrentPath);
                this._diskInfo = "Free: " + ByteSizeFormatter.Format(free) + " / " + ByteSizeFormatter.Format(total);
                this._lastDiskInfoPath = this.CurrentPath;
                this._lastDiskInfoEntries = this.Entries;
            }
        }

        #endregion
    }
}
