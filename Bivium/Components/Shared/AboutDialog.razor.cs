using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Components;

namespace Bivium.Components.Shared
{
    /// <summary>
    /// Dialog showing application info, version, license and repository
    /// </summary>
    public partial class AboutDialog : ComponentBase
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
        /// Application version string
        /// </summary>
        private string _version = "";

        /// <summary>
        /// Runtime description
        /// </summary>
        private string _runtime = "";

        /// <summary>
        /// Platform description
        /// </summary>
        private string _platform = "";

        /// <summary>
        /// Reference to the OK button for focus
        /// </summary>
        private ElementReference _okButton;

        #endregion

        #region Public Methods

        /// <summary>
        /// Shows the about dialog
        /// </summary>
        public void Show()
        {
            // Read version from assembly
            Assembly assembly = Assembly.GetExecutingAssembly();
            AssemblyInformationalVersionAttribute infoVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            if (infoVersion != null)
            {
                this._version = infoVersion.InformationalVersion;
            }
            else
            {
                this._version = assembly.GetName().Version.ToString();
            }

            // Runtime and platform info
            this._runtime = RuntimeInformation.FrameworkDescription;
            this._platform = RuntimeInformation.OSDescription;

            this._isVisible = true;
            this.StateHasChanged();

            // Focus the OK button after render
            _ = this.FocusButtonAsync();
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Focuses the OK button after render
        /// </summary>
        private async System.Threading.Tasks.Task FocusButtonAsync()
        {
            await System.Threading.Tasks.Task.Delay(50);
            await this._okButton.FocusAsync();
        }

        /// <summary>
        /// Handles close button click
        /// </summary>
        private void HandleClose()
        {
            this._isVisible = false;
            this.OnClose.InvokeAsync();
        }

        #endregion
    }
}
