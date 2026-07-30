using Bivium.Models;
using Bivium.Services;
using Microsoft.AspNetCore.Mvc;
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Bivium.Controllers
{
    /// <summary>
    /// Streams retained terminal history as plain UTF-8 text
    /// </summary>
    [ApiController]
    [Route("api/terminal")]
    public sealed class TerminalHistoryController : ControllerBase
    {
        #region Class Variables

        /// <summary>
        /// Global terminal runtime
        /// </summary>
        private readonly TerminalRuntimeService _terminalRuntimeService;

        /// <summary>
        /// Workspace that validates the controlling browser
        /// </summary>
        private readonly BiviumWorkspaceService _workspaceService;

        #endregion

        #region Constructor

        /// <summary>
        /// Creates the terminal history controller
        /// </summary>
        /// <param name="terminalRuntimeService">Global terminal runtime</param>
        /// <param name="workspaceService">Global workspace</param>
        public TerminalHistoryController(TerminalRuntimeService terminalRuntimeService, BiviumWorkspaceService workspaceService)
        {
            this._terminalRuntimeService = terminalRuntimeService;
            this._workspaceService = workspaceService;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Downloads all retained history and the current screen for one terminal tab
        /// </summary>
        /// <param name="sessionId">Terminal session identifier</param>
        /// <param name="attachmentId">Current browser attachment</param>
        /// <param name="generation">Current workspace lease generation</param>
        /// <returns>Streaming plain-text download</returns>
        [HttpGet("history")]
        public IActionResult DownloadHistory([FromQuery] int sessionId, [FromQuery] string attachmentId, [FromQuery] long generation)
        {
            WorkspaceClientToken clientToken = new WorkspaceClientToken(attachmentId, generation);
            CancellationToken leaseCancellation = this._workspaceService.GetRevocationToken(clientToken);
            if (leaseCancellation.IsCancellationRequested)
                return this.Unauthorized();

            TerminalHistoryExportSnapshot snapshot = this._terminalRuntimeService.GetHistoryExportSnapshot(sessionId, leaseCancellation);
            if (snapshot == null)
                return this.NotFound("Terminal session not found");

            string fileName = "bivium-terminal-" + snapshot.SessionId + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".txt";
            return new TerminalHistoryStreamResult(snapshot, fileName, leaseCancellation);
        }

        #endregion
    }

    /// <summary>
    /// Streams a terminal export without materializing one large string
    /// </summary>
    internal sealed class TerminalHistoryStreamResult : IActionResult
    {
        #region Constants

        /// <summary>
        /// Marker emitted when older history has already been discarded
        /// </summary>
        private const string TRUNCATION_MARKER = "[Earlier terminal history was truncated]";

        #endregion

        #region Class Variables

        /// <summary>
        /// Point-in-time terminal export source
        /// </summary>
        private readonly TerminalHistoryExportSnapshot _snapshot;

        /// <summary>
        /// Safe output file name
        /// </summary>
        private readonly string _fileName;

        /// <summary>
        /// Token cancelled when the controlling browser loses its lease
        /// </summary>
        private readonly CancellationToken _leaseCancellation;

        #endregion

        #region Constructor

        /// <summary>
        /// Creates a streaming terminal history result
        /// </summary>
        /// <param name="snapshot">Point-in-time export source</param>
        /// <param name="fileName">Download file name</param>
        /// <param name="leaseCancellation">Workspace lease cancellation</param>
        public TerminalHistoryStreamResult(TerminalHistoryExportSnapshot snapshot, string fileName, CancellationToken leaseCancellation)
        {
            this._snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            this._fileName = fileName ?? throw new ArgumentNullException(nameof(fileName));
            this._leaseCancellation = leaseCancellation;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Writes plain terminal text directly to the response body
        /// </summary>
        /// <param name="context">Current action context</param>
        /// <returns>Asynchronous write operation</returns>
        public async Task ExecuteResultAsync(ActionContext context)
        {
            HttpResponse response = context.HttpContext.Response;
            response.ContentType = "text/plain; charset=utf-8";
            response.Headers.CacheControl = "no-store";
            response.Headers.Append("Content-Disposition", "attachment; filename=\"" + this._fileName + "\"");

            using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.HttpContext.RequestAborted, this._leaseCancellation);
            await using StreamWriter writer = new StreamWriter(response.Body, new UTF8Encoding(false), 16 * 1024, true);
            if (this._snapshot.Truncated)
                await writer.WriteLineAsync(TRUNCATION_MARKER.AsMemory(), cancellation.Token);

            bool hasPreviousRow = false;
            hasPreviousRow = await this.WriteLinesAsync(writer, this._snapshot.HistoryLines, this._snapshot.HistoryLines.Count, hasPreviousRow, cancellation.Token);
            int screenLineCount = this.GetScreenLineCount();
            await this.WriteLinesAsync(writer, this._snapshot.Screen.Lines, screenLineCount, hasPreviousRow, cancellation.Token);
            await writer.FlushAsync(cancellation.Token);
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Writes rows while joining soft-wrapped continuations
        /// </summary>
        /// <param name="writer">Response writer</param>
        /// <param name="lines">Rows to write</param>
        /// <param name="count">Number of rows to include</param>
        /// <param name="hasPreviousRow">Whether an earlier row was written</param>
        /// <param name="cancellationToken">Combined request and lease cancellation</param>
        /// <returns>True when at least one row was written</returns>
        private async Task<bool> WriteLinesAsync(StreamWriter writer, IReadOnlyList<TerminalLineSnapshot> lines, int count, bool hasPreviousRow, CancellationToken cancellationToken)
        {
            for (int i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TerminalLineSnapshot line = lines[i];
                if (hasPreviousRow && !line.Wrapped)
                    await writer.WriteAsync(Environment.NewLine.AsMemory(), cancellationToken);
                await writer.WriteAsync((line.Text ?? "").AsMemory(), cancellationToken);
                hasPreviousRow = true;
            }

            return hasPreviousRow;
        }

        /// <summary>
        /// Retains meaningful screen rows and the current cursor row while removing unused trailing space
        /// </summary>
        /// <returns>Number of screen rows to export</returns>
        private int GetScreenLineCount()
        {
            int result = Math.Clamp(this._snapshot.Screen.CursorY + 1, 0, this._snapshot.Screen.Lines.Count);
            for (int i = this._snapshot.Screen.Lines.Count - 1; i >= result; i--)
            {
                TerminalLineSnapshot line = this._snapshot.Screen.Lines[i];
                if (!string.IsNullOrEmpty(line.Text) || line.Wrapped)
                {
                    result = i + 1;
                    break;
                }
            }

            return result;
        }

        #endregion
    }
}
