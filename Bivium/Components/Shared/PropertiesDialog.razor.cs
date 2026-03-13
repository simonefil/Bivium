using Microsoft.AspNetCore.Components;
using Bivium.Models;
using Bivium.Services;

namespace Bivium.Components.Shared
{
    /// <summary>
    /// Read-only dialog showing file or directory properties
    /// </summary>
    public partial class PropertiesDialog : ComponentBase
    {
        #region Injected Services

        /// <summary>
        /// Permission service for reading file permissions
        /// </summary>
        [Inject]
        private IPermissionService _permissionService { get; set; }

        /// <summary>
        /// File system service for directory size calculation
        /// </summary>
        [Inject]
        private IFileSystemService _fileSystemService { get; set; }

        #endregion

        #region Parameters

        /// <summary>
        /// Callback when dialog is closed
        /// </summary>
        [Parameter]
        public EventCallback OnClose { get; set; }

        #endregion

        #region Class Variables

        /// <summary>
        /// Whether the dialog is visible
        /// </summary>
        private bool _isVisible = false;

        /// <summary>
        /// The file system entry being displayed
        /// </summary>
        private FileSystemEntry _entry = new FileSystemEntry();

        /// <summary>
        /// Permission model for the entry
        /// </summary>
        private PermissionModel _permissions;

        /// <summary>
        /// Calculated directory size in bytes (-1 = not calculated)
        /// </summary>
        private long _calculatedSize = -1;

        /// <summary>
        /// Number of files found during size calculation
        /// </summary>
        private int _calculatedFileCount = 0;

        /// <summary>
        /// Number of subdirectories found during size calculation
        /// </summary>
        private int _calculatedDirCount = 0;

        /// <summary>
        /// Whether a size calculation is in progress
        /// </summary>
        private bool _isCalculating = false;

        /// <summary>
        /// Reference to the OK button for focus
        /// </summary>
        private ElementReference _okButton;

        #endregion

        #region Public Methods

        /// <summary>
        /// Shows the dialog for the specified entry
        /// </summary>
        /// <param name="entry">File system entry to display</param>
        public void Show(FileSystemEntry entry)
        {
            this._entry = entry;

            // Reset calculated size
            this._calculatedSize = -1;
            this._calculatedFileCount = 0;
            this._calculatedDirCount = 0;
            this._isCalculating = false;

            // Load permissions
            try
            {
                this._permissions = this._permissionService.GetPermissions(entry.FullPath);
            }
            catch
            {
                this._permissions = null;
            }

            this._isVisible = true;
            this.StateHasChanged();

            // Focus the OK button after render
            _ = this.FocusButtonAsync();
        }

        /// <summary>
        /// Focuses the OK button after render
        /// </summary>
        private async System.Threading.Tasks.Task FocusButtonAsync()
        {
            await System.Threading.Tasks.Task.Delay(50);
            await this._okButton.FocusAsync();
        }

        /// <summary>
        /// Hides the dialog
        /// </summary>
        public void Hide()
        {
            this._isVisible = false;
            this.StateHasChanged();
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Handles close button click
        /// </summary>
        private void HandleClose()
        {
            this._isVisible = false;
            this.OnClose.InvokeAsync();
        }

        /// <summary>
        /// Calculates directory size on a background thread
        /// </summary>
        private void HandleCalculateSize()
        {
            this._isCalculating = true;
            this.StateHasChanged();

            // Run on background thread to avoid blocking UI
            Thread calcThread = new Thread(() =>
            {
                int fileCount = 0;
                int dirCount = 0;
                long size = this._fileSystemService.CalculateDirectorySize(this._entry.FullPath, out fileCount, out dirCount);

                this._calculatedSize = size;
                this._calculatedFileCount = fileCount;
                this._calculatedDirCount = dirCount;
                this._isCalculating = false;

                // Marshal back to Blazor render thread
                this.InvokeAsync(() => this.StateHasChanged());
            });
            calcThread.IsBackground = true;
            calcThread.Start();
        }

        /// <summary>
        /// Formats the calculated directory size for display
        /// </summary>
        /// <returns>Human-readable size string</returns>
        private string FormatCalculatedSize()
        {
            string result = "";

            if (this._calculatedSize < 1024)
            {
                result = this._calculatedSize.ToString() + " B";
            }
            else if (this._calculatedSize < 1024 * 1024)
            {
                result = (this._calculatedSize / 1024.0).ToString("F1") + " KB";
            }
            else if (this._calculatedSize < 1024L * 1024 * 1024)
            {
                result = (this._calculatedSize / (1024.0 * 1024.0)).ToString("F1") + " MB";
            }
            else
            {
                result = (this._calculatedSize / (1024.0 * 1024.0 * 1024.0)).ToString("F2") + " GB";
            }

            result += " (" + this._calculatedSize.ToString("N0") + " bytes)";

            return result;
        }

        #endregion
    }
}
