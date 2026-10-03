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

        /// <summary>Disponibilità e shortcut calcolati esclusivamente dal Commander</summary>
        [Parameter]
        public IReadOnlyList<CommanderCommandState> Commands { get; set; } = new List<CommanderCommandState>();

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
        /// Identificatori delle azioni mostrate nella legenda, nell'ordine storico
        /// </summary>
        private static readonly IReadOnlyList<string> s_shortcutIds = new List<string>
        {
            "copy",
            "cut",
            "paste",
            "delete",
            "rename",
            "new-folder",
            "refresh",
            "properties",
            "terminal",
            "about"
        };

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
        /// Finds a command projection by its identifier
        /// </summary>
        /// <param name="commandId">Identifier of the command to find</param>
        /// <returns>The matching command projection, or null when it is not received</returns>
        private CommanderCommandState GetCommand(string commandId)
        {
            for (int i = 0; i < this.Commands.Count; i++)
            {
                if (this.Commands[i].Id == commandId)
                    return this.Commands[i];
            }

            return null;
        }

        /// <summary>
        /// Returns the legacy visual shortcut text without changing the command mapping
        /// </summary>
        /// <param name="command">Command projection received from the Commander</param>
        /// <returns>Shortcut text for the legend keycap</returns>
        private string GetVisualShortcut(CommanderCommandState command)
        {
            if (command.Id == "delete")
                return "Del";

            return command.Shortcut;
        }

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

                this._selectionInfo = this.SelectedPaths.Count + " selected, " + this.FormatSize(totalSize);
            }
        }

        /// <summary>
        /// Updates the disk space info text
        /// </summary>
        private void UpdateDiskInfo()
        {
            if (!string.IsNullOrEmpty(this.CurrentPath) && this.CurrentPath != this._lastDiskInfoPath)
            {
                long free = this.FileSystemService.GetAvailableDiskSpace(this.CurrentPath);
                long total = this.FileSystemService.GetTotalDiskSpace(this.CurrentPath);
                this._diskInfo = "Free: " + this.FormatSize(free) + " / " + this.FormatSize(total);
                this._lastDiskInfoPath = this.CurrentPath;
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
