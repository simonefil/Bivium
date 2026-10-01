using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Bivium.Models;
using System.Collections.Generic;

namespace Bivium.Components.Shared
{
    /// <summary>
    /// Dialog for selecting archive format and output file name
    /// </summary>
    public partial class CompressDialog : ComponentBase
    {
        #region Parameters

        /// <summary>
        /// Callback when dialog is closed (returns format and output name, or empty name on cancel)
        /// </summary>
        [Parameter]
        public EventCallback<(ArchiveFormat Format, string OutputName)> OnClose { get; set; }

        /// <summary>
        /// Rivalida l'autorità del browser prima di confermare la compressione
        /// </summary>
        [Parameter]
        public Func<bool> CanInvoke { get; set; }

        #endregion

        #region Class Variables

        /// <summary>
        /// Whether the dialog is visible
        /// </summary>
        private bool _isVisible = false;

        /// <summary>
        /// Selected archive format
        /// </summary>
        private ArchiveFormat _selectedFormat = ArchiveFormat.Zip;

        /// <summary>
        /// Output file name entered by the user
        /// </summary>
        private string _outputName = "";

        /// <summary>
        /// Base name derived from the source selection
        /// </summary>
        private string _baseName = "";

        /// <summary>
        /// Reference to the output name input for focus
        /// </summary>
        private Radzen.Blazor.RadzenTextBox _outputNameElement;

        /// <summary>Formati esistenti con etichette leggibili per il dropdown</summary>
        private readonly Dictionary<ArchiveFormat, string> _formats = new Dictionary<ArchiveFormat, string>
        {
            { ArchiveFormat.Zip, "ZIP (.zip)" },
            { ArchiveFormat.TarGz, "TAR.GZ (.tar.gz)" },
            { ArchiveFormat.TarBz2, "TAR.BZ2 (.tar.bz2)" },
            { ArchiveFormat.TarXz, "TAR.XZ (.tar.xz)" },
            { ArchiveFormat.TarZst, "TAR.ZST (.tar.zst)" },
            { ArchiveFormat.Tar, "TAR (.tar)" }
        };

        #endregion

        #region Public Methods

        /// <summary>
        /// Shows the dialog with a suggested base name
        /// </summary>
        /// <param name="baseName">Suggested archive base name (without extension)</param>
        public void Show(string baseName)
        {
            this._baseName = baseName;
            this._selectedFormat = ArchiveFormat.Zip;
            this._outputName = baseName + ".zip";
            this._isVisible = true;
            this.StateHasChanged();

        }

        /// <summary>
        /// Focuses the output name input after render
        /// </summary>
        /// <param name="firstRender">Primo mount reale del contenuto</param>
        private async System.Threading.Tasks.Task HandleContentRenderedAsync(bool firstRender)
        {
            if (firstRender && this._isVisible)
                await this._outputNameElement.Element.FocusAsync();
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
        /// Handles format dropdown change and updates the file extension
        /// </summary>
        /// <param name="args">Change event args</param>
        private void HandleFormatChange(object args)
        {
            string value = args.ToString();

            if (value == "Zip")
            {
                this._selectedFormat = ArchiveFormat.Zip;
            }
            else if (value == "Tar")
            {
                this._selectedFormat = ArchiveFormat.Tar;
            }
            else if (value == "TarGz")
            {
                this._selectedFormat = ArchiveFormat.TarGz;
            }
            else if (value == "TarBz2")
            {
                this._selectedFormat = ArchiveFormat.TarBz2;
            }
            else if (value == "TarXz")
            {
                this._selectedFormat = ArchiveFormat.TarXz;
            }
            else if (value == "TarZst")
            {
                this._selectedFormat = ArchiveFormat.TarZst;
            }

            // Update output name extension
            this.UpdateOutputExtension();
        }

        /// <summary>
        /// Updates the output file name extension based on selected format
        /// </summary>
        private void UpdateOutputExtension()
        {
            string ext = ".zip";

            if (this._selectedFormat == ArchiveFormat.Tar)
            {
                ext = ".tar";
            }
            else if (this._selectedFormat == ArchiveFormat.TarGz)
            {
                ext = ".tar.gz";
            }
            else if (this._selectedFormat == ArchiveFormat.TarBz2)
            {
                ext = ".tar.bz2";
            }
            else if (this._selectedFormat == ArchiveFormat.TarXz)
            {
                ext = ".tar.xz";
            }
            else if (this._selectedFormat == ArchiveFormat.TarZst)
            {
                ext = ".tar.zst";
            }

            this._outputName = this._baseName + ext;
        }

        /// <summary>
        /// Handles confirm button
        /// </summary>
        private async System.Threading.Tasks.Task HandleConfirm()
        {
            if (this.CanInvoke != null && !this.CanInvoke())
                return;

            this._isVisible = false;
            await this.OnClose.InvokeAsync((this._selectedFormat, this._outputName));
        }

        /// <summary>
        /// Handles cancel button
        /// </summary>
        private async System.Threading.Tasks.Task HandleCancel()
        {
            this._isVisible = false;
            await this.OnClose.InvokeAsync((this._selectedFormat, ""));
        }

        /// <summary>
        /// Handles keyboard input in the output name field
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
