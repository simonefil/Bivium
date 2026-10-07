using Bivium.Models;
using Bivium.Services;
using Microsoft.AspNetCore.Mvc;

namespace Bivium.Controllers
{
    /// <summary>
    /// Receives browser lifecycle notifications for the shared workspace
    /// </summary>
    [ApiController]
    [Route("api/workspace")]
    public sealed class WorkspaceController : ControllerBase
    {
        /// <summary>
        /// Authoritative service of the shared workspace
        /// </summary>
        private readonly BiviumWorkspaceService _workspaceService;

        /// <summary>
        /// Creates the controller protected by the application authorization filter
        /// </summary>
        /// <param name="workspaceService">Authoritative workspace service</param>
        public WorkspaceController(BiviumWorkspaceService workspaceService)
        {
            this._workspaceService = workspaceService;
        }

        /// <summary>
        /// Marks the current owner as disconnected without detaching its persistent resources
        /// </summary>
        /// <returns>No content for every well-formed token</returns>
        [HttpPost("disconnect")]
        public IActionResult Disconnect()
        {
            string attachmentId = this.Request.Headers["X-Bivium-Attachment"].ToString();
            string generationText = this.Request.Headers["X-Bivium-Lease-Generation"].ToString();
            long generation;
            if (string.IsNullOrEmpty(attachmentId) || !long.TryParse(generationText, out generation))
                return this.BadRequest("Workspace attachment headers are required");

            this._workspaceService.MarkClientDisconnected(new WorkspaceClientToken(attachmentId, generation));
            return this.NoContent();
        }
    }
}
