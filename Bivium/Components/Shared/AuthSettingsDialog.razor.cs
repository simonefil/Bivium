using System.Text.Json;
using System;
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

        [Parameter]
        public string AttachmentId { get; set; } = "";

        [Parameter]
        public long LeaseGeneration { get; set; }

        /// <summary>
        /// Revalidates the browser authority before and after authentication changes
        /// </summary>
        [Parameter]
        public Func<bool> CanInvoke { get; set; }

        [Parameter] public WorkspaceWorkflowSnapshot Workflow { get; set; }
        [Inject] private Bivium.Services.BiviumWorkspaceService WorkspaceService { get; set; }
        private readonly WorkspaceFormBinding _binding = new WorkspaceFormBinding();
        /// <summary>Challenge allowlist indicator; on takeover the server cancels it</summary>
        private bool _pendingMfaSetup;

        /// <summary>Authentication requests actually in progress remain protected until the outcome</summary>
        internal bool HasNonTransferableWork => this._isSaving || this._isOpening;

        /// <summary>Allowlist; sensitive fields remain exclusively in this adapter</summary>
        private string GetDraft() => JsonSerializer.Serialize(new WorkspaceAuthenticationDraft(this._enabled, this._disabled, this._twoFactorEnabled, this._hasUser, this._mfaPanelVisible, this._passwordPanelVisible, this._username, this._configuredUsername, this._pendingMfaSetup));

        private void PublishDraft() => this._binding.Publish(this.WorkspaceService, new WorkspaceClientToken(this.AttachmentId, this.LeaseGeneration), this.GetDraft());

        /// <summary>Restores the dialog and section without calling endpoints or transferring secrets</summary>
        protected override void OnParametersSet()
        {
            if (this.Workflow == null)
            {
                this._isVisible = false;
                this.ClearSensitiveFields();
                return;
            }
            bool changed = this._binding.Current?.Id != this.Workflow.Id || this._binding.Generation != this.LeaseGeneration;
            if (!this._binding.Adopt(this.Workflow, this.LeaseGeneration))
                return;
            WorkspaceAuthenticationDraft draft = JsonSerializer.Deserialize<WorkspaceAuthenticationDraft>(this.Workflow.Draft);
            this._enabled = draft.Enabled;
            this._disabled = draft.Disabled;
            this._twoFactorEnabled = draft.TwoFactorEnabled;
            this._hasUser = draft.HasUser;
            this._mfaPanelVisible = draft.MfaPanelVisible;
            this._passwordPanelVisible = draft.PasswordPanelVisible;
            this._username = draft.Username;
            this._configuredUsername = draft.ConfiguredUsername;
            this._pendingMfaSetup = draft.PendingMfaSetup;
            this._disabledLabel = this._disabled ? "yes" : "no";
            this._twoFactorLabel = this._twoFactorEnabled ? "enabled" : "disabled";
            if (changed)
            {
                this.ClearSensitiveFields();
                this._statusText = "";
            }
            this._isVisible = this.Workflow.Phase == WorkspaceWorkflowPhase.AwaitingInput;
        }

        /// <summary>The pending MFA guard does not prevent the checkpoint of the other sections</summary>
        internal System.Threading.Tasks.Task<bool> FlushForHandoffAsync(CancellationToken cancellationToken) => this._binding.FlushAsync(this.WorkspaceService, new WorkspaceClientToken(this.AttachmentId, this.LeaseGeneration), this.GetDraft(), cancellationToken);

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

        /// <summary>Style of the displayed outcome: error, warning or confirmation</summary>
        private Radzen.AlertStyle _statusStyle = Radzen.AlertStyle.Danger;

        private IJSObjectReference _jsModule;

        /// <summary>Prevents closing and double commits during non-cancellable requests</summary>
        private bool _isSaving;

        /// <summary>Requests in progress cannot use a new generation after an await</summary>
        private WorkspaceClientToken _mutationToken;

        /// <summary>Reserves the open while the initial state is being loaded</summary>
        private bool _isOpening;

        #endregion

        #region Public Methods

        /// <summary>
        /// Shows the authentication settings dialog
        /// </summary>
        public async System.Threading.Tasks.Task Show()
        {
            if (this._isOpening || this.Workflow?.IsActive == true || !this.CanInvokeMutation())
                return;

            WorkspaceClientToken token = new WorkspaceClientToken(this.AttachmentId, this.LeaseGeneration);
            this._isOpening = true;
            try
            {
                await this.LoadAndOpenAsync(token);
            }
            finally
            {
                this._isOpening = false;
            }
        }

        #endregion

        #region Private Methods

        /// <summary>Loads the state and admits only the open that still belongs to the captured lease</summary>
        /// <param name="token">Lease at the start of the open</param>
        private async System.Threading.Tasks.Task LoadAndOpenAsync(WorkspaceClientToken token)
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
            this._isVisible = false;

            try
            {
                await this.LoadStatus(token);
            }
            catch (Exception ex)
            {
                this.SetStatus("Failed to load authentication settings: " + ex.Message, Radzen.AlertStyle.Danger);
            }
            if (this.AttachmentId != token.AttachmentId || this.LeaseGeneration != token.Generation || !this.CanInvokeMutation())
                return;
            WorkspaceWorkflowSnapshot opened;
            try
            {
                opened = this.WorkspaceService.BeginFormWorkflow(token, WorkspaceWorkflowKind.Authentication, new WorkspaceWorkflowInvocation("", "", -1, "Authentication", "", -1), this.GetDraft());
            }
            catch (InvalidOperationException)
            {
                // Another form may have been admitted during loading; it does not replace it
                return;
            }
            catch (UnauthorizedAccessException)
            {
                // A lease change between the check and the admission is a rejection, not a circuit fault
                return;
            }
            this._binding.Adopt(opened, this.LeaseGeneration);
            this._isVisible = true;
            this.StateHasChanged();
        }

        /// <summary>Protects the modification actions without altering their payload and authority</summary>
        /// <param name="mutation">Existing action to execute</param>
        private async System.Threading.Tasks.Task ExecuteMutationAsync(Func<System.Threading.Tasks.Task> mutation)
        {
            if (this._isSaving)
                return;

            this._isSaving = true;
            WorkspaceClientToken captured = new WorkspaceClientToken(this.AttachmentId, this.LeaseGeneration);
            this._mutationToken = captured;
            this.StateHasChanged();
            try
            {
                await mutation();
            }
            finally
            {
                this._isSaving = false;
                if (this.AttachmentId != captured.AttachmentId || this.LeaseGeneration != captured.Generation || !this.WorkspaceService.ValidatePublication(captured))
                    this.ClearSensitiveFields();
            }
        }

        /// <summary>Does not retain passwords, tokens or challenges when the adapter loses the lease</summary>
        private void ClearSensitiveFields()
        {
            this._newPassword = this._confirmPassword = this._mfaPassword = this._twoFactorCode = this._twoFactorSecret = this._qrCodeDataUrl = "";
            this._changeCurrentPassword = this._changeNewPassword = this._changeConfirmPassword = "";
            this._pendingMfaSetup = false;
        }

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
        private async System.Threading.Tasks.Task LoadStatus(WorkspaceClientToken token)
        {
            await this.EnsureJsModule();
            JsFetchResult response = await this._jsModule.InvokeAsync<JsFetchResult>("getJsonResult", "/api/Settings/authentication");
            if (this.AttachmentId != token.AttachmentId || this.LeaseGeneration != token.Generation || !this.WorkspaceService.ValidatePublication(token))
                return;

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
                this.SetStatus(string.IsNullOrWhiteSpace(response.Text) ? "Failed to load authentication settings" : response.Text, Radzen.AlertStyle.Danger);
            }
        }

        /// <summary>
        /// Saves main authentication settings
        /// </summary>
        private async System.Threading.Tasks.Task HandleSave()
        {
            if (!this.CanInvokeMutation())
                return;

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
            JsFetchResult response = await this._jsModule.InvokeAsync<JsFetchResult>("putJsonResult", "/api/Settings/authentication", json, this._mutationToken.AttachmentId, this._mutationToken.Generation);

            if (!this.CanInvokeMutation())
                return;

            if (response.Ok)
            {
                this._statusText = "";
                this._newPassword = "";
                this._confirmPassword = "";
                this._isVisible = false;
                this._binding.Respond(this.WorkspaceService, new WorkspaceClientToken(this.AttachmentId, this.LeaseGeneration), true);
                this.NotificationService.Notify(Radzen.NotificationSeverity.Success, "Authentication settings saved", "", 4000);
                await this.OnClose.InvokeAsync();
            }
            else
            {
                this.SetStatus(string.IsNullOrWhiteSpace(response.Text) ? "Failed to save authentication settings" : response.Text, Radzen.AlertStyle.Danger);
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
            this.PublishDraft();
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
            this.PublishDraft();
        }

        /// <summary>
        /// Starts two-factor setup
        /// </summary>
        private async System.Threading.Tasks.Task HandleStartTwoFactorSetup()
        {
            if (!this.CanInvokeMutation())
                return;

            WorkspaceClientToken captured = new WorkspaceClientToken(this.AttachmentId, this.LeaseGeneration);
            await this.EnsureJsModule();
            TwoFactorVerifyRequest request = new TwoFactorVerifyRequest();
            request.CurrentPassword = this._mfaPassword;
            string json = JsonSerializer.Serialize(request);
            JsFetchResult response = await this._jsModule.InvokeAsync<JsFetchResult>("postJsonResult", "/api/Settings/authentication/twofactor/setup", json, captured.AttachmentId, captured.Generation);

            if (this.AttachmentId != captured.AttachmentId || this.LeaseGeneration != captured.Generation || !this.WorkspaceService.ValidatePublication(captured) || !this.CanInvokeMutation())
                return;

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
                this._pendingMfaSetup = true;
                this.PublishDraft();
                this._statusText = "";
            }
            else
            {
                this.SetStatus(string.IsNullOrWhiteSpace(response.Text) ? "Failed to start 2FA setup" : response.Text, Radzen.AlertStyle.Danger);
            }
        }

        /// <summary>
        /// Copies the pending two-factor secret
        /// </summary>
        private async System.Threading.Tasks.Task HandleCopyTwoFactorSecret()
        {
            if (!this.CanInvokeMutation())
                return;
            await this.EnsureJsModule();
            if (!this.CanInvokeMutation())
                return;
            bool copied = await this._jsModule.InvokeAsync<bool>("copyText", this._twoFactorSecret);
            this.SetStatus(copied ? "Secret copied" : "Failed to copy secret", copied ? Radzen.AlertStyle.Success : Radzen.AlertStyle.Danger);
        }

        /// <summary>
        /// Enables two-factor authentication
        /// </summary>
        private async System.Threading.Tasks.Task HandleEnableTwoFactor()
        {
            if (!this.CanInvokeMutation())
                return;

            TwoFactorVerifyRequest request = new TwoFactorVerifyRequest();
            request.CurrentPassword = this._mfaPassword;
            request.Code = this._twoFactorCode;

            string json = JsonSerializer.Serialize(request);
            await this.EnsureJsModule();
            JsFetchResult response = await this._jsModule.InvokeAsync<JsFetchResult>("postJsonResult", "/api/Settings/authentication/twofactor/enable", json, this._mutationToken.AttachmentId, this._mutationToken.Generation);

            if (!this.CanInvokeMutation())
                return;

            if (response.Ok)
            {
                this._twoFactorEnabled = true;
                this._twoFactorLabel = "enabled";
                this._qrCodeDataUrl = "";
                this._twoFactorSecret = "";
                this._mfaPassword = "";
                this._twoFactorCode = "";
                this._statusText = "";
                this._pendingMfaSetup = false;
                this._isVisible = false;
                this._binding.Respond(this.WorkspaceService, new WorkspaceClientToken(this.AttachmentId, this.LeaseGeneration), true);
                this.NotificationService.Notify(Radzen.NotificationSeverity.Success, "2FA enabled", "", 4000);
                await this.OnClose.InvokeAsync();
            }
            else
            {
                this.SetStatus(string.IsNullOrWhiteSpace(response.Text) ? "Invalid 2FA code" : response.Text, Radzen.AlertStyle.Danger);
            }
        }

        /// <summary>
        /// Disables two-factor authentication
        /// </summary>
        private async System.Threading.Tasks.Task HandleDisableTwoFactor()
        {
            if (!this.CanInvokeMutation())
                return;

            await this.EnsureJsModule();
            TwoFactorVerifyRequest request = new TwoFactorVerifyRequest();
            request.CurrentPassword = this._mfaPassword;
            string json = JsonSerializer.Serialize(request);
            JsFetchResult response = await this._jsModule.InvokeAsync<JsFetchResult>("postJsonResult", "/api/Settings/authentication/twofactor/disable", json, this._mutationToken.AttachmentId, this._mutationToken.Generation);

            if (!this.CanInvokeMutation())
                return;

            if (response.Ok)
            {
                this._twoFactorEnabled = false;
                this._twoFactorLabel = "disabled";
                this._qrCodeDataUrl = "";
                this._twoFactorSecret = "";
                this._mfaPassword = "";
                this._twoFactorCode = "";
                this._statusText = "";
                this._pendingMfaSetup = false;
                this._isVisible = false;
                this._binding.Respond(this.WorkspaceService, new WorkspaceClientToken(this.AttachmentId, this.LeaseGeneration), true);
                this.NotificationService.Notify(Radzen.NotificationSeverity.Success, "2FA disabled", "", 4000);
                await this.OnClose.InvokeAsync();
            }
            else
            {
                this.SetStatus(string.IsNullOrWhiteSpace(response.Text) ? "Failed to disable 2FA" : response.Text, Radzen.AlertStyle.Danger);
            }
        }

        /// <summary>
        /// Changes the configured administrator password
        /// </summary>
        private async System.Threading.Tasks.Task HandleChangePassword()
        {
            if (!this.CanInvokeMutation())
                return;

            ChangePasswordRequest request = new ChangePasswordRequest();
            request.CurrentPassword = this._changeCurrentPassword;
            request.NewPassword = this._changeNewPassword;
            request.ConfirmPassword = this._changeConfirmPassword;

            string json = JsonSerializer.Serialize(request);
            await this.EnsureJsModule();
            JsFetchResult response = await this._jsModule.InvokeAsync<JsFetchResult>("postJsonResult", "/api/Settings/authentication/password", json, this._mutationToken.AttachmentId, this._mutationToken.Generation);

            if (!this.CanInvokeMutation())
                return;

            if (response.Ok)
            {
                this._changeCurrentPassword = "";
                this._changeNewPassword = "";
                this._changeConfirmPassword = "";
                this._statusText = "";
                this._isVisible = false;
                this._binding.Respond(this.WorkspaceService, new WorkspaceClientToken(this.AttachmentId, this.LeaseGeneration), true);
                this.NotificationService.Notify(Radzen.NotificationSeverity.Success, "Password changed", "", 4000);
                await this.OnClose.InvokeAsync();
            }
            else
            {
                this.SetStatus(string.IsNullOrWhiteSpace(response.Text) ? "Failed to change password" : response.Text, Radzen.AlertStyle.Danger);
            }
        }

        /// <summary>
        /// Handles cancel
        /// </summary>
        private async System.Threading.Tasks.Task HandleCancel()
        {
            if (this._isSaving)
                return;
            this._binding.Respond(this.WorkspaceService, new WorkspaceClientToken(this.AttachmentId, this.LeaseGeneration), true);
            this._isVisible = false;
            await this.OnClose.InvokeAsync();
        }

        /// <summary>Shows an outcome in the dialog with the corresponding style</summary>
        /// <param name="text">Message</param>
        /// <param name="style">Error, warning or confirmation</param>
        private void SetStatus(string text, Radzen.AlertStyle style)
        {
            this._statusText = text;
            this._statusStyle = style;
        }

        /// <summary>
        /// Verifies that the browser retains authority for an authentication change
        /// </summary>
        /// <returns><see langword="true"/> when the command can proceed</returns>
        private bool CanInvokeMutation()
        {
            return (!this._isSaving || (this._mutationToken?.AttachmentId == this.AttachmentId && this._mutationToken.Generation == this.LeaseGeneration)) && !this._binding.Rejected && (this.CanInvoke == null || this.CanInvoke()) && this.WorkspaceService.ValidateMutation(new WorkspaceClientToken(this.AttachmentId, this.LeaseGeneration));
        }

        #endregion

        #region Dispose

        /// <summary>Releases the imported JS module, tolerating an already closed circuit</summary>
        public async System.Threading.Tasks.ValueTask DisposeAsync()
        {
            if (this._jsModule == null)
                return;
            try
            {
                await this._jsModule.DisposeAsync();
            }
            catch (Exception ex) when (ex is JSDisconnectedException || ex is OperationCanceledException)
            {
                // Circuit already released
            }
            this._jsModule = null;
        }

        #endregion
    }
}
