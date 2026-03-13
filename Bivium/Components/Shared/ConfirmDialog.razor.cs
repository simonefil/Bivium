using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Bivium.Components.Shared
{
    /// <summary>
    /// Confirmation dialog with OK/Cancel buttons
    /// </summary>
    public partial class ConfirmDialog : ComponentBase
    {
        #region Parameters

        /// <summary>
        /// Callback when dialog is closed
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
        /// Dialog title
        /// </summary>
        private string _title = "Confirm";

        /// <summary>
        /// Dialog message
        /// </summary>
        private string _message = "";

        /// <summary>
        /// Text for the confirm button
        /// </summary>
        private string _confirmText = "OK";

        /// <summary>
        /// Text for the cancel button
        /// </summary>
        private string _cancelText = "Cancel";

        /// <summary>
        /// Which button is focused (0 = cancel, 1 = confirm)
        /// </summary>
        private int _focusedButton = 1;

        /// <summary>
        /// Reference to the dialog element for focus
        /// </summary>
        private ElementReference _dialogElement;

        #endregion

        #region Properties

        /// <summary>
        /// Dialog title for template binding
        /// </summary>
        public string Title => this._title;

        /// <summary>
        /// Dialog message for template binding
        /// </summary>
        public string Message => this._message;

        /// <summary>
        /// Confirm button text for template binding
        /// </summary>
        public string ConfirmText => this._confirmText;

        /// <summary>
        /// Cancel button text for template binding
        /// </summary>
        public string CancelText => this._cancelText;

        #endregion

        #region Public Methods

        /// <summary>
        /// Shows the dialog with specified title, message, and button texts
        /// </summary>
        /// <param name="title">Dialog title</param>
        /// <param name="message">Dialog message</param>
        /// <param name="confirmText">Confirm button text</param>
        /// <param name="cancelText">Cancel button text (empty to hide)</param>
        public void Show(string title, string message, string confirmText, string cancelText)
        {
            this._title = title;
            this._message = message;
            this._confirmText = confirmText;
            this._cancelText = cancelText;
            this._focusedButton = 1;
            this._isVisible = true;
            this.StateHasChanged();

            // Focus the dialog after render
            _ = this.FocusDialogAsync();
        }

        /// <summary>
        /// Focuses the dialog element after render
        /// </summary>
        private async System.Threading.Tasks.Task FocusDialogAsync()
        {
            await System.Threading.Tasks.Task.Delay(50);
            await this._dialogElement.FocusAsync();
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
        /// Handles confirm button click
        /// </summary>
        private void HandleConfirm()
        {
            this._isVisible = false;
            this.OnClose.InvokeAsync(true);
        }

        /// <summary>
        /// Handles cancel button click
        /// </summary>
        private void HandleCancel()
        {
            this._isVisible = false;
            this.OnClose.InvokeAsync(false);
        }

        /// <summary>
        /// Handles keyboard events in the dialog
        /// </summary>
        /// <param name="args">Keyboard event args</param>
        private void HandleKeyDown(KeyboardEventArgs args)
        {
            if (args.Key == "Enter")
            {
                // Confirm or cancel based on focused button
                if (this._focusedButton == 0)
                {
                    this.HandleCancel();
                }
                else
                {
                    this.HandleConfirm();
                }
            }
            else if (args.Key == "Escape")
            {
                this.HandleCancel();
            }
            else if (args.Key == "Tab" || args.Key == "ArrowLeft" || args.Key == "ArrowRight")
            {
                // Toggle between cancel and confirm buttons
                if (!string.IsNullOrEmpty(this._cancelText))
                {
                    this._focusedButton = this._focusedButton == 0 ? 1 : 0;
                }
            }
        }

        #endregion
    }
}
