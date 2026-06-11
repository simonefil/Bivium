using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bivium.Models;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using OtpNet;
using QRCoder;

namespace Bivium.Services
{
    /// <summary>
    /// Handles local administrator authentication, lockout, and TOTP setup
    /// </summary>
    public class AuthenticationService
    {
        #region Constants

        private const int PERMANENT_LOCK_ATTEMPTS = 7;

        private const int MIN_PASSWORD_LENGTH = 12;

        private const string GENERIC_LOGIN_ERROR = "Invalid credentials";

        private const string SECURITY_STAMP_CLAIM = "BiviumSecurityStamp";

        #endregion

        #region Class Variables

        private readonly IOptionsMonitor<CommanderSettings> _settingsMonitor;

        private readonly IConfiguration _configuration;

        private readonly IWebHostEnvironment _environment;

        private readonly PasswordHasher<AuthenticationUserSettings> _passwordHasher;

        private readonly object _lock = new object();

        private int _failedAttempts = 0;

        private bool _permanentlyLockedInMemory = false;

        private string _pendingTwoFactorSecret = "";

        #endregion

        #region Constructor

        /// <summary>
        /// Creates a new authentication service
        /// </summary>
        /// <param name="settingsMonitor">Settings monitor</param>
        /// <param name="configuration">Configuration</param>
        /// <param name="environment">Hosting environment</param>
        public AuthenticationService(IOptionsMonitor<CommanderSettings> settingsMonitor, IConfiguration configuration, IWebHostEnvironment environment)
        {
            this._settingsMonitor = settingsMonitor;
            this._configuration = configuration;
            this._environment = environment;
            this._passwordHasher = new PasswordHasher<AuthenticationUserSettings>();
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Gets whether authentication is currently required
        /// </summary>
        /// <returns>True if authentication is required</returns>
        public bool IsAuthenticationRequired()
        {
            AuthenticationSettings auth = this.GetAuthenticationSettings();
            bool result = auth.Enabled && this.HasConfiguredUser(auth);
            return result;
        }

        /// <summary>
        /// Gets whether the specified principal can access the application
        /// </summary>
        /// <param name="principal">Current user principal</param>
        /// <returns>True if access is allowed</returns>
        public bool CanAccess(ClaimsPrincipal principal)
        {
            AuthenticationSettings auth = this.GetAuthenticationSettings();
            bool result = true;

            if (auth.Enabled && this.HasConfiguredUser(auth))
            {
                AuthenticationUserSettings user = auth.User;
                result = false;
                if (principal != null && principal.Identity != null && principal.Identity.IsAuthenticated && !user.Disabled && string.Equals(principal.Identity.Name, user.Username, StringComparison.OrdinalIgnoreCase) && this.PrincipalHasCurrentSecurityStamp(principal, user))
                {
                    result = true;
                }
            }

            return result;
        }

        /// <summary>
        /// Gets whether authentication settings can be managed by the current principal
        /// </summary>
        /// <param name="principal">Current user principal</param>
        /// <returns>True if settings can be managed</returns>
        public bool CanManageSettings(ClaimsPrincipal principal)
        {
            AuthenticationSettings auth = this.GetAuthenticationSettings();
            bool result;

            if (!this.HasConfiguredUser(auth))
            {
                result = true;
            }
            else if (!auth.Enabled)
            {
                result = true;
            }
            else
            {
                result = this.CanAccess(principal);
            }

            return result;
        }

        /// <summary>
        /// Gets authentication status for the UI
        /// </summary>
        /// <param name="principal">Current user principal</param>
        /// <returns>Authentication status</returns>
        public AuthenticationStatus GetStatus(ClaimsPrincipal principal)
        {
            AuthenticationSettings auth = this.GetAuthenticationSettings();
            AuthenticationUserSettings user = auth.User;

            AuthenticationStatus result = new AuthenticationStatus();
            result.Enabled = auth.Enabled;
            result.HasUser = this.HasConfiguredUser(auth);
            result.Required = auth.Enabled && this.HasConfiguredUser(auth);
            result.Authenticated = principal != null && principal.Identity != null && principal.Identity.IsAuthenticated && user != null && !user.Disabled && string.Equals(principal.Identity.Name, user.Username, StringComparison.OrdinalIgnoreCase) && this.PrincipalHasCurrentSecurityStamp(principal, user);
            result.CanManageSettings = this.CanManageSettings(principal);

            if (user != null)
            {
                result.Username = user.Username ?? "";
                result.Disabled = user.Disabled;
                result.TwoFactorEnabled = user.TwoFactor != null && user.TwoFactor.Enabled && !string.IsNullOrWhiteSpace(user.TwoFactor.Secret);
            }

            return result;
        }

        /// <summary>
        /// Validates a login attempt, applying lockout policy on failure
        /// </summary>
        /// <param name="request">Login request</param>
        /// <returns>Login result</returns>
        public async System.Threading.Tasks.Task<LoginValidationResult> ValidateLoginAsync(LoginRequest request)
        {
            LoginValidationResult result = new LoginValidationResult();
            result.Success = false;
            result.ErrorMessage = GENERIC_LOGIN_ERROR;

            AuthenticationSettings auth = this.GetAuthenticationSettings();
            AuthenticationUserSettings user = auth.User;
            bool valid = false;

            if (!auth.Enabled || !this.HasConfiguredUser(auth))
            {
                return result;
            }

            if (request == null)
            {
                await this.RegisterFailureAsync();
                return result;
            }

            if (this.HasConfiguredUser(auth) && !user.Disabled && !this._permanentlyLockedInMemory)
            {
                string username = "";
                if (request.Username != null)
                {
                    username = request.Username.Trim();
                }

                bool usernameMatches = string.Equals(username, user.Username, StringComparison.OrdinalIgnoreCase);

                if (usernameMatches)
                {
                    PasswordVerificationResult passwordResult = this._passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password ?? "");
                    bool passwordValid = passwordResult == PasswordVerificationResult.Success || passwordResult == PasswordVerificationResult.SuccessRehashNeeded;

                    bool twoFactorValid = this.ValidateConfiguredTwoFactor(user, request.TwoFactorCode);
                    valid = passwordValid && twoFactorValid;
                }
            }

            if (valid)
            {
                this.ResetFailures();
                result.Success = true;
                result.ErrorMessage = "";
                result.Principal = this.CreatePrincipal(user);
            }
            else
            {
                await this.RegisterFailureAsync();
            }

            return result;
        }

        /// <summary>
        /// Updates main authentication settings and local admin credentials
        /// </summary>
        /// <param name="request">Settings update request</param>
        public async System.Threading.Tasks.Task UpdateAuthenticationAsync(AuthenticationSettingsRequest request)
        {
            if (request == null)
            {
                throw new InvalidOperationException("Missing authentication settings");
            }

            string username = "";
            if (request.Username != null)
            {
                username = request.Username.Trim();
            }

            string currentPassword = request.CurrentPassword ?? "";
            string password = request.NewPassword ?? "";
            string confirmPassword = request.ConfirmPassword ?? "";

            AuthenticationSettings current = this.GetAuthenticationSettings();
            bool hasUser = this.HasConfiguredUser(current);
            if (hasUser && string.IsNullOrWhiteSpace(username))
            {
                username = current.User.Username ?? "";
            }
            if (!hasUser && !request.Enabled)
            {
                username = "";
                password = "";
            }

            bool usernameChanged = hasUser && !string.IsNullOrWhiteSpace(username) && !string.Equals(username, current.User.Username, StringComparison.OrdinalIgnoreCase);
            bool passwordChanged = !string.IsNullOrWhiteSpace(password);
            bool enabledChanged = hasUser && current.Enabled != request.Enabled;

            if (hasUser && passwordChanged)
            {
                throw new InvalidOperationException("Use change password to update administrator credentials");
            }

            if (request.Enabled && string.IsNullOrWhiteSpace(username))
            {
                throw new InvalidOperationException("Username is required");
            }

            if (!hasUser && !string.IsNullOrWhiteSpace(password) && string.IsNullOrWhiteSpace(username))
            {
                throw new InvalidOperationException("Username is required for a new administrator");
            }

            if (!hasUser && request.Enabled && string.IsNullOrWhiteSpace(password))
            {
                throw new InvalidOperationException("Password is required for a new administrator");
            }

            if (!hasUser && !string.IsNullOrWhiteSpace(username) && string.IsNullOrWhiteSpace(password))
            {
                throw new InvalidOperationException("Password is required for a new administrator");
            }

            if (!hasUser && request.Enabled && !string.Equals(password, confirmPassword, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Password confirmation does not match");
            }

            if (passwordChanged)
            {
                this.ValidatePasswordComplexity(password);
            }

            if (hasUser && current.User.Disabled)
            {
                throw new InvalidOperationException("Configured administrator is disabled. Remove User from appsettings.json to reset authentication.");
            }

            if (hasUser && usernameChanged)
            {
                PasswordVerificationResult currentPasswordResult = this._passwordHasher.VerifyHashedPassword(current.User, current.User.PasswordHash, currentPassword);

                if (currentPasswordResult != PasswordVerificationResult.Success && currentPasswordResult != PasswordVerificationResult.SuccessRehashNeeded)
                {
                    await this.RegisterFailureAsync();
                    throw new InvalidOperationException("Current password is invalid");
                }
            }

            this.UpdateAuthenticationNode(authNode =>
            {
                authNode["Enabled"] = request.Enabled;

                if (!string.IsNullOrWhiteSpace(username))
                {
                    JsonObject userNode = this.GetOrCreateObject(authNode, "User");
                    userNode["Username"] = username;

                    if (!string.IsNullOrWhiteSpace(password))
                    {
                        AuthenticationUserSettings hashUser = new AuthenticationUserSettings();
                        hashUser.Username = username;
                        string passwordHash = this._passwordHasher.HashPassword(hashUser, password);
                        userNode["PasswordHash"] = passwordHash;
                        userNode["SecurityStamp"] = Guid.NewGuid().ToString("N");
                        if (!hasUser)
                        {
                            userNode["Disabled"] = false;
                        }
                    }
                    else if (usernameChanged || enabledChanged)
                    {
                        userNode["SecurityStamp"] = Guid.NewGuid().ToString("N");
                    }
                    else if (userNode["SecurityStamp"] == null)
                    {
                        userNode["SecurityStamp"] = Guid.NewGuid().ToString("N");
                    }
                }
                else if (!hasUser && !request.Enabled)
                {
                    authNode.Remove("User");
                }
            });

            lock (this._lock)
            {
                this._pendingTwoFactorSecret = "";
            }

            this.ResetFailures();
        }

        /// <summary>
        /// Changes the configured administrator password
        /// </summary>
        /// <param name="request">Password change request</param>
        public async System.Threading.Tasks.Task ChangePasswordAsync(ChangePasswordRequest request)
        {
            if (request == null)
            {
                throw new InvalidOperationException("Missing password change request");
            }

            AuthenticationSettings auth = this.GetAuthenticationSettings();
            if (!this.HasConfiguredUser(auth))
            {
                throw new InvalidOperationException("Configure the administrator before changing password");
            }
            if (auth.User.Disabled)
            {
                throw new InvalidOperationException("Configured administrator is disabled. Remove User from appsettings.json to reset authentication.");
            }
            if (!string.Equals(request.NewPassword ?? "", request.ConfirmPassword ?? "", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Password confirmation does not match");
            }

            PasswordVerificationResult currentPasswordResult = this._passwordHasher.VerifyHashedPassword(auth.User, auth.User.PasswordHash, request.CurrentPassword ?? "");
            if (currentPasswordResult != PasswordVerificationResult.Success && currentPasswordResult != PasswordVerificationResult.SuccessRehashNeeded)
            {
                await this.RegisterFailureAsync();
                throw new InvalidOperationException("Current password is invalid");
            }

            this.ValidatePasswordComplexity(request.NewPassword ?? "");

            this.UpdateAuthenticationNode(authNode =>
            {
                JsonObject userNode = this.GetOrCreateObject(authNode, "User");
                AuthenticationUserSettings hashUser = new AuthenticationUserSettings();
                hashUser.Username = auth.User.Username;
                userNode["PasswordHash"] = this._passwordHasher.HashPassword(hashUser, request.NewPassword ?? "");
                userNode["SecurityStamp"] = Guid.NewGuid().ToString("N");
            });

            lock (this._lock)
            {
                this._pendingTwoFactorSecret = "";
            }

            this.ResetFailures();
        }

        /// <summary>
        /// Creates a pending two-factor setup and returns QR code data
        /// </summary>
        /// <returns>Two-factor setup data</returns>
        public async System.Threading.Tasks.Task<TwoFactorSetupResult> CreateTwoFactorSetupAsync(string currentPassword)
        {
            AuthenticationSettings auth = this.GetAuthenticationSettings();
            if (!this.HasConfiguredUser(auth))
            {
                throw new InvalidOperationException("Configure the administrator before enabling 2FA");
            }
            if (auth.User.Disabled)
            {
                throw new InvalidOperationException("Configured administrator is disabled. Remove User from appsettings.json to reset authentication.");
            }
            if (auth.User.TwoFactor != null && auth.User.TwoFactor.Enabled && !string.IsNullOrWhiteSpace(auth.User.TwoFactor.Secret))
            {
                throw new InvalidOperationException("2FA is already enabled");
            }

            PasswordVerificationResult currentPasswordResult = this._passwordHasher.VerifyHashedPassword(auth.User, auth.User.PasswordHash, currentPassword ?? "");
            if (currentPasswordResult != PasswordVerificationResult.Success && currentPasswordResult != PasswordVerificationResult.SuccessRehashNeeded)
            {
                await this.RegisterFailureAsync();
                throw new InvalidOperationException("Current password is invalid");
            }

            byte[] secretBytes = KeyGeneration.GenerateRandomKey(20);
            string secret = Base32Encoding.ToString(secretBytes);
            string issuer = "Bivium";
            string accountName = auth.User.Username;
            string uri = "otpauth://totp/" + UrlEncoder.Default.Encode(issuer + ":" + accountName) + "?secret=" + secret + "&issuer=" + UrlEncoder.Default.Encode(issuer);

            using QRCodeGenerator generator = new QRCodeGenerator();
            using QRCodeData qrCodeData = generator.CreateQrCode(uri, QRCodeGenerator.ECCLevel.Q);
            PngByteQRCode qrCode = new PngByteQRCode(qrCodeData);
            byte[] qrBytes = qrCode.GetGraphic(8);

            lock (this._lock)
            {
                this._pendingTwoFactorSecret = secret;
            }

            TwoFactorSetupResult result = new TwoFactorSetupResult();
            result.QrCodeDataUrl = "data:image/png;base64," + Convert.ToBase64String(qrBytes);
            result.Secret = secret;
            return result;
        }

        /// <summary>
        /// Enables pending two-factor settings after verifying a TOTP code
        /// </summary>
        /// <param name="code">TOTP code</param>
        /// <param name="currentPassword">Current administrator password</param>
        public async System.Threading.Tasks.Task EnableTwoFactorAsync(string code, string currentPassword)
        {
            AuthenticationSettings auth = this.GetAuthenticationSettings();
            if (!this.HasConfiguredUser(auth))
            {
                throw new InvalidOperationException("Configure the administrator before changing 2FA");
            }
            if (auth.User.Disabled)
            {
                throw new InvalidOperationException("Configured administrator is disabled. Remove User from appsettings.json to reset authentication.");
            }

            PasswordVerificationResult currentPasswordResult = this._passwordHasher.VerifyHashedPassword(auth.User, auth.User.PasswordHash, currentPassword ?? "");

            if (currentPasswordResult != PasswordVerificationResult.Success && currentPasswordResult != PasswordVerificationResult.SuccessRehashNeeded)
            {
                await this.RegisterFailureAsync();
                throw new InvalidOperationException("Current password is invalid");
            }

            string secret;
            lock (this._lock)
            {
                secret = this._pendingTwoFactorSecret;
            }

            if (string.IsNullOrWhiteSpace(secret) || !this.ValidateTotp(secret, code))
            {
                await this.RegisterFailureAsync();
                throw new InvalidOperationException("Invalid two-factor code");
            }

            this.UpdateAuthenticationNode(authNode =>
            {
                JsonObject userNode = this.GetOrCreateObject(authNode, "User");
                JsonObject twoFactorNode = this.GetOrCreateObject(userNode, "TwoFactor");
                twoFactorNode["Enabled"] = true;
                twoFactorNode["Secret"] = secret;
                userNode["SecurityStamp"] = Guid.NewGuid().ToString("N");
            });

            lock (this._lock)
            {
                this._pendingTwoFactorSecret = "";
            }

            this.ResetFailures();
        }

        /// <summary>
        /// Disables two-factor authentication
        /// </summary>
        /// <param name="currentPassword">Current administrator password</param>
        public async System.Threading.Tasks.Task DisableTwoFactorAsync(string currentPassword)
        {
            AuthenticationSettings auth = this.GetAuthenticationSettings();
            if (!this.HasConfiguredUser(auth))
            {
                throw new InvalidOperationException("Configure the administrator before changing 2FA");
            }
            if (auth.User.Disabled)
            {
                throw new InvalidOperationException("Configured administrator is disabled. Remove User from appsettings.json to reset authentication.");
            }

            PasswordVerificationResult currentPasswordResult = this._passwordHasher.VerifyHashedPassword(auth.User, auth.User.PasswordHash, currentPassword ?? "");

            if (currentPasswordResult != PasswordVerificationResult.Success && currentPasswordResult != PasswordVerificationResult.SuccessRehashNeeded)
            {
                await this.RegisterFailureAsync();
                throw new InvalidOperationException("Current password is invalid");
            }

            this.UpdateAuthenticationNode(authNode =>
            {
                JsonObject userNode = this.GetOrCreateObject(authNode, "User");
                userNode.Remove("TwoFactor");
                userNode["SecurityStamp"] = Guid.NewGuid().ToString("N");
            });

            lock (this._lock)
            {
                this._pendingTwoFactorSecret = "";
            }

            this.ResetFailures();
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Returns current authentication settings with null-safe defaults
        /// </summary>
        /// <returns>Authentication settings</returns>
        private AuthenticationSettings GetAuthenticationSettings()
        {
            AuthenticationSettings result = this._settingsMonitor.CurrentValue.Authentication ?? new AuthenticationSettings();

            return result;
        }

        /// <summary>
        /// Gets whether a usable admin user is configured
        /// </summary>
        /// <param name="auth">Authentication settings</param>
        /// <returns>True if a user is configured</returns>
        private bool HasConfiguredUser(AuthenticationSettings auth)
        {
            bool result = auth != null && auth.User != null && !string.IsNullOrWhiteSpace(auth.User.Username) && !string.IsNullOrWhiteSpace(auth.User.PasswordHash);
            return result;
        }

        /// <summary>
        /// Validates configured TOTP settings
        /// </summary>
        /// <param name="user">User settings</param>
        /// <param name="code">Submitted code</param>
        /// <returns>True if valid or not required</returns>
        private bool ValidateConfiguredTwoFactor(AuthenticationUserSettings user, string code)
        {
            bool result = true;

            if (user.TwoFactor != null && user.TwoFactor.Enabled && !string.IsNullOrWhiteSpace(user.TwoFactor.Secret))
            {
                result = this.ValidateTotp(user.TwoFactor.Secret, code);
            }

            return result;
        }

        /// <summary>
        /// Validates a TOTP code
        /// </summary>
        /// <param name="secret">Base32 secret</param>
        /// <param name="code">Submitted code</param>
        /// <returns>True if valid</returns>
        private bool ValidateTotp(string secret, string code)
        {
            bool result = false;

            if (!string.IsNullOrWhiteSpace(secret) && !string.IsNullOrWhiteSpace(code))
            {
                try
                {
                    byte[] secretBytes = Base32Encoding.ToBytes(secret);
                    Totp totp = new Totp(secretBytes);
                    result = totp.VerifyTotp(code.Trim(), out long _, VerificationWindow.RfcSpecifiedNetworkDelay);
                }
                catch
                {
                    result = false;
                }
            }

            return result;
        }

        /// <summary>
        /// Enforces the local administrator password complexity policy
        /// </summary>
        /// <param name="password">Password to validate</param>
        private void ValidatePasswordComplexity(string password)
        {
            if (string.IsNullOrWhiteSpace(password) || password.Length < MIN_PASSWORD_LENGTH)
            {
                throw new InvalidOperationException("Password must be at least 12 characters long");
            }

            bool hasLower = false;
            bool hasUpper = false;
            bool hasDigit = false;
            bool hasSymbol = false;

            for (int i = 0; i < password.Length; i++)
            {
                char c = password[i];
                if (char.IsLower(c))
                {
                    hasLower = true;
                }
                else if (char.IsUpper(c))
                {
                    hasUpper = true;
                }
                else if (char.IsDigit(c))
                {
                    hasDigit = true;
                }
                else
                {
                    hasSymbol = true;
                }
            }

            int classCount = 0;
            if (hasLower)
            {
                classCount++;
            }
            if (hasUpper)
            {
                classCount++;
            }
            if (hasDigit)
            {
                classCount++;
            }
            if (hasSymbol)
            {
                classCount++;
            }

            if (classCount < 2)
            {
                throw new InvalidOperationException("Password must include at least two character types");
            }
        }

        /// <summary>
        /// Registers a failed login attempt and applies delay/disable policy
        /// </summary>
        private async System.Threading.Tasks.Task RegisterFailureAsync()
        {
            int attempts;
            bool shouldDisable = false;

            lock (this._lock)
            {
                if (!this._permanentlyLockedInMemory)
                {
                    this._failedAttempts++;
                    if (this._failedAttempts >= PERMANENT_LOCK_ATTEMPTS)
                    {
                        shouldDisable = true;
                    }
                }

                attempts = this._failedAttempts;
            }

            if (shouldDisable)
            {
                this.DisableConfiguredUser();
                lock (this._lock)
                {
                    this._permanentlyLockedInMemory = true;
                }
            }

            int delaySeconds = this.GetFailureDelaySeconds(attempts);
            if (delaySeconds > 0)
            {
                await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(delaySeconds));
            }
        }

        /// <summary>
        /// Resets in-memory failed login state
        /// </summary>
        private void ResetFailures()
        {
            lock (this._lock)
            {
                this._failedAttempts = 0;
                this._permanentlyLockedInMemory = false;
            }
        }

        /// <summary>
        /// Gets the delay for a failed attempt count
        /// </summary>
        /// <param name="attempts">Failed attempt count</param>
        /// <returns>Delay in seconds</returns>
        private int GetFailureDelaySeconds(int attempts)
        {
            int result;

            if (attempts <= 0)
            {
                result = 0;
            }
            else if (attempts == 1)
            {
                result = 1;
            }
            else if (attempts == 2)
            {
                result = 2;
            }
            else if (attempts == 3)
            {
                result = 5;
            }
            else if (attempts == 4)
            {
                result = 15;
            }
            else if (attempts == 5)
            {
                result = 60;
            }
            else
            {
                result = 300;
            }

            return result;
        }

        /// <summary>
        /// Persists Disabled=true for the configured user
        /// </summary>
        private void DisableConfiguredUser()
        {
            AuthenticationSettings auth = this.GetAuthenticationSettings();
            if (this.HasConfiguredUser(auth) && !auth.User.Disabled)
            {
                this.UpdateAuthenticationNode(authNode =>
                {
                    JsonObject userNode = this.GetOrCreateObject(authNode, "User");
                    userNode["Disabled"] = true;
                    userNode["SecurityStamp"] = Guid.NewGuid().ToString("N");
                });
                lock (this._lock)
                {
                    this._pendingTwoFactorSecret = "";
                }
            }
        }

        /// <summary>
        /// Creates an authenticated principal for the local admin
        /// </summary>
        /// <param name="user">User settings</param>
        /// <returns>Claims principal</returns>
        private ClaimsPrincipal CreatePrincipal(AuthenticationUserSettings user)
        {
            List<Claim> claims = new List<Claim>();
            claims.Add(new Claim(ClaimTypes.Name, user.Username));
            claims.Add(new Claim(ClaimTypes.Role, "Administrator"));
            claims.Add(new Claim(SECURITY_STAMP_CLAIM, user.SecurityStamp ?? ""));

            ClaimsIdentity identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            ClaimsPrincipal result = new ClaimsPrincipal(identity);
            return result;
        }

        /// <summary>
        /// Gets whether a principal has the current security stamp
        /// </summary>
        /// <param name="principal">Principal to check</param>
        /// <param name="user">Current user settings</param>
        /// <returns>True if the stamp matches</returns>
        private bool PrincipalHasCurrentSecurityStamp(ClaimsPrincipal principal, AuthenticationUserSettings user)
        {
            string currentStamp = user.SecurityStamp ?? "";
            string principalStamp = principal.FindFirstValue(SECURITY_STAMP_CLAIM) ?? "";

            bool result;
            result = string.IsNullOrWhiteSpace(currentStamp) || string.Equals(principalStamp, currentStamp, StringComparison.Ordinal);

            return result;
        }

        /// <summary>
        /// Updates the Authentication node in appsettings.json
        /// </summary>
        /// <param name="update">Update action</param>
        private void UpdateAuthenticationNode(Action<JsonObject> update)
        {
            lock (this._lock)
            {
                string settingsPath = Path.Combine(this._environment.ContentRootPath, "appsettings.json");
                string json = File.ReadAllText(settingsPath);

                JsonNode rootNode = JsonNode.Parse(json, new JsonNodeOptions(), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
                if (rootNode == null || rootNode.AsObject() == null)
                {
                    throw new InvalidOperationException("Invalid appsettings.json");
                }

                JsonObject root = rootNode.AsObject();
                JsonObject commanderSettings = this.GetOrCreateObject(root, "CommanderSettings");
                JsonObject authNode = this.GetOrCreateObject(commanderSettings, "Authentication");

                update(authNode);

                JsonSerializerOptions writeOptions = new JsonSerializerOptions();
                writeOptions.WriteIndented = true;
                string updatedJson = root.ToJsonString(writeOptions);
                File.WriteAllText(settingsPath, updatedJson);

                IConfigurationRoot configurationRoot = this._configuration as IConfigurationRoot;
                if (configurationRoot != null)
                {
                    configurationRoot.Reload();
                }
            }
        }

        /// <summary>
        /// Gets or creates a JSON object property
        /// </summary>
        /// <param name="parent">Parent JSON object</param>
        /// <param name="propertyName">Property name</param>
        /// <returns>JSON object</returns>
        private JsonObject GetOrCreateObject(JsonObject parent, string propertyName)
        {
            JsonObject result;

            if (parent[propertyName] is not JsonObject)
            {
                result = new JsonObject();
                parent[propertyName] = result;
            }
            else
            {
                result = parent[propertyName].AsObject();
            }

            return result;
        }

        #endregion
    }

    /// <summary>
    /// Result of a local login validation
    /// </summary>
    public class LoginValidationResult
    {
        #region Properties

        public bool Success { get; set; } = false;

        public string ErrorMessage { get; set; } = "";

        public ClaimsPrincipal Principal { get; set; }

        #endregion
    }
}
