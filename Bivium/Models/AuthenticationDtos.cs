namespace Bivium.Models
{
    /// <summary>
    /// Authentication status returned to the UI
    /// </summary>
    public class AuthenticationStatus
    {
        #region Properties

        public bool Enabled { get; set; } = false;

        public bool HasUser { get; set; } = false;

        public bool Required { get; set; } = false;

        public bool Authenticated { get; set; } = false;

        public string Username { get; set; } = "";

        public bool Disabled { get; set; } = false;

        public bool TwoFactorEnabled { get; set; } = false;

        public bool CanManageSettings { get; set; } = false;

        #endregion
    }

    /// <summary>
    /// Login request submitted by the UI
    /// </summary>
    public class LoginRequest
    {
        #region Properties

        public string Username { get; set; } = "";

        public string Password { get; set; } = "";

        public string TwoFactorCode { get; set; } = "";

        #endregion
    }

    /// <summary>
    /// Authentication settings update request
    /// </summary>
    public class AuthenticationSettingsRequest
    {
        #region Properties

        public bool Enabled { get; set; } = false;

        public string Username { get; set; } = "";

        public string CurrentPassword { get; set; } = "";

        public string NewPassword { get; set; } = "";

        public string ConfirmPassword { get; set; } = "";

        #endregion
    }

    /// <summary>
    /// Two-factor setup response
    /// </summary>
    public class TwoFactorSetupResult
    {
        #region Properties

        public string QrCodeDataUrl { get; set; } = "";

        public string Secret { get; set; } = "";

        #endregion
    }

    /// <summary>
    /// Two-factor verification request
    /// </summary>
    public class TwoFactorVerifyRequest
    {
        #region Properties

        public string CurrentPassword { get; set; } = "";

        public string Code { get; set; } = "";

        #endregion
    }

    /// <summary>
    /// Password change request
    /// </summary>
    public class ChangePasswordRequest
    {
        #region Properties

        public string CurrentPassword { get; set; } = "";

        public string NewPassword { get; set; } = "";

        public string ConfirmPassword { get; set; } = "";

        #endregion
    }
}
