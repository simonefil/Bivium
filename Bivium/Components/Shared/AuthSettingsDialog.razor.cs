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

        private bool _mfaPanelVisible = false;

        private bool _passwordPanelVisible = false;

        private string _username = "";

        private string _configuredUsername = "";

        private string _disabledLabel = "no";

        private string _twoFactorLabel = "disabled";

        private string _newPassword = "";

        private string _confirmPassword = "";

        private string _mfaPassword = "";

        private string _twoFactorCode = "";

        private string _twoFactorSecret = "";

        private string _qrCodeDataUrl = "";

        private string _changeCurrentPassword = "";

        private string _changeNewPassword = "";

        private string _changeConfirmPassword = "";

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
            this._mfaPanelVisible = false;
            this._passwordPanelVisible = false;
            this._username = "";
            this._configuredUsername = "";
            this._disabledLabel = "no";
            this._twoFactorLabel = "disabled";
            this._newPassword = "";
            this._confirmPassword = "";
            this._mfaPassword = "";
            this._twoFactorCode = "";
            this._twoFactorSecret = "";
            this._qrCodeDataUrl = "";
            this._changeCurrentPassword = "";
            this._changeNewPassword = "";
            this._changeConfirmPassword = "";
            this._isVisible = true;
            this.StateHasChanged();

            try
            {
                await this.LoadStatus();
            }
            catch (Exception ex)
            {
                this._statusText = "Failed to load authentication settings: " + ex.Message;
            }

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
                }
                if (response.Data.TryGetProperty("enabled", out JsonElement enabled))
                {
                    this._enabled = enabled.GetBoolean();
                }
                if (response.Data.TryGetProperty("username", out JsonElement username))
                {
                    this._username = username.GetString() ?? "";
                    this._configuredUsername = this._username;
                }
                if (response.Data.TryGetProperty("disabled", out JsonElement disabled))
                {
                    this._disabled = disabled.GetBoolean();
                    this._disabledLabel = this._disabled ? "yes" : "no";
                }
                if (response.Data.TryGetProperty("twoFactorEnabled", out JsonElement twoFactorEnabled))
                {
                    this._twoFactorEnabled = twoFactorEnabled.GetBoolean();
                    this._twoFactorLabel = this._twoFactorEnabled ? "enabled" : "disabled";
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
            if (this._enabled)
            {
                request.Username = this._username;
                if (!this._hasUser)
                {
                    request.NewPassword = this._newPassword;
                    request.ConfirmPassword = this._confirmPassword;
                }
            }
            else
            {
                request.Username = this._hasUser ? this._configuredUsername : "";
            }

            string json = JsonSerializer.Serialize(request);
            await this.EnsureJsModule();
            JsFetchResult response = await this._jsModule.InvokeAsync<JsFetchResult>("putJsonResult", "/api/Settings/authentication", json);

            if (response.Ok)
            {
                this._statusText = "Saved";
                this._newPassword = "";
                this._confirmPassword = "";
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
        /// Toggles the MFA panel
        /// </summary>
        private void HandleToggleMfaPanel()
        {
            this._mfaPanelVisible = !this._mfaPanelVisible;
            this._passwordPanelVisible = false;
            this._statusText = "";
            this._changeCurrentPassword = "";
            this._changeNewPassword = "";
            this._changeConfirmPassword = "";
            if (!this._mfaPanelVisible)
            {
                this._mfaPassword = "";
                this._twoFactorCode = "";
                this._twoFactorSecret = "";
                this._qrCodeDataUrl = "";
            }
        }

        /// <summary>
        /// Toggles the password change panel
        /// </summary>
        private void HandleTogglePasswordPanel()
        {
            this._passwordPanelVisible = !this._passwordPanelVisible;
            this._mfaPanelVisible = false;
            this._statusText = "";
            this._mfaPassword = "";
            this._twoFactorCode = "";
            this._twoFactorSecret = "";
            this._qrCodeDataUrl = "";
            if (!this._passwordPanelVisible)
            {
                this._changeCurrentPassword = "";
                this._changeNewPassword = "";
                this._changeConfirmPassword = "";
            }
        }

        /// <summary>
        /// Starts two-factor setup
        /// </summary>
        private async System.Threading.Tasks.Task HandleStartTwoFactorSetup()
        {
            await this.EnsureJsModule();
            TwoFactorVerifyRequest request = new TwoFactorVerifyRequest();
            request.CurrentPassword = this._mfaPassword;
            string json = JsonSerializer.Serialize(request);
            JsFetchResult response = await this._jsModule.InvokeAsync<JsFetchResult>("postJsonResult", "/api/Settings/authentication/twofactor/setup", json);

            if (response.Ok && response.Data.ValueKind == JsonValueKind.Object)
            {
                if (response.Data.TryGetProperty("qrCodeDataUrl", out JsonElement qr))
                {
                    this._qrCodeDataUrl = qr.GetString() ?? "";
                }
                if (response.Data.TryGetProperty("secret", out JsonElement secret))
                {
                    this._twoFactorSecret = secret.GetString() ?? "";
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
        /// Copies the pending two-factor secret
        /// </summary>
        private async System.Threading.Tasks.Task HandleCopyTwoFactorSecret()
        {
            await this.EnsureJsModule();
            bool copied = await this._jsModule.InvokeAsync<bool>("copyText", this._twoFactorSecret);
            this._statusText = copied ? "Secret copied" : "Failed to copy secret";
        }

        /// <summary>
        /// Enables two-factor authentication
        /// </summary>
        private async System.Threading.Tasks.Task HandleEnableTwoFactor()
        {
            TwoFactorVerifyRequest request = new TwoFactorVerifyRequest();
            request.CurrentPassword = this._mfaPassword;
            request.Code = this._twoFactorCode;

            string json = JsonSerializer.Serialize(request);
            await this.EnsureJsModule();
            JsFetchResult response = await this._jsModule.InvokeAsync<JsFetchResult>("postJsonResult", "/api/Settings/authentication/twofactor/enable", json);

            if (response.Ok)
            {
                this._twoFactorEnabled = true;
                this._twoFactorLabel = "enabled";
                this._qrCodeDataUrl = "";
                this._twoFactorSecret = "";
                this._mfaPassword = "";
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
            request.CurrentPassword = this._mfaPassword;
            string json = JsonSerializer.Serialize(request);
            JsFetchResult response = await this._jsModule.InvokeAsync<JsFetchResult>("postJsonResult", "/api/Settings/authentication/twofactor/disable", json);

            if (response.Ok)
            {
                this._twoFactorEnabled = false;
                this._twoFactorLabel = "disabled";
                this._qrCodeDataUrl = "";
                this._twoFactorSecret = "";
                this._mfaPassword = "";
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
        /// Changes the configured administrator password
        /// </summary>
        private async System.Threading.Tasks.Task HandleChangePassword()
        {
            ChangePasswordRequest request = new ChangePasswordRequest();
            request.CurrentPassword = this._changeCurrentPassword;
            request.NewPassword = this._changeNewPassword;
            request.ConfirmPassword = this._changeConfirmPassword;

            string json = JsonSerializer.Serialize(request);
            await this.EnsureJsModule();
            JsFetchResult response = await this._jsModule.InvokeAsync<JsFetchResult>("postJsonResult", "/api/Settings/authentication/password", json);

            if (response.Ok)
            {
                this._changeCurrentPassword = "";
                this._changeNewPassword = "";
                this._changeConfirmPassword = "";
                this._statusText = "Password changed";
                this._isVisible = false;
                await this.OnClose.InvokeAsync();
            }
            else
            {
                if (string.IsNullOrWhiteSpace(response.Text))
                {
                    this._statusText = "Failed to change password";
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
