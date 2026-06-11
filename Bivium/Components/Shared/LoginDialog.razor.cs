using System.Text.Json;
using Bivium.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Bivium.Components.Shared
{
    /// <summary>
    /// Login dialog for local administrator authentication
    /// </summary>
    public partial class LoginDialog : ComponentBase
    {
        #region Parameters

        /// <summary>
        /// Callback invoked after a successful login
        /// </summary>
        [Parameter]
        public EventCallback OnLogin { get; set; }

        /// <summary>
        /// Whether the configured administrator requires a TOTP code
        /// </summary>
        [Parameter]
        public bool RequiresTwoFactor { get; set; } = false;

        #endregion

        #region Class Variables

        private string _username = "";

        private string _password = "";

        private string _twoFactorCode = "";

        private string _statusText = "";

        private bool _isSubmitting = false;

        private IJSObjectReference _jsModule;

        #endregion

        #region Private Methods

        /// <summary>
        /// Ensures the JS module is loaded
        /// </summary>
        private async System.Threading.Tasks.Task EnsureJsModule()
        {
            if (this._jsModule == null)
            {
                this._jsModule = await this.JSRuntime.InvokeAsync<IJSObjectReference>("import", "./js/interop.js");
            }
        }

        /// <summary>
        /// Attempts login via browser fetch so the auth cookie is set in the browser
        /// </summary>
        private async System.Threading.Tasks.Task HandleLogin()
        {
            if (this._isSubmitting)
            {
                return;
            }

            this._isSubmitting = true;
            this._statusText = "";

            try
            {
                LoginRequest request = new LoginRequest();
                request.Username = this._username;
                request.Password = this._password;
                request.TwoFactorCode = this._twoFactorCode;

                string json = JsonSerializer.Serialize(request);
                await this.EnsureJsModule();
                bool success = await this._jsModule.InvokeAsync<bool>("postJson", "/api/Auth/login", json);

                if (success)
                {
                    await this.OnLogin.InvokeAsync();
                    await this._jsModule.InvokeVoidAsync("reloadPage");
                }
                else
                {
                    this._statusText = "Invalid credentials";
                }
            }
            finally
            {
                this._isSubmitting = false;
            }
        }

        #endregion
    }
}
