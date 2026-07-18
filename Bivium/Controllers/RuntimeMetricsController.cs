using Bivium.Models;
using Bivium.Services;
using Microsoft.AspNetCore.Mvc;

namespace Bivium.Controllers
{
    /// <summary>
    /// Exposes only aggregate metrics from singleton runtimes
    /// </summary>
    [ApiController]
    [Route("api/runtime/metrics")]
    public sealed class RuntimeMetricsController : ControllerBase
    {
        private readonly BiviumWorkspaceService _workspaceService;

        private readonly TerminalRuntimeService _terminalRuntimeService;

        /// <summary>
        /// Creates the metrics controller protected by the application authorization filter
        /// </summary>
        /// <param name="workspaceService">Global workspace runtime</param>
        /// <param name="terminalRuntimeService">Global terminal runtime</param>
        public RuntimeMetricsController(BiviumWorkspaceService workspaceService, TerminalRuntimeService terminalRuntimeService)
        {
            this._workspaceService = workspaceService;
            this._terminalRuntimeService = terminalRuntimeService;
        }

        /// <summary>
        /// Returns aggregate counters without paths, terminal content or client identifiers
        /// </summary>
        /// <returns>Current aggregate metrics</returns>
        [HttpGet]
        public ActionResult<RuntimeMetricsSnapshot> GetMetrics()
        {
            this.Response.Headers.CacheControl = "no-store";
            RuntimeMetricsSnapshot result = new RuntimeMetricsSnapshot();
            result.Workspace = this._workspaceService.GetMetrics();
            result.Terminal = this._terminalRuntimeService.GetMetrics();
            return this.Ok(result);
        }
    }

    /// <summary>
    /// Aggregate response from the metrics endpoint
    /// </summary>
    public sealed class RuntimeMetricsSnapshot
    {
        /// <summary>
        /// Workspace counters
        /// </summary>
        public WorkspaceRuntimeMetrics Workspace { get; set; } = new WorkspaceRuntimeMetrics();

        /// <summary>
        /// Terminal runtime counters
        /// </summary>
        public TerminalRuntimeMetrics Terminal { get; set; } = new TerminalRuntimeMetrics();
    }
}
