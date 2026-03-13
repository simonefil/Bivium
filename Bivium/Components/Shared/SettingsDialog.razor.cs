using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Bivium.Components.Shared
{
    /// <summary>
    /// Dialog for editing the list of editable file extensions
    /// </summary>
    public partial class SettingsDialog : ComponentBase
    {
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
        /// Extensions text, one per line
        /// </summary>
        private string _extensionsText = "";

        /// <summary>
        /// Status or error text
        /// </summary>
        private string _statusText = "";

        /// <summary>
        /// JS module reference for interop
        /// </summary>
        private IJSObjectReference _jsModule;

        /// <summary>
        /// Reference to the textarea for focus
        /// </summary>
        private ElementReference _textareaElement;

        #endregion

        #region Public Methods

        /// <summary>
        /// Shows the dialog with the current extensions list
        /// </summary>
        /// <param name="currentExtensions">List of current editable extensions</param>
        public void Show(List<string> currentExtensions)
        {
            this._extensionsText = string.Join("\n", currentExtensions);
            this._statusText = "";
            this._isVisible = true;
            this.StateHasChanged();

            // Focus the textarea after render
            _ = this.FocusTextareaAsync();
        }

        /// <summary>
        /// Focuses the textarea after render
        /// </summary>
        private async System.Threading.Tasks.Task FocusTextareaAsync()
        {
            await System.Threading.Tasks.Task.Delay(50);
            await this._textareaElement.FocusAsync();
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
        /// Ensures the JS interop module is loaded
        /// </summary>
        private async System.Threading.Tasks.Task EnsureJsModule()
        {
            if (this._jsModule == null)
            {
                this._jsModule = await this.JSRuntime.InvokeAsync<IJSObjectReference>("import", "./js/interop.js");
            }
        }

        /// <summary>
        /// Handles the Save button click - sends updated extensions to the server
        /// </summary>
        private async System.Threading.Tasks.Task HandleSave()
        {
            // Parse textarea back to a list of non-empty extensions
            string[] lines = this._extensionsText.Split('\n');
            List<string> extensions = new List<string>();
            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].Trim();
                if (!string.IsNullOrEmpty(trimmed))
                {
                    extensions.Add(trimmed);
                }
            }

            // Serialize extensions list to JSON
            string jsonBody = JsonSerializer.Serialize(extensions);

            // Load JS module and send PUT request via fetch
            await this.EnsureJsModule();
            bool success = await this._jsModule.InvokeAsync<bool>("putJson", "/api/Settings/extensions", jsonBody);

            if (success)
            {
                this._isVisible = false;
                await this.OnClose.InvokeAsync();
            }
            else
            {
                this._statusText = "Failed to save extensions";
            }
        }

        /// <summary>
        /// Handles the Cancel button click
        /// </summary>
        private void HandleCancel()
        {
            this._isVisible = false;
            this.OnClose.InvokeAsync();
        }

        #endregion
    }
}
