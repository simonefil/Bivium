using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Bivium.Models;

namespace Bivium.Components.Shared
{
    /// <summary>
    /// Three-way overwrite confirmation dialog
    /// </summary>
    public partial class OverwriteDialog : ComponentBase
    {
        #region Parameters

        /// <summary>
        /// Callback when dialog is closed
        /// </summary>
        [Parameter]
        public EventCallback<OverwriteChoice> OnClose { get; set; }

        #endregion

        #region Class Variables

        /// <summary>
        /// Whether the dialog is visible
        /// </summary>
        private bool _isVisible = false;

        /// <summary>
        /// Dialog title
        /// </summary>
        private string _title = "Overwrite";

        /// <summary>
        /// Dialog message
        /// </summary>
        private string _message = "";

        /// <summary>
        /// Which button is focused (0 = no, 1 = yes to all, 2 = yes)
        /// </summary>
        private int _focusedButton = 2;

        /// <summary>
        /// Reference to the dialog element for focus
        /// </summary>
        private ElementReference _dialogElement;

        #endregion

        #region Public Methods

        /// <summary>
        /// Shows the overwrite dialog
        /// </summary>
        /// <param name="title">Dialog title</param>
        /// <param name="message">Dialog message</param>
        public void Show(string title, string message)
        {
            this._title = title;
            this._message = message;
            this._focusedButton = 2;
            this._isVisible = true;
            this.StateHasChanged();

            _ = this.FocusDialogAsync();
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
        /// Focuses the dialog element after render
        /// </summary>
        private async System.Threading.Tasks.Task FocusDialogAsync()
        {
            await System.Threading.Tasks.Task.Delay(50);
            await this._dialogElement.FocusAsync();
        }

        /// <summary>
        /// Handles Yes button click
        /// </summary>
        private async System.Threading.Tasks.Task HandleYes()
        {
            this._isVisible = false;
            await this.OnClose.InvokeAsync(OverwriteChoice.Yes);
        }

        /// <summary>
        /// Handles Yes to all button click
        /// </summary>
        private async System.Threading.Tasks.Task HandleYesToAll()
        {
            this._isVisible = false;
            await this.OnClose.InvokeAsync(OverwriteChoice.YesToAll);
        }

        /// <summary>
        /// Handles No button click
        /// </summary>
        private async System.Threading.Tasks.Task HandleNo()
        {
            this._isVisible = false;
            await this.OnClose.InvokeAsync(OverwriteChoice.No);
        }

        /// <summary>
        /// Handles keyboard events in the dialog
        /// </summary>
        /// <param name="args">Keyboard event args</param>
        private void HandleKeyDown(KeyboardEventArgs args)
        {
            if (args.Key == "Enter")
            {
                if (this._focusedButton == 0)
                {
                    _ = this.HandleNo();
                }
                else if (this._focusedButton == 1)
                {
                    _ = this.HandleYesToAll();
                }
                else
                {
                    _ = this.HandleYes();
                }
            }
            else if (args.Key == "Escape")
            {
                _ = this.HandleNo();
            }
            else if (args.Key == "Tab" || args.Key == "ArrowRight")
            {
                this._focusedButton++;
                if (this._focusedButton > 2)
                {
                    this._focusedButton = 0;
                }
            }
            else if (args.Key == "ArrowLeft")
            {
                this._focusedButton--;
                if (this._focusedButton < 0)
                {
                    this._focusedButton = 2;
                }
            }
        }

        #endregion
    }
}
