using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Bivium.Models;
using Bivium.Services;

namespace Bivium.Controllers
{
    /// <summary>
    /// API controller for reading and writing application settings
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class SettingsController : ControllerBase
    {
        #region Class Variables

        /// <summary>
        /// Application settings monitor for hot-reload
        /// </summary>
        private readonly IOptionsMonitor<CommanderSettings> _settingsMonitor;

        /// <summary>
        /// Hosting environment for resolving appsettings.json path
        /// </summary>
        private readonly IWebHostEnvironment _environment;

        /// <summary>
        /// Local authentication service
        /// </summary>
        private readonly AuthenticationService _authenticationService;

        /// <summary>
        /// Workspace authority for mutating settings requests
        /// </summary>
        private readonly BiviumWorkspaceService _workspaceService;

        #endregion

        #region Constructor

        /// <summary>
        /// Creates a new SettingsController
        /// </summary>
        /// <param name="settingsMonitor">Settings monitor for hot-reload</param>
        /// <param name="environment">Hosting environment</param>
        /// <param name="authenticationService">Authentication service</param>
        /// <param name="workspaceService">Workspace lease authority</param>
        public SettingsController(IOptionsMonitor<CommanderSettings> settingsMonitor, IWebHostEnvironment environment, AuthenticationService authenticationService, BiviumWorkspaceService workspaceService)
        {
            this._settingsMonitor = settingsMonitor;
            this._environment = environment;
            this._authenticationService = authenticationService;
            this._workspaceService = workspaceService;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Gets the current editable extensions list
        /// </summary>
        /// <returns>List of extensions</returns>
        [HttpGet("extensions")]
        public IActionResult GetExtensions()
        {
            List<string> extensions = this._settingsMonitor.CurrentValue.EditableExtensions;
            IActionResult result = this.Ok(extensions);
            return result;
        }

        /// <summary>
        /// Updates the editable extensions list and writes back to appsettings.json
        /// </summary>
        /// <param name="extensions">New list of extensions</param>
        /// <returns>Result</returns>
        [HttpPut("extensions")]
        public IActionResult UpdateExtensions([FromBody] List<string> extensions)
        {
            if (!this.HasValidWorkspaceLease())
                return this.Conflict("This browser no longer controls the workspace");
            CancellationToken cancellationToken = this.GetWorkspaceRevocationToken();
            IActionResult result;

            try
            {
                // Read current appsettings.json
                string settingsPath = Path.Combine(this._environment.ContentRootPath, "appsettings.json");
                string json = System.IO.File.ReadAllText(settingsPath);

                // Parse and update
                JsonDocumentOptions docOptions = new JsonDocumentOptions();
                docOptions.CommentHandling = JsonCommentHandling.Skip;
                JsonDocument doc = JsonDocument.Parse(json, docOptions);

                // Rebuild JSON with updated extensions
                Dictionary<string, object> root = this.JsonElementToDict(doc.RootElement);
                doc.Dispose();

                // Ensure CommanderSettings section exists
                if (!root.ContainsKey("CommanderSettings"))
                {
                    root["CommanderSettings"] = new Dictionary<string, object>();
                }

                Dictionary<string, object> settings = (Dictionary<string, object>)root["CommanderSettings"];
                settings["EditableExtensions"] = extensions;

                // Write back with indentation
                JsonSerializerOptions writeOptions = new JsonSerializerOptions();
                writeOptions.WriteIndented = true;
                string updatedJson = JsonSerializer.Serialize(root, writeOptions);
                cancellationToken.ThrowIfCancellationRequested();
                System.IO.File.WriteAllText(settingsPath, updatedJson);

                result = this.Ok(new { success = true });
            }
            catch (IOException ex)
            {
                result = this.StatusCode(500, "Failed to write settings: " + ex.Message);
            }
            catch (OperationCanceledException)
            {
                result = this.Conflict("This browser no longer controls the workspace");
            }

            return result;
        }

        /// <summary>
        /// Gets current authentication settings status
        /// </summary>
        /// <returns>Authentication status</returns>
        [HttpGet("authentication")]
        public IActionResult GetAuthentication()
        {
            AuthenticationStatus status = this._authenticationService.GetStatus(this.User);
            IActionResult result = this.Ok(status);
            return result;
        }

        /// <summary>
        /// Updates local authentication settings
        /// </summary>
        /// <param name="request">Authentication settings request</param>
        /// <returns>Result</returns>
        [HttpPut("authentication")]
        public async System.Threading.Tasks.Task<IActionResult> UpdateAuthentication([FromBody] AuthenticationSettingsRequest request)
        {
            if (!this.HasValidWorkspaceLease())
                return this.Conflict("This browser no longer controls the workspace");
            CancellationToken cancellationToken = this.GetWorkspaceRevocationToken();
            IActionResult result;

            try
            {
                if (!this._authenticationService.CanManageSettings(this.User))
                {
                    result = this.Unauthorized();
                }
                else
                {
                    await this._authenticationService.UpdateAuthenticationAsync(request, cancellationToken);
                    result = this.Ok(new { success = true });
                }
            }
            catch (OperationCanceledException)
            {
                result = this.Conflict("This browser no longer controls the workspace");
            }
            catch (InvalidOperationException ex)
            {
                result = this.BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                result = this.StatusCode(500, ex.Message);
            }

            return result;
        }

        /// <summary>
        /// Changes the local administrator password
        /// </summary>
        /// <param name="request">Password change request</param>
        /// <returns>Result</returns>
        [HttpPost("authentication/password")]
        public async System.Threading.Tasks.Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
        {
            if (!this.HasValidWorkspaceLease())
                return this.Conflict("This browser no longer controls the workspace");
            CancellationToken cancellationToken = this.GetWorkspaceRevocationToken();
            IActionResult result;

            try
            {
                if (!this._authenticationService.CanManageSettings(this.User))
                {
                    result = this.Unauthorized();
                }
                else
                {
                    await this._authenticationService.ChangePasswordAsync(request, cancellationToken);
                    result = this.Ok(new { success = true });
                }
            }
            catch (OperationCanceledException)
            {
                result = this.Conflict("This browser no longer controls the workspace");
            }
            catch (InvalidOperationException ex)
            {
                result = this.BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                result = this.StatusCode(500, ex.Message);
            }

            return result;
        }

        /// <summary>
        /// Creates a pending TOTP setup
        /// </summary>
        /// <returns>Setup payload</returns>
        [HttpPost("authentication/twofactor/setup")]
        public async System.Threading.Tasks.Task<IActionResult> SetupTwoFactor([FromBody] TwoFactorVerifyRequest request)
        {
            if (!this.HasValidWorkspaceLease())
                return this.Conflict("This browser no longer controls the workspace");
            CancellationToken cancellationToken = this.GetWorkspaceRevocationToken();
            IActionResult result;

            try
            {
                if (!this._authenticationService.CanManageSettings(this.User))
                {
                    result = this.Unauthorized();
                }
                else
                {
                    string currentPassword = "";
                    if (request != null)
                    {
                        currentPassword = request.CurrentPassword;
                    }

                    TwoFactorSetupResult setup = await this._authenticationService.CreateTwoFactorSetupAsync(currentPassword, cancellationToken);
                    result = this.Ok(setup);
                }
            }
            catch (OperationCanceledException)
            {
                result = this.Conflict("This browser no longer controls the workspace");
            }
            catch (InvalidOperationException ex)
            {
                result = this.BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                result = this.StatusCode(500, ex.Message);
            }

            return result;
        }

        /// <summary>
        /// Enables TOTP after verifying a setup code
        /// </summary>
        /// <param name="request">Validation request</param>
        /// <returns>Result</returns>
        [HttpPost("authentication/twofactor/enable")]
        public async System.Threading.Tasks.Task<IActionResult> EnableTwoFactor([FromBody] TwoFactorVerifyRequest request)
        {
            if (!this.HasValidWorkspaceLease())
                return this.Conflict("This browser no longer controls the workspace");
            CancellationToken cancellationToken = this.GetWorkspaceRevocationToken();
            IActionResult result;

            try
            {
                if (!this._authenticationService.CanManageSettings(this.User))
                {
                    result = this.Unauthorized();
                }
                else
                {
                    string code = "";
                    string currentPassword = "";
                    if (request != null)
                    {
                        code = request.Code;
                        currentPassword = request.CurrentPassword;
                    }

                    await this._authenticationService.EnableTwoFactorAsync(code, currentPassword, cancellationToken);
                    result = this.Ok(new { success = true });
                }
            }
            catch (OperationCanceledException)
            {
                result = this.Conflict("This browser no longer controls the workspace");
            }
            catch (InvalidOperationException ex)
            {
                result = this.BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                result = this.StatusCode(500, ex.Message);
            }

            return result;
        }

        /// <summary>
        /// Disables TOTP
        /// </summary>
        /// <returns>Result</returns>
        [HttpPost("authentication/twofactor/disable")]
        public async System.Threading.Tasks.Task<IActionResult> DisableTwoFactor([FromBody] TwoFactorVerifyRequest request)
        {
            if (!this.HasValidWorkspaceLease())
                return this.Conflict("This browser no longer controls the workspace");
            CancellationToken cancellationToken = this.GetWorkspaceRevocationToken();
            IActionResult result;

            try
            {
                if (!this._authenticationService.CanManageSettings(this.User))
                {
                    result = this.Unauthorized();
                }
                else
                {
                    string currentPassword = "";
                    if (request != null)
                    {
                        currentPassword = request.CurrentPassword;
                    }

                    await this._authenticationService.DisableTwoFactorAsync(currentPassword, cancellationToken);
                    result = this.Ok(new { success = true });
                }
            }
            catch (OperationCanceledException)
            {
                result = this.Conflict("This browser no longer controls the workspace");
            }
            catch (InvalidOperationException ex)
            {
                result = this.BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                result = this.StatusCode(500, ex.Message);
            }

            return result;
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Validates attachment and generation received through server-side headers
        /// </summary>
        /// <returns>True if the request owns the current lease</returns>
        private bool HasValidWorkspaceLease()
        {
            string attachmentId = this.Request.Headers["X-Bivium-Attachment"].ToString();
            string generationText = this.Request.Headers["X-Bivium-Lease-Generation"].ToString();
            long generation;
            return !string.IsNullOrEmpty(attachmentId) && long.TryParse(generationText, out generation) && this._workspaceService.ValidateMutation(new WorkspaceClientToken(attachmentId, generation));
        }

        /// <summary>
        /// Returns the server-side token revoked atomically during a takeover
        /// </summary>
        /// <returns>Request revocation token</returns>
        private CancellationToken GetWorkspaceRevocationToken()
        {
            string attachmentId = this.Request.Headers["X-Bivium-Attachment"].ToString();
            string generationText = this.Request.Headers["X-Bivium-Lease-Generation"].ToString();
            long generation;
            if (!long.TryParse(generationText, out generation))
                return new CancellationToken(true);
            return this._workspaceService.GetRevocationToken(new WorkspaceClientToken(attachmentId, generation));
        }

        /// <summary>
        /// Converts a JsonElement tree into a Dictionary for re-serialization
        /// </summary>
        /// <param name="element">JSON element to convert</param>
        /// <returns>Dictionary representation</returns>
        private Dictionary<string, object> JsonElementToDict(JsonElement element)
        {
            Dictionary<string, object> dict = new Dictionary<string, object>();

            foreach (JsonProperty prop in element.EnumerateObject())
            {
                dict[prop.Name] = this.JsonElementToObject(prop.Value);
            }

            return dict;
        }

        /// <summary>
        /// Converts a JsonElement value to the appropriate .NET type
        /// </summary>
        /// <param name="element">JSON element</param>
        /// <returns>.NET object</returns>
        private object JsonElementToObject(JsonElement element)
        {
            object result;

            if (element.ValueKind == JsonValueKind.Object)
            {
                result = this.JsonElementToDict(element);
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                List<object> list = new List<object>();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    list.Add(this.JsonElementToObject(item));
                }
                result = list;
            }
            else if (element.ValueKind == JsonValueKind.String)
            {
                result = element.GetString();
            }
            else if (element.ValueKind == JsonValueKind.Number)
            {
                if (element.TryGetInt64(out long l))
                {
                    result = l;
                }
                else
                {
                    result = element.GetDouble();
                }
            }
            else if (element.ValueKind == JsonValueKind.True)
            {
                result = true;
            }
            else if (element.ValueKind == JsonValueKind.False)
            {
                result = false;
            }
            else
            {
                result = null;
            }

            return result;
        }

        #endregion
    }
}
