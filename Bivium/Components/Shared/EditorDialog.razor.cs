using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Bivium.Components.Shared
{
    /// <summary>
    /// Floating editor window with Monaco Editor for editing text files
    /// </summary>
    public partial class EditorDialog : ComponentBase, IDisposable
    {
        #region Parameters

        /// <summary>
        /// Callback when the editor is closed (true if saved, false if cancelled)
        /// </summary>
        [Parameter]
        public EventCallback<bool> OnClose { get; set; }

        /// <summary>
        /// Callback to save file content, receives (filePath, content)
        /// </summary>
        [Parameter]
        public EventCallback<(string FilePath, string Content)> OnSave { get; set; }

        #endregion

        #region Class Variables

        /// <summary>
        /// Whether the editor window is visible
        /// </summary>
        private bool _isVisible = false;

        /// <summary>
        /// Full path of the file being edited
        /// </summary>
        private string _filePath = "";

        /// <summary>
        /// Display name of the file being edited
        /// </summary>
        private string _fileName = "";

        /// <summary>
        /// Whether the editor content has unsaved changes
        /// </summary>
        private bool _isDirty = false;

        /// <summary>
        /// JS module reference for editor interop
        /// </summary>
        private IJSObjectReference _jsModule = null;

        /// <summary>
        /// JS module reference for drag/resize interop
        /// </summary>
        private IJSObjectReference _interopModule = null;

        /// <summary>
        /// .NET reference for JS save callback
        /// </summary>
        private DotNetObjectReference<EditorDialog> _dotNetRef = null;

        /// <summary>
        /// Whether the JS editor has been initialized
        /// </summary>
        private bool _jsInitialized = false;

        /// <summary>
        /// Status bar text (file size, encoding info)
        /// </summary>
        private string _statusText = "";

        #endregion

        #region Public Methods

        /// <summary>
        /// Shows the editor window with the specified file content
        /// </summary>
        /// <param name="filePath">Full path to the file</param>
        /// <param name="content">Text content to load into the editor</param>
        public void Show(string filePath, string content)
        {
            this._filePath = filePath;
            this._fileName = System.IO.Path.GetFileName(filePath);
            this._isDirty = false;
            this._statusText = FormatFileSize(content.Length);
            this._isVisible = true;
            this.StateHasChanged();

            // Initialize editor after render
            _ = this.InitializeEditor(content);
        }

        /// <summary>
        /// Hides the editor window and disposes the Monaco instance
        /// </summary>
        public void Hide()
        {
            this._isVisible = false;
            this._isDirty = false;

            // Dispose the Monaco editor instance
            if (this._jsModule != null && this._jsInitialized)
            {
                _ = this._jsModule.InvokeVoidAsync("disposeEditor");
                this._jsInitialized = false;
            }

            this.StateHasChanged();
        }

        /// <summary>
        /// Returns whether the editor is currently visible
        /// </summary>
        /// <returns>True if visible</returns>
        public bool IsVisible()
        {
            return this._isVisible;
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Initializes the Monaco editor via JS interop
        /// </summary>
        /// <param name="content">Text content to load</param>
        private async System.Threading.Tasks.Task InitializeEditor(string content)
        {
            // Wait for the DOM to be ready
            await System.Threading.Tasks.Task.Delay(100);

            // Import JS modules
            if (this._jsModule == null)
            {
                this._jsModule = await this.JSRuntime.InvokeAsync<IJSObjectReference>("import", "./js/editor.js");
            }

            if (this._interopModule == null)
            {
                this._interopModule = await this.JSRuntime.InvokeAsync<IJSObjectReference>("import", "./js/interop.js");
            }

            // Determine language from file extension
            string extension = System.IO.Path.GetExtension(this._filePath).ToLowerInvariant();
            string language = await this._jsModule.InvokeAsync<string>("getLanguageFromExtension", extension);

            // Register Ctrl+S callback before editor init
            if (this._dotNetRef == null)
            {
                this._dotNetRef = DotNetObjectReference.Create(this);
            }
            await this._jsModule.InvokeVoidAsync("setSaveCallback", this._dotNetRef);

            // Initialize Monaco editor
            await this._jsModule.InvokeVoidAsync("initEditor", "monaco-container", content, language);
            this._jsInitialized = true;

            // Initialize drag and resize for the window
            await this._interopModule.InvokeVoidAsync("initWindowDrag", "editor-window", "editor-titlebar", "editor-resize-handle");
        }

        /// <summary>
        /// Called from JS when Ctrl+S is pressed in the editor
        /// </summary>
        [JSInvokable]
        public async System.Threading.Tasks.Task OnEditorSave()
        {
            await this.HandleSave();
        }

        /// <summary>
        /// Handles the save button click - retrieves content from Monaco and invokes callback
        /// </summary>
        private async System.Threading.Tasks.Task HandleSave()
        {
            if (this._jsModule == null || !this._jsInitialized)
            {
                return;
            }

            // Get current editor content from Monaco
            string content = await this._jsModule.InvokeAsync<string>("getEditorContent");

            // Invoke save callback with file path and content
            await this.OnSave.InvokeAsync((this._filePath, content));

            // Update status
            this._isDirty = false;
            this._statusText = FormatFileSize(content.Length) + " - Saved";
            this.StateHasChanged();
        }

        /// <summary>
        /// Handles the close button click
        /// </summary>
        private void HandleClose()
        {
            bool wasSaved = !this._isDirty;
            this.Hide();
            this.OnClose.InvokeAsync(wasSaved);
        }

        /// <summary>
        /// Formats a byte count as a human-readable file size string
        /// </summary>
        /// <param name="bytes">Number of bytes</param>
        /// <returns>Formatted size string</returns>
        private static string FormatFileSize(long bytes)
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
            else
            {
                result = (bytes / (1024.0 * 1024.0)).ToString("F1") + " MB";
            }

            return result;
        }

        #endregion

        #region IDisposable

        /// <summary>
        /// Cleanup Monaco editor and JS references
        /// </summary>
        public void Dispose()
        {
            if (this._jsModule != null && this._jsInitialized)
            {
                _ = this._jsModule.InvokeVoidAsync("disposeEditor");
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
