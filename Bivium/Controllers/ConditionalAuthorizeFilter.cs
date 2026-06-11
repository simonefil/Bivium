using Bivium.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Bivium.Controllers
{
    /// <summary>
    /// Protects API controllers only when local authentication is enabled and configured
    /// </summary>
    public class ConditionalAuthorizeFilter : IAuthorizationFilter
    {
        #region Class Variables

        private readonly AuthenticationService _authenticationService;

        #endregion

        #region Constructor

        /// <summary>
        /// Creates a new conditional authorization filter
        /// </summary>
        /// <param name="authenticationService">Authentication service</param>
        public ConditionalAuthorizeFilter(AuthenticationService authenticationService)
        {
            this._authenticationService = authenticationService;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Applies authorization when authentication is required
        /// </summary>
        /// <param name="context">Authorization context</param>
        public void OnAuthorization(AuthorizationFilterContext context)
        {
            if (this.HasAllowAnonymous(context))
            {
                return;
            }

            if (this._authenticationService.IsAuthenticationRequired() && !this._authenticationService.CanAccess(context.HttpContext.User))
            {
                context.Result = new UnauthorizedResult();
            }
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Gets whether the endpoint allows anonymous access
        /// </summary>
        /// <param name="context">Authorization context</param>
        /// <returns>True if anonymous access is allowed</returns>
        private bool HasAllowAnonymous(AuthorizationFilterContext context)
        {
            bool result = false;

            ControllerActionDescriptor descriptor = context.ActionDescriptor as ControllerActionDescriptor;
            if (descriptor != null)
            {
                result = descriptor.ControllerTypeInfo.GetCustomAttributes(typeof(AllowAnonymousAttribute), true).Length > 0 || descriptor.MethodInfo.GetCustomAttributes(typeof(AllowAnonymousAttribute), true).Length > 0;
            }

            return result;
        }

        #endregion
    }
}
