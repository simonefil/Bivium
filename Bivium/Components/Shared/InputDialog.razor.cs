using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Bivium.Components.Shared
{
    /// <summary>
    /// Dialog with a text input field for rename/mkdir operations
    /// </summary>
    public partial class InputDialog : ComponentBase
    {
        #region Parameters

        /// <summary>
        /// Callback when dialog is closed (returns input value or empty on cancel)
        /// </summary>
        [Parameter]
        public EventCallback<string> OnClose { get; set; }

        #endregion

        #region Class Variables

        /// <summary>
        /// Whether the dialog is visible
        /// </summary>
        private bool _isVisible = false;

        /// <summary>
        /// Dialog title
        /// </summary>
        private string _title = "";

        /// <summary>
        /// Label for the input field
        /// </summary>
        private string _label = "";

        /// <summary>
        /// Current input value
        /// </summary>
        private string _inputValue = "";

        /// <summary>
        /// Number of leading characters to select after focus, or -1 for no selection
        /// </summary>
        private int _selectionLength = -1;

        /// <summary>
        /// Reference to the input element for focus
        /// </summary>
        private ElementReference _inputElement;

        /// <summary>
        /// JS module reference for input text selection
        /// </summary>
        private IJSObjectReference _jsModule;

        #endregion

        #region Public Methods

        /// <summary>
        /// Shows the dialog with specified title, label, and default value
        /// </summary>
        /// <param name="title">Dialog title</param>
        /// <param name="label">Input label</param>
        /// <param name="defaultValue">Pre-filled input value</param>
        /// <param name="selectionLength">Number of leading characters to select, or -1 for no selection</param>
        public void Show(string title, string label, string defaultValue, int selectionLength = -1)
        {
            this._title = title;
            this._label = label;
            this._inputValue = defaultValue;
            this._selectionLength = selectionLength;
            this._isVisible = true;
            this.StateHasChanged();

            // Focus the input after render
            _ = this.FocusInputAsync();
        }

        /// <summary>
        /// Focuses the input element after render
        /// </summary>
        private async System.Threading.Tasks.Task FocusInputAsync()
        {
            await System.Threading.Tasks.Task.Delay(50);
            await this._inputElement.FocusAsync();

            if (this._selectionLength >= 0)
            {
                await this.EnsureJsModule();
                await this._jsModule.InvokeVoidAsync("selectInputText", this._inputElement, this._selectionLength);
            }
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
        /// Handles confirm
        /// </summary>
        private async System.Threading.Tasks.Task HandleConfirm()
        {
            this._isVisible = false;
            await this.OnClose.InvokeAsync(this._inputValue);
        }

        /// <summary>
        /// Handles cancel
        /// </summary>
        private async System.Threading.Tasks.Task HandleCancel()
        {
            this._isVisible = false;
            await this.OnClose.InvokeAsync("");
        }

        /// <summary>
        /// Handles Enter key in input field
        /// </summary>
        /// <param name="args">Keyboard event args</param>
        private void HandleKeyDown(KeyboardEventArgs args)
        {
            if (args.Key == "Enter")
            {
                _ = this.HandleConfirm();
            }
            else if (args.Key == "Escape")
            {
                _ = this.HandleCancel();
            }
        }

        #endregion
    }
}
