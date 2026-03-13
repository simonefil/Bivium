using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Bivium.Components.Shared
{
    /// <summary>
    /// Dialog for chunked file upload with progress display
    /// </summary>
    public partial class UploadDialog : ComponentBase, IDisposable
    {
        #region Parameters

        /// <summary>
        /// Callback when dialog is closed (true if upload succeeded)
        /// </summary>
        [Parameter]
        public EventCallback<bool> OnClose { get; set; }

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
        /// Selected file name
        /// </summary>
        private string _fileName = "";

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
        /// JS module reference for upload interop
        /// </summary>
        private IJSObjectReference _jsModule;

        /// <summary>
        /// .NET object reference for JS callbacks
        /// </summary>
        private DotNetObjectReference<UploadDialog> _dotNetRef;

        /// <summary>
        /// Reference to the Browse button for focus
        /// </summary>
        private ElementReference _browseButton;

        #endregion

        #region Public Methods

        /// <summary>
        /// Shows the upload dialog for the specified destination directory
        /// </summary>
        /// <param name="destinationDir">Target directory path</param>
        public void Show(string destinationDir)
        {
            this._destinationDir = destinationDir;
            this._fileName = "";
            this._progress = 0;
            this._isUploading = false;
            this._statusText = "";
            this._isVisible = true;
            this.StateHasChanged();

            // Initialize the JS module and focus
            _ = this.InitializeAndFocusAsync();
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

        #region JS Invokable Methods

        /// <summary>
        /// Called from JS to update upload progress
        /// </summary>
        /// <param name="percent">Progress percentage (0-100)</param>
        [JSInvokable]
        public void OnUploadProgress(int percent)
        {
            this._progress = percent;
            this.InvokeAsync(() => this.StateHasChanged());
        }

        /// <summary>
        /// Called from JS when upload completes or fails
        /// </summary>
        /// <param name="success">True if upload succeeded</param>
        /// <param name="message">Error message on failure</param>
        [JSInvokable]
        public void OnUploadComplete(bool success, string message)
        {
            this._isUploading = false;

            if (success)
            {
                this._statusText = "Upload complete";
                this._isVisible = false;
                this.OnClose.InvokeAsync(true);
            }
            else
            {
                this._statusText = "Error: " + message;
            }

            this.InvokeAsync(() => this.StateHasChanged());
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Initializes JS module and focuses the Browse button
        /// </summary>
        private async System.Threading.Tasks.Task InitializeAndFocusAsync()
        {
            await this.InitializeJsModule();
            await this._browseButton.FocusAsync();
        }

        /// <summary>
        /// Initializes the JS upload module and passes the .NET reference
        /// </summary>
        private async System.Threading.Tasks.Task InitializeJsModule()
        {
            // Small delay for DOM readiness
            await System.Threading.Tasks.Task.Delay(50);

            if (this._jsModule == null)
            {
                this._jsModule = await this.JSRuntime.InvokeAsync<IJSObjectReference>("import", "./js/upload.js");
            }

            // Create .NET reference for callbacks
            if (this._dotNetRef == null)
            {
                this._dotNetRef = DotNetObjectReference.Create(this);
            }

            await this._jsModule.InvokeVoidAsync("initUpload", this._dotNetRef);
        }

        /// <summary>
        /// Handles the Browse button click - opens file picker via JS
        /// </summary>
        private async System.Threading.Tasks.Task HandleBrowse()
        {
            if (this._jsModule == null)
            {
                return;
            }

            string selectedName = await this._jsModule.InvokeAsync<string>("selectFile");

            if (!string.IsNullOrEmpty(selectedName))
            {
                this._fileName = selectedName;
                this._statusText = "";
                this.StateHasChanged();
            }
        }

        /// <summary>
        /// Handles the Upload button click - starts chunked upload via JS
        /// </summary>
        private async System.Threading.Tasks.Task HandleUpload()
        {
            if (this._jsModule == null || string.IsNullOrEmpty(this._fileName))
            {
                return;
            }

            this._isUploading = true;
            this._progress = 0;
            this._statusText = "Uploading...";
            this.StateHasChanged();

            await this._jsModule.InvokeVoidAsync("uploadFile", this._destinationDir, this._fileName);
        }

        /// <summary>
        /// Handles the Cancel button click
        /// </summary>
        private void HandleCancel()
        {
            if (this._isUploading)
            {
                return;
            }

            this._isVisible = false;
            this.OnClose.InvokeAsync(false);
        }

        /// <summary>
        /// Renders a TUI-style progress bar string
        /// </summary>
        /// <returns>Progress bar text representation</returns>
        private string RenderProgressBar()
        {
            // 30 character wide progress bar
            int barWidth = 30;
            int filled = (int)((this._progress / 100.0) * barWidth);
            if (filled > barWidth)
            {
                filled = barWidth;
            }

            string bar = "[";
            for (int i = 0; i < barWidth; i++)
            {
                if (i < filled)
                {
                    bar += "=";
                }
                else if (i == filled)
                {
                    bar += ">";
                }
                else
                {
                    bar += " ";
                }
            }
            bar += "] " + this._progress + "%";

            return bar;
        }

        #endregion

        #region IDisposable

        /// <summary>
        /// Cleanup JS references
        /// </summary>
        public void Dispose()
        {
            if (this._jsModule != null)
            {
                _ = this._jsModule.InvokeVoidAsync("dispose");
            }

            if (this._dotNetRef != null)
            {
                this._dotNetRef.Dispose();
                this._dotNetRef = null;
            }
        }

        #endregion
    }
}
