using System.Text.Json;
using Bivium.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Bivium.Components.Shared
{
    /// <summary>
    /// Dialog for local authentication settings
    /// </summary>
    public partial class AuthSettingsDialog : ComponentBase
    {
        #region Parameters

        /// <summary>
        /// Callback when dialog is closed
        /// </summary>
        [Parameter]
        public EventCallback OnClose { get; set; }

        #endregion

        #region Class Variables

        private bool _isVisible = false;

        private bool _enabled = false;

        private bool _disabled = false;

        private bool _twoFactorEnabled = false;

        private bool _hasUser = false;

        private string _username = "";

        private string _passwordLabel = "Password:";

        private string _disabledLabel = "no";

        private string _twoFactorLabel = "disabled";

        private string _currentPassword = "";

        private string _newPassword = "";

        private string _twoFactorCode = "";

        private string _qrCodeDataUrl = "";

        private string _statusText = "";

        private IJSObjectReference _jsModule;

        #endregion

        #region Public Methods

        /// <summary>
        /// Shows the authentication settings dialog
        /// </summary>
        public async System.Threading.Tasks.Task Show()
        {
            this._statusText = "";
            this._enabled = false;
            this._disabled = false;
            this._twoFactorEnabled = false;
            this._hasUser = false;
            this._username = "";
            this._passwordLabel = "Password:";
            this._disabledLabel = "no";
            this._twoFactorLabel = "disabled";
            this._currentPassword = "";
            this._newPassword = "";
            this._twoFactorCode = "";
            this._qrCodeDataUrl = "";
            await this.LoadStatus();
            this._isVisible = true;
            this.StateHasChanged();
        }

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
        /// Loads authentication status from the API
        /// </summary>
        private async System.Threading.Tasks.Task LoadStatus()
        {
            await this.EnsureJsModule();
            JsFetchResult response = await this._jsModule.InvokeAsync<JsFetchResult>("getJsonResult", "/api/Settings/authentication");

            if (response.Ok && response.Data.ValueKind == JsonValueKind.Object)
            {
                if (response.Data.TryGetProperty("hasUser", out JsonElement hasUser))
                {
                    this._hasUser = hasUser.GetBoolean();
                    if (this._hasUser)
                    {
                        this._passwordLabel = "New password:";
                    }
                    else
                    {
                        this._passwordLabel = "Password:";
                    }
                }
                if (response.Data.TryGetProperty("enabled", out JsonElement enabled))
                {
                    this._enabled = enabled.GetBoolean();
                }
                if (response.Data.TryGetProperty("username", out JsonElement username))
                {
                    this._username = username.GetString() ?? "";
                }
                if (response.Data.TryGetProperty("disabled", out JsonElement disabled))
                {
                    this._disabled = disabled.GetBoolean();
                    if (this._disabled)
                    {
                        this._disabledLabel = "yes";
                    }
                    else
                    {
                        this._disabledLabel = "no";
                    }
                }
                if (response.Data.TryGetProperty("twoFactorEnabled", out JsonElement twoFactorEnabled))
                {
                    this._twoFactorEnabled = twoFactorEnabled.GetBoolean();
                    if (this._twoFactorEnabled)
                    {
                        this._twoFactorLabel = "enabled";
                    }
                    else
                    {
                        this._twoFactorLabel = "disabled";
                    }
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(response.Text))
                {
                    this._statusText = "Failed to load authentication settings";
                }
                else
                {
                    this._statusText = response.Text;
                }
            }
        }

        /// <summary>
        /// Saves main authentication settings
        /// </summary>
        private async System.Threading.Tasks.Task HandleSave()
        {
            AuthenticationSettingsRequest request = new AuthenticationSettingsRequest();
            request.Enabled = this._enabled;
            request.Username = this._username;
            request.CurrentPassword = this._currentPassword;
            request.NewPassword = this._newPassword;

            string json = JsonSerializer.Serialize(request);
            await this.EnsureJsModule();
            JsFetchResult response = await this._jsModule.InvokeAsync<JsFetchResult>("putJsonResult", "/api/Settings/authentication", json);

            if (response.Ok)
            {
                this._statusText = "Saved";
                this._currentPassword = "";
                this._newPassword = "";
                this._isVisible = false;
                await this.OnClose.InvokeAsync();
            }
            else
            {
                if (string.IsNullOrWhiteSpace(response.Text))
                {
                    this._statusText = "Failed to save authentication settings";
                }
                else
                {
                    this._statusText = response.Text;
                }
            }
        }

        /// <summary>
        /// Starts two-factor setup
        /// </summary>
        private async System.Threading.Tasks.Task HandleStartTwoFactorSetup()
        {
            await this.EnsureJsModule();
            TwoFactorVerifyRequest request = new TwoFactorVerifyRequest();
            request.CurrentPassword = this._currentPassword;
            string json = JsonSerializer.Serialize(request);
            JsFetchResult response = await this._jsModule.InvokeAsync<JsFetchResult>("postJsonResult", "/api/Settings/authentication/twofactor/setup", json);

            if (response.Ok && response.Data.ValueKind == JsonValueKind.Object)
            {
                if (response.Data.TryGetProperty("qrCodeDataUrl", out JsonElement qr))
                {
                    this._qrCodeDataUrl = qr.GetString();
                }
                this._statusText = "";
            }
            else
            {
                if (string.IsNullOrWhiteSpace(response.Text))
                {
                    this._statusText = "Failed to start 2FA setup";
                }
                else
                {
                    this._statusText = response.Text;
                }
            }
        }

        /// <summary>
        /// Enables two-factor authentication
        /// </summary>
        private async System.Threading.Tasks.Task HandleEnableTwoFactor()
        {
            TwoFactorVerifyRequest request = new TwoFactorVerifyRequest();
            request.CurrentPassword = this._currentPassword;
            request.Code = this._twoFactorCode;

            string json = JsonSerializer.Serialize(request);
            await this.EnsureJsModule();
            JsFetchResult response = await this._jsModule.InvokeAsync<JsFetchResult>("postJsonResult", "/api/Settings/authentication/twofactor/enable", json);

            if (response.Ok)
            {
                this._twoFactorEnabled = true;
                this._twoFactorLabel = "enabled";
                this._qrCodeDataUrl = "";
                this._currentPassword = "";
                this._twoFactorCode = "";
                this._statusText = "2FA enabled";
                this._isVisible = false;
                await this.OnClose.InvokeAsync();
            }
            else
            {
                if (string.IsNullOrWhiteSpace(response.Text))
                {
                    this._statusText = "Invalid 2FA code";
                }
                else
                {
                    this._statusText = response.Text;
                }
            }
        }

        /// <summary>
        /// Disables two-factor authentication
        /// </summary>
        private async System.Threading.Tasks.Task HandleDisableTwoFactor()
        {
            await this.EnsureJsModule();
            TwoFactorVerifyRequest request = new TwoFactorVerifyRequest();
            request.CurrentPassword = this._currentPassword;
            string json = JsonSerializer.Serialize(request);
            JsFetchResult response = await this._jsModule.InvokeAsync<JsFetchResult>("postJsonResult", "/api/Settings/authentication/twofactor/disable", json);

            if (response.Ok)
            {
                this._twoFactorEnabled = false;
                this._twoFactorLabel = "disabled";
                this._qrCodeDataUrl = "";
                this._currentPassword = "";
                this._twoFactorCode = "";
                this._statusText = "2FA disabled";
                this._isVisible = false;
                await this.OnClose.InvokeAsync();
            }
            else
            {
                if (string.IsNullOrWhiteSpace(response.Text))
                {
                    this._statusText = "Failed to disable 2FA";
                }
                else
                {
                    this._statusText = response.Text;
                }
            }
        }

        /// <summary>
        /// Handles cancel
        /// </summary>
        private async System.Threading.Tasks.Task HandleCancel()
        {
            this._isVisible = false;
            await this.OnClose.InvokeAsync();
        }

        #endregion
    }
}
