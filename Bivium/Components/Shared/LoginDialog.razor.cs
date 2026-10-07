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

        /// <summary>Campo username montato nella pagina gated</summary>
        private Radzen.Blazor.RadzenTextBox _usernameInput;

        #endregion

        #region Private Methods

        /// <summary>Imposta il focus soltanto al primo mount della pagina</summary>
        /// <param name="firstRender">Primo render reale</param>
        protected override async System.Threading.Tasks.Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender)
                await this._usernameInput.Element.FocusAsync();
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
                JsFetchResult response = await this._jsModule.InvokeAsync<JsFetchResult>("postJsonResult", "/api/Auth/login", json);

                if (response.Ok)
                {
                    await this.OnLogin.InvokeAsync();
                    await this._jsModule.InvokeVoidAsync("reloadPage");
                }
                else
                {
                    this._statusText = this.GetFailureText(response);
                }
            }
            finally
            {
                this._isSubmitting = false;
            }
        }

        /// <summary>Distingue credenziali rifiutate da server irraggiungibile o in errore</summary>
        /// <param name="response">Esito della richiesta browser</param>
        /// <returns>Messaggio per l'utente</returns>
        private string GetFailureText(JsFetchResult response)
        {
            if (response.Status == 0)
                return "The server could not be reached. Check the connection and try again.";
            if (response.Status == 401)
            {
                if (response.Data.ValueKind == JsonValueKind.Object && response.Data.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(error.GetString()))
                    return error.GetString();
                return "Invalid credentials";
            }
            return "Login failed because of a server error (HTTP " + response.Status + "). Try again later.";
        }

        #endregion

        #region Public Methods

        /// <summary>Rilascia il modulo JS importato, tollerando il circuito già chiuso</summary>
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
                // Circuito già rilasciato
            }
            this._jsModule = null;
        }

        #endregion
    }
}
