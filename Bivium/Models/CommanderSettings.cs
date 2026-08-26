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

        /// <summary>
        /// Optional ownership and permission defaults for newly created entries
        /// </summary>
        public DefaultCreationPermissionsSettings DefaultCreationPermissions { get; set; } = new DefaultCreationPermissionsSettings();

        /// <summary>
        /// Persistent terminal runtime settings
        /// </summary>
        public TerminalRuntimeSettings TerminalRuntime { get; set; } = new TerminalRuntimeSettings();

        #endregion
    }

    /// <summary>
    /// Ownership and permission defaults for entries created by Bivium
    /// </summary>
    public class DefaultCreationPermissionsSettings
    {
        #region Properties

        /// <summary>
        /// Whether Bivium overrides the operating system creation defaults
        /// </summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// Optional owner assigned to created entries
        /// </summary>
        public string Owner { get; set; } = "";

        /// <summary>
        /// Optional Unix group assigned to created entries
        /// </summary>
        public string Group { get; set; } = "";

        /// <summary>
        /// Permissions assigned to created files
        /// </summary>
        public CreationPermissionSettings FilePermissions { get; set; } = new CreationPermissionSettings
        {
            OwnerRead = true,
            OwnerWrite = true,
            GroupRead = true,
            OthersRead = true,
            WinArchive = true
        };

        /// <summary>
        /// Permissions assigned to created directories
        /// </summary>
        public CreationPermissionSettings DirectoryPermissions { get; set; } = new CreationPermissionSettings
        {
            OwnerRead = true,
            OwnerWrite = true,
            OwnerExecute = true,
            GroupRead = true,
            GroupExecute = true,
            OthersRead = true,
            OthersExecute = true
        };

        #endregion
    }

    /// <summary>
    /// Cross-platform permission values for one created entry type
    /// </summary>
    public class CreationPermissionSettings
    {
        #region Properties - Unix

        /// <summary>
        /// Owner read permission
        /// </summary>
        public bool OwnerRead { get; set; } = false;

        /// <summary>
        /// Owner write permission
        /// </summary>
        public bool OwnerWrite { get; set; } = false;

        /// <summary>
        /// Owner execute permission
        /// </summary>
        public bool OwnerExecute { get; set; } = false;

        /// <summary>
        /// Group read permission
        /// </summary>
        public bool GroupRead { get; set; } = false;

        /// <summary>
        /// Group write permission
        /// </summary>
        public bool GroupWrite { get; set; } = false;

        /// <summary>
        /// Group execute permission
        /// </summary>
        public bool GroupExecute { get; set; } = false;

        /// <summary>
        /// Others read permission
        /// </summary>
        public bool OthersRead { get; set; } = false;

        /// <summary>
        /// Others write permission
        /// </summary>
        public bool OthersWrite { get; set; } = false;

        /// <summary>
        /// Others execute permission
        /// </summary>
        public bool OthersExecute { get; set; } = false;

        #endregion

        #region Properties - Windows

        /// <summary>
        /// Read-only attribute
        /// </summary>
        public bool WinReadOnly { get; set; } = false;

        /// <summary>
        /// Hidden attribute
        /// </summary>
        public bool WinHidden { get; set; } = false;

        /// <summary>
        /// System attribute
        /// </summary>
        public bool WinSystem { get; set; } = false;

        /// <summary>
        /// Archive attribute
        /// </summary>
        public bool WinArchive { get; set; } = false;

        #endregion
    }

    /// <summary>
    /// Persistent terminal runtime limits
    /// </summary>
    public class TerminalRuntimeSettings
    {
        #region Properties

        /// <summary>
        /// Maximum serialized history bytes retained by one tab
        /// </summary>
        public long MaxHistoryBytesPerTab { get; set; } = 100L * 1024 * 1024;

        /// <summary>
        /// Maximum serialized history bytes retained by all tabs
        /// </summary>
        public long MaxHistoryBytesGlobal { get; set; } = 512L * 1024 * 1024;

        /// <summary>
        /// Maximum number of terminal tabs
        /// </summary>
        public int MaxTabs { get; set; } = 16;

        /// <summary>
        /// Target serialized size of one history segment
        /// </summary>
        public int HistorySegmentBytes { get; set; } = 256 * 1024;

        /// <summary>
        /// Default number of rows returned by one history page
        /// </summary>
        public int HistoryPageRows { get; set; } = 200;

        /// <summary>
        /// Number of headless scrollback lines retained before recycling
        /// </summary>
        public int HeadlessScrollbackRows { get; set; } = 4096;

        /// <summary>
        /// Hours for which exited tabs remain available
        /// </summary>
        public int ExitedSessionRetentionHours { get; set; } = 24;

        /// <summary>
        /// Seconds without heartbeat after which a client lease expires
        /// </summary>
        public int ClientLeaseTimeoutSeconds { get; set; } = 90;

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
