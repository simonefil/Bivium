using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

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
        /// Reference to the input element for focus
        /// </summary>
        private ElementReference _inputElement;

        #endregion

        #region Public Methods

        /// <summary>
        /// Shows the dialog with specified title, label, and default value
        /// </summary>
        /// <param name="title">Dialog title</param>
        /// <param name="label">Input label</param>
        /// <param name="defaultValue">Pre-filled input value</param>
        public void Show(string title, string label, string defaultValue)
        {
            this._title = title;
            this._label = label;
            this._inputValue = defaultValue;
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
        /// Handles confirm
        /// </summary>
        private void HandleConfirm()
        {
            this._isVisible = false;
            this.OnClose.InvokeAsync(this._inputValue);
        }

        /// <summary>
        /// Handles cancel
        /// </summary>
        private void HandleCancel()
        {
            this._isVisible = false;
            this.OnClose.InvokeAsync("");
        }

        /// <summary>
        /// Handles Enter key in input field
        /// </summary>
        /// <param name="args">Keyboard event args</param>
        private void HandleKeyDown(KeyboardEventArgs args)
        {
            if (args.Key == "Enter")
            {
                this.HandleConfirm();
            }
            else if (args.Key == "Escape")
            {
                this.HandleCancel();
            }
        }

        #endregion
    }
}
