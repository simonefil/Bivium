using Bivium.Models;
using Bivium.Services;
using Microsoft.AspNetCore.Mvc;

namespace Bivium.Controllers
{
    /// <summary>
    /// Riceve le notifiche del ciclo di vita del browser per il workspace condiviso
    /// </summary>
    [ApiController]
    [Route("api/workspace")]
    public sealed class WorkspaceController : ControllerBase
    {
        /// <summary>
        /// Servizio autoritativo del workspace condiviso
        /// </summary>
        private readonly BiviumWorkspaceService _workspaceService;

        /// <summary>
        /// Crea il controller protetto dal filtro di autorizzazione applicativo
        /// </summary>
        /// <param name="workspaceService">Servizio autoritativo del workspace</param>
        public WorkspaceController(BiviumWorkspaceService workspaceService)
        {
            this._workspaceService = workspaceService;
        }

        /// <summary>
        /// Marca il proprietario corrente come disconnesso senza scollegarne le risorse persistenti
        /// </summary>
        /// <returns>Nessun contenuto per ogni token ben formato</returns>
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
