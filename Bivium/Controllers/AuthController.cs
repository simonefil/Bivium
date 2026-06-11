using Bivium.Models;
using Bivium.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using LocalAuthenticationService = Bivium.Services.AuthenticationService;

namespace Bivium.Controllers
{
    /// <summary>
    /// API controller for local administrator login and logout
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class AuthController : ControllerBase
    {
        #region Class Variables

        private readonly LocalAuthenticationService _authenticationService;

        #endregion

        #region Constructor

        /// <summary>
        /// Creates a new auth controller
        /// </summary>
        /// <param name="authenticationService">Authentication service</param>
        public AuthController(LocalAuthenticationService authenticationService)
        {
            this._authenticationService = authenticationService;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Gets current authentication status
        /// </summary>
        /// <returns>Authentication status</returns>
        [AllowAnonymous]
        [HttpGet("status")]
        public IActionResult GetStatus()
        {
            AuthenticationStatus status = this._authenticationService.GetStatus(this.User);
            if (status.Required && !status.Authenticated)
            {
                status.Username = "";
                status.Disabled = false;
                status.TwoFactorEnabled = false;
                status.CanManageSettings = false;
            }

            IActionResult result = this.Ok(status);
            return result;
        }

        /// <summary>
        /// Attempts to sign in the local administrator
        /// </summary>
        /// <param name="request">Login request</param>
        /// <returns>Login result</returns>
        [AllowAnonymous]
        [HttpPost("login")]
        public async System.Threading.Tasks.Task<IActionResult> Login([FromBody] LoginRequest request)
        {
            IActionResult result;

            LoginValidationResult validation = await this._authenticationService.ValidateLoginAsync(request);
            if (validation.Success)
            {
                AuthenticationProperties properties = new AuthenticationProperties();
                properties.IsPersistent = true;
                properties.ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8);

                await this.HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, validation.Principal, properties);
                result = this.Ok(new { success = true });
            }
            else
            {
                result = this.Unauthorized(new { success = false, error = validation.ErrorMessage });
            }

            return result;
        }

        /// <summary>
        /// Signs out the current administrator
        /// </summary>
        /// <returns>Logout result</returns>
        [HttpPost("logout")]
        public async System.Threading.Tasks.Task<IActionResult> Logout()
        {
            await this.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            IActionResult result = this.Ok(new { success = true });
            return result;
        }

        #endregion
    }
}
