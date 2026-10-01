using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Bivium.Components.Shared
{
    /// <summary>
    /// Dialog for hierarchical chunked file and directory uploads
    /// </summary>
    public partial class UploadDialog : ComponentBase, IAsyncDisposable
    {
        #region Parameters

        /// <summary>
        /// Callback when dialog is closed (true if upload succeeded)
        /// </summary>
        [Parameter]
        public EventCallback<bool> OnClose { get; set; }

        /// <summary>
        /// Attachment authorized for the mutating upload
        /// </summary>
        [Parameter]
        public string AttachmentId { get; set; } = "";

        /// <summary>
        /// Generation authorized for the mutating upload
        /// </summary>
        [Parameter]
        public long LeaseGeneration { get; set; }

        #endregion

        #region Class Variables

        /// <summary>
        /// Whether the dialog is visible
        /// </summary>
        private bool _isVisible = false;

        /// <summary>
        /// Target directory for the upload
        /// </summary>
        private string _destinationDir = "";

        /// <summary>
        /// Number of selected files
        /// </summary>
        private int _fileCount = 0;

        /// <summary>
        /// Number of selected directories
        /// </summary>
        private int _directoryCount = 0;

        /// <summary>
        /// Total bytes across selected files
        /// </summary>
        private long _totalBytes = 0;

        /// <summary>
        /// Upload progress percentage (0-100)
        /// </summary>
        private int _progress = 0;

        /// <summary>
        /// Whether an upload is in progress
        /// </summary>
        private bool _isUploading = false;

        /// <summary>
        /// Status or error text
        /// </summary>
        private string _statusText = "";

        /// <summary>
        /// Current relative path being uploaded or prepared
        /// </summary>
        private string _currentItem = "";

        /// <summary>
        /// Number of completely uploaded files
        /// </summary>
        private int _processedFiles = 0;

        /// <summary>
        /// Total files in the active upload
        /// </summary>
        private int _uploadFileCount = 0;

        /// <summary>
        /// JS module reference for upload interop
        /// </summary>
        private IJSObjectReference _jsModule;

        /// <summary>
        /// .NET object reference for JS callbacks
        /// </summary>
        private DotNetObjectReference<UploadDialog> _dotNetRef;

        /// <summary>
        /// Whether component-owned callbacks have been released
        /// </summary>
        private bool _isDisposed;

        /// <summary>
        /// Reference to the Browse button for focus
        /// </summary>
        private Radzen.Blazor.RadzenButton _browseButton;

        /// <summary>Bridge pronto per la selezione nel mount corrente</summary>
        private bool _bridgeReady;

        /// <summary>Generazione locale dell'apertura per scartare inizializzazioni obsolete</summary>
        private long _showGeneration;

        #endregion

        #region Public Methods

        /// <summary>
        /// Shows the upload dialog for the specified destination directory
        /// </summary>
        /// <param name="destinationDir">Target directory path</param>
        public void Show(string destinationDir)
        {
            this._destinationDir = destinationDir;
            this._fileCount = 0;
            this._directoryCount = 0;
            this._totalBytes = 0;
            this._progress = 0;
            this._isUploading = false;
            this._statusText = "";
            this._currentItem = "";
            this._processedFiles = 0;
            this._uploadFileCount = 0;
            this._isVisible = true;
            this._bridgeReady = false;
            this._showGeneration++;
            this.StateHasChanged();
        }

        /// <summary>
        /// Hides the dialog
        /// </summary>
        public void Hide()
        {
            this._isVisible = false;
            this._bridgeReady = false;
            this._showGeneration++;
            this.StateHasChanged();
        }

        #endregion

        #region JS Invokable Methods

        /// <summary>
        /// Called from JS when files or directories are added to the upload queue
        /// </summary>
        /// <param name="fileCount">Selected file count</param>
        /// <param name="directoryCount">Selected directory count</param>
        /// <param name="totalBytes">Total selected file bytes</param>
        [JSInvokable]
        public void OnUploadSelectionChanged(int fileCount, int directoryCount, long totalBytes)
        {
            if (this._isDisposed || !this._isVisible)
                return;

            this._fileCount = fileCount;
            this._directoryCount = directoryCount;
            this._totalBytes = totalBytes;
            this._statusText = "";
            this.InvokeAsync(() => this.StateHasChanged());
        }

        /// <summary>
        /// Called from JS to update upload progress
        /// </summary>
        /// <param name="percent">Progress percentage (0-100)</param>
        /// <param name="currentPath">Current relative path</param>
        /// <param name="processedFiles">Number of completed files</param>
        /// <param name="totalFiles">Total number of files</param>
        [JSInvokable]
        public void OnUploadProgress(int percent, string currentPath, int processedFiles, int totalFiles)
        {
            if (this._isDisposed || !this._isVisible || !this._isUploading)
                return;

            this._progress = percent;
            this._currentItem = currentPath;
            this._processedFiles = processedFiles;
            this._uploadFileCount = totalFiles;
            this.InvokeAsync(() => this.StateHasChanged());
        }

        /// <summary>
        /// Called from JS when upload completes or fails
        /// </summary>
        /// <param name="success">True if upload succeeded</param>
        /// <param name="message">Error message on failure</param>
        [JSInvokable]
        public async System.Threading.Tasks.Task OnUploadComplete(bool success, string message)
        {
            if (this._isDisposed || !this._isVisible || !this._isUploading)
                return;

            this._isUploading = false;

            if (success)
            {
                this._statusText = "Upload complete";
                this._isVisible = false;
                await this.OnClose.InvokeAsync(true);
            }
            else
            {
                this._statusText = "Error: " + message;
            }

            await this.InvokeAsync(() => this.StateHasChanged());
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Initializes JS module and focuses the Browse button
        /// </summary>
        /// <param name="firstRender">Primo mount reale del contenuto nativo</param>
        private async System.Threading.Tasks.Task HandleContentRenderedAsync(bool firstRender)
        {
            if (!firstRender || this._isDisposed || !this._isVisible)
                return;

            long generation = this._showGeneration;
            await this.InitializeJsModule();
            if (this._isDisposed || !this._isVisible || generation != this._showGeneration || this._jsModule == null)
                return;
            await this._jsModule.InvokeVoidAsync("initUpload", this._dotNetRef);
            if (this._isDisposed || !this._isVisible || generation != this._showGeneration)
                return;
            await this._jsModule.InvokeVoidAsync("clearUploadSelection");
            if (this._isDisposed || !this._isVisible || generation != this._showGeneration)
                return;
            this._bridgeReady = true;
            await this._browseButton.Element.FocusAsync();
        }

        /// <summary>
        /// Initializes the JS upload module and passes the .NET reference
        /// </summary>
        private async System.Threading.Tasks.Task InitializeJsModule()
        {
            if (this._isDisposed || !this._isVisible)
                return;

            if (this._jsModule == null)
            {
                IJSObjectReference module = await this.JSRuntime.InvokeAsync<IJSObjectReference>("import", "./js/upload.js");
                if (this._isDisposed || !this._isVisible)
                {
                    await module.DisposeAsync();
                    return;
                }
                this._jsModule = module;
            }
            if (this._isDisposed || !this._isVisible)
                return;

            // Create .NET reference for callbacks
            if (this._dotNetRef == null)
            {
                this._dotNetRef = DotNetObjectReference.Create(this);
            }

        }

        /// <summary>
        /// Clears the current upload queue
        /// </summary>
        private async System.Threading.Tasks.Task HandleClearSelection()
        {
            if (this._jsModule == null || this._isUploading)
                return;

            await this._jsModule.InvokeVoidAsync("clearUploadSelection");
        }

        /// <summary>
        /// Handles the Upload button click - starts chunked upload via JS
        /// </summary>
        private async System.Threading.Tasks.Task HandleUpload()
        {
            if (this._jsModule == null || !this._bridgeReady || this._isUploading || !this.HasSelection())
                return;

            this._isUploading = true;
            this._progress = 0;
            this._statusText = "Uploading...";
            this._currentItem = "";
            this._processedFiles = 0;
            this._uploadFileCount = this._fileCount;
            this.StateHasChanged();

            await this._jsModule.InvokeVoidAsync("uploadSelection", this._destinationDir, this.AttachmentId, this.LeaseGeneration);
        }

        /// <summary>
        /// Handles the Cancel button click
        /// </summary>
        private async System.Threading.Tasks.Task HandleCancel()
        {
            if (!this._isVisible)
                return;

            bool wasUploading = this._isUploading;
            this._isUploading = false;
            this._isVisible = false;
            this._bridgeReady = false;
            this._showGeneration++;
            this._progress = 0;
            this._statusText = "";
            this._currentItem = "";
            this._processedFiles = 0;
            this._uploadFileCount = 0;
            this._fileCount = 0;
            this._directoryCount = 0;
            this._totalBytes = 0;
            if (wasUploading && this._jsModule != null)
                await this._jsModule.InvokeVoidAsync("cancelUpload");
            else if (this._jsModule != null)
                await this._jsModule.InvokeVoidAsync("clearUploadSelection");
            await this.OnClose.InvokeAsync(false);
        }

        /// <summary>
        /// Returns whether the upload queue contains files or directories
        /// </summary>
        /// <returns>True when at least one entry is selected</returns>
        private bool HasSelection()
        {
            return this._fileCount > 0 || this._directoryCount > 0;
        }

        /// <summary>
        /// Formats the current upload queue summary
        /// </summary>
        /// <returns>Selection summary</returns>
        private string GetSelectionSummary()
        {
            if (!this.HasSelection())
                return "No files or folders selected";

            return this._fileCount + " file(s), " + this._directoryCount + " folder(s), " + this.FormatSize(this._totalBytes);
        }

        /// <summary>
        /// Formats the current file counter prefix
        /// </summary>
        /// <returns>Progress prefix</returns>
        private string GetProgressSummary()
        {
            if (this._uploadFileCount <= 0)
                return "";
            int currentFile = Math.Min(this._processedFiles + 1, this._uploadFileCount);
            return "[" + currentFile + "/" + this._uploadFileCount + "] ";
        }

        /// <summary>
        /// Formats a byte count for display
        /// </summary>
        /// <param name="bytes">Size in bytes</param>
        /// <returns>Formatted size string</returns>
        private string FormatSize(long bytes)
        {
            string result;
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

        #region IAsyncDisposable

        /// <summary>
        /// Cleanup JS references
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (this._isDisposed)
                return;
            this._isDisposed = true;

            try
            {
                if (this._jsModule != null)
                {
                    try
                    {
                        await this._jsModule.InvokeVoidAsync("dispose");
                    }
                    finally
                    {
                        await this._jsModule.DisposeAsync();
                    }
                }
            }
            catch (Exception ex) when (ex is JSDisconnectedException || ex is OperationCanceledException)
            {
            }
            finally
            {
                if (this._dotNetRef != null)
                {
                    this._dotNetRef.Dispose();
                    this._dotNetRef = null;
                }
            }
        }

        #endregion
    }
}
