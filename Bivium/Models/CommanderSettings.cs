namespace Bivium.Models
{
    /// <summary>
    /// Application configuration settings
    /// </summary>
    public class CommanderSettings
    {
        #region Properties

        /// <summary>
        /// Local administrator authentication settings
        /// </summary>
        public AuthenticationSettings Authentication { get; set; } = new AuthenticationSettings();

        /// <summary>
        /// Default WebTUI theme name
        /// </summary>
        public string DefaultTheme { get; set; } = "dark";

        /// <summary>
        /// File extensions that can be opened in the editor
        /// </summary>
        public List<string> EditableExtensions { get; set; } = new List<string>();

        #endregion
    }

    /// <summary>
    /// Optional local administrator authentication configuration
    /// </summary>
    public class AuthenticationSettings
    {
        #region Properties

        /// <summary>
        /// Whether authentication is enabled when an admin user is configured
        /// </summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// Single local administrator user
        /// </summary>
        public AuthenticationUserSettings User { get; set; }

        #endregion
    }

    /// <summary>
    /// Local administrator user configuration
    /// </summary>
    public class AuthenticationUserSettings
    {
        #region Properties

        /// <summary>
        /// Administrator username
        /// </summary>
        public string Username { get; set; } = "";

        /// <summary>
        /// Hashed administrator password
        /// </summary>
        public string PasswordHash { get; set; } = "";

        /// <summary>
        /// Whether the administrator account is disabled
        /// </summary>
        public bool Disabled { get; set; } = false;

        /// <summary>
        /// Security stamp used to invalidate existing sessions after credential changes
        /// </summary>
        public string SecurityStamp { get; set; } = "";

        /// <summary>
        /// Optional TOTP two-factor settings
        /// </summary>
        public TwoFactorSettings TwoFactor { get; set; }

        #endregion
    }

    /// <summary>
    /// TOTP two-factor authentication settings
    /// </summary>
    public class TwoFactorSettings
    {
        #region Properties

        /// <summary>
        /// Whether TOTP two-factor authentication is enabled
        /// </summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// Base32-encoded TOTP secret
        /// </summary>
        public string Secret { get; set; } = "";

        #endregion
    }
}
