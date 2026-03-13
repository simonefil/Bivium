using Microsoft.AspNetCore.Components;
using Bivium.Models;
using Bivium.Services;

namespace Bivium.Components.Shared
{
    /// <summary>
    /// Status bar showing selection info, progress text, and disk space
    /// </summary>
    public partial class StatusBar : ComponentBase
    {
        #region Parameters

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
                // Calculate total size of selected files
                long totalSize = 0;
                for (int i = 0; i < this.Entries.Count; i++)
                {
                    if (this.SelectedPaths.Contains(this.Entries[i].FullPath))
                    {
                        totalSize += this.Entries[i].SizeBytes;
                    }
                }

                this._selectionInfo = this.SelectedPaths.Count + " selected, " + this.FormatSize(totalSize);
            }
        }

        /// <summary>
        /// Updates the disk space info text
        /// </summary>
        private void UpdateDiskInfo()
        {
            if (!string.IsNullOrEmpty(this.CurrentPath))
            {
                long free = this.FileSystemService.GetAvailableDiskSpace(this.CurrentPath);
                long total = this.FileSystemService.GetTotalDiskSpace(this.CurrentPath);
                this._diskInfo = "Free: " + this.FormatSize(free) + " / " + this.FormatSize(total);
            }
        }

        /// <summary>
        /// Formats a byte count for display
        /// </summary>
        /// <param name="bytes">Size in bytes</param>
        /// <returns>Formatted size string</returns>
        private string FormatSize(long bytes)
        {
            string result = "";

            if (bytes < 1024)
            {
                result = bytes + " B";
            }
            else if (bytes < 1024 * 1024)
            {
                result = (bytes / 1024.0).ToString("F1") + " KB";
            }
            else if (bytes < 1024L * 1024 * 1024)
            {
                result = (bytes / (1024.0 * 1024.0)).ToString("F1") + " MB";
            }
            else
            {
                result = (bytes / (1024.0 * 1024.0 * 1024.0)).ToString("F1") + " GB";
            }

            return result;
        }

        #endregion
    }
}
