using System.IO.Compression;
using Microsoft.AspNetCore.Mvc;
using Bivium.Models;
using Bivium.Services;

namespace Bivium.Controllers
{
    /// <summary>
    /// API controller for file download, ZIP streaming, and chunked upload
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class FileTransferController : ControllerBase
    {
        #region Constants

        /// <summary>
        /// Maximum chunk size for uploads (50 MB)
        /// </summary>
        private const long MAX_CHUNK_SIZE = 50L * 1024 * 1024;

        #endregion

        #region Class Variables

        /// <summary>
        /// Security service for path validation
        /// </summary>
        private readonly SecurityService _securityService;

        /// <summary>
        /// Workspace that validates the mutation lease
        /// </summary>
        private readonly BiviumWorkspaceService _workspaceService;

        /// <summary>
        /// Permission service for configured upload defaults
        /// </summary>
        private readonly IPermissionService _permissionService;

        #endregion

        #region Constructor

        /// <summary>
        /// Creates a new FileTransferController
        /// </summary>
        /// <param name="securityService">Security service instance</param>
        /// <param name="workspaceService">Global workspace</param>
        /// <param name="permissionService">Permission service instance</param>
        public FileTransferController(SecurityService securityService, BiviumWorkspaceService workspaceService, IPermissionService permissionService)
        {
            this._securityService = securityService;
            this._workspaceService = workspaceService;
            this._permissionService = permissionService;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Downloads a single file with Range header support for resume
        /// </summary>
        /// <param name="path">Full file path (query parameter)</param>
        /// <returns>File content with resume support</returns>
        [HttpGet("download")]
        public IActionResult Download([FromQuery] string path)
        {
            IActionResult result;

            if (string.IsNullOrWhiteSpace(path) || !this._securityService.IsPathSafe(path))
            {
                result = this.BadRequest("Invalid path");
            }
            else if (!System.IO.File.Exists(path))
            {
                result = this.NotFound("File not found");
            }
            else
            {
                // PhysicalFile supports Range headers natively
                string fileName = Path.GetFileName(path);
                string contentType = "application/octet-stream";
                result = this.PhysicalFile(path, contentType, fileName, true);
            }

            return result;
        }

        /// <summary>
        /// Downloads a directory as a ZIP stream (store mode, no compression)
        /// </summary>
        /// <param name="path">Full directory path (query parameter)</param>
        /// <returns>ZIP stream</returns>
        [HttpGet("download-zip")]
        public IActionResult DownloadZip([FromQuery] string path)
        {
            IActionResult result;

            if (string.IsNullOrWhiteSpace(path) || !this._securityService.IsPathSafe(path))
            {
                result = this.BadRequest("Invalid path");
            }
            else if (!Directory.Exists(path))
            {
                result = this.NotFound("Directory not found");
            }
            else
            {
                string dirName = new DirectoryInfo(path).Name;
                string zipFileName = dirName + ".zip";

                // Stream ZIP directly to response without temp file
                result = new ZipStreamResult(path, zipFileName);
            }

            return result;
        }

        /// <summary>
        /// Downloads multiple files and directories as a ZIP stream
        /// </summary>
        /// <param name="path">Full file or directory paths (repeated query parameter)</param>
        /// <returns>ZIP stream</returns>
        [HttpGet("download-zip-multi")]
        public IActionResult DownloadZipMulti([FromQuery] List<string> path)
        {
            IActionResult result;

            if (path == null || path.Count == 0)
            {
                result = this.BadRequest("No paths selected");
            }
            else
            {
                List<string> safePaths = new List<string>();
                string error = "";

                for (int i = 0; i < path.Count; i++)
                {
                    string currentPath = path[i];
                    if (string.IsNullOrWhiteSpace(currentPath) || !this._securityService.IsPathSafe(currentPath))
                    {
                        error = "Invalid path: " + currentPath;
                        break;
                    }
                    if (!System.IO.File.Exists(currentPath) && !Directory.Exists(currentPath))
                    {
                        error = "Path not found: " + currentPath;
                        break;
                    }

                    safePaths.Add(currentPath);
                }

                if (!string.IsNullOrEmpty(error))
                {
                    result = this.BadRequest(error);
                }
                else
                {
                    result = new MultiZipStreamResult(safePaths, "bivium-selection.zip");
                }
            }

            return result;
        }

        /// <summary>
        /// Receives a chunked file upload
        /// </summary>
        /// <returns>Upload result</returns>
        [HttpPost("upload")]
        [RequestSizeLimit(MAX_CHUNK_SIZE + 4096)]
        public async System.Threading.Tasks.Task<IActionResult> UploadAsync()
        {
            IActionResult result;
            string tempPath = "";

            WorkspaceClientToken workspaceToken;
            if (!this.TryGetWorkspaceToken(out workspaceToken))
                return this.StatusCode(409, "Workspace lease revoked");
            CancellationToken revocationToken = this._workspaceService.GetRevocationToken(workspaceToken);
            using CancellationTokenSource uploadCancellation = CancellationTokenSource.CreateLinkedTokenSource(this.HttpContext.RequestAborted, revocationToken);
            CancellationToken cancellationToken = uploadCancellation.Token;

            string destinationDir = Uri.UnescapeDataString(this.Request.Headers["X-Destination-Dir"].ToString());
            string fileName = Uri.UnescapeDataString(this.Request.Headers["X-File-Name"].ToString());
            string relativePath = Uri.UnescapeDataString(this.Request.Headers["X-Relative-Path"].ToString());
            string chunkIndexStr = this.Request.Headers["X-Chunk-Index"].ToString();
            string totalChunksStr = this.Request.Headers["X-Total-Chunks"].ToString();
            string uploadIdStr = this.Request.Headers["X-Upload-Id"].ToString();

            if (string.IsNullOrWhiteSpace(destinationDir) || !this._securityService.IsPathSafe(destinationDir))
            {
                result = this.BadRequest("Invalid destination directory");
            }
            else if (string.IsNullOrWhiteSpace(fileName))
            {
                result = this.BadRequest("Missing file name");
            }
            else if (fileName != Path.GetFileName(fileName) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                result = this.BadRequest("Invalid file name");
            }
            else if (!Directory.Exists(destinationDir))
            {
                result = this.BadRequest("Destination directory not found");
            }
            else if (!Guid.TryParse(uploadIdStr, out Guid uploadId))
            {
                result = this.BadRequest("Invalid upload id");
            }
            else
            {
                if (string.IsNullOrEmpty(relativePath))
                    relativePath = fileName;

                string destPath;
                string pathError;
                if (!this.TryResolveUploadPath(destinationDir, relativePath, out destPath, out pathError))
                    return this.BadRequest(pathError);
                if (Path.GetFileName(destPath) != fileName)
                    return this.BadRequest("File name does not match relative path");
                if (!Directory.Exists(Path.GetDirectoryName(destPath)))
                    return this.BadRequest("Upload directory was not prepared");

                int chunkIndex;
                int totalChunks;
                bool chunkIndexValid = int.TryParse(chunkIndexStr, out chunkIndex);
                bool totalChunksValid = int.TryParse(totalChunksStr, out totalChunks);

                if (!chunkIndexValid || !totalChunksValid || totalChunks <= 0 || chunkIndex < 0 || chunkIndex >= totalChunks)
                {
                    result = this.BadRequest("Invalid chunk metadata");
                }
                else
                {
                    try
                    {
                        tempPath = Path.Combine(destinationDir, "." + fileName + "." + uploadId.ToString("N") + ".uploading");
                        cancellationToken.ThrowIfCancellationRequested();

                        if (chunkIndex > 0)
                        {
                            if (!System.IO.File.Exists(tempPath))
                            {
                                result = this.BadRequest("Upload chunks must be sent in order");
                                return result;
                            }

                            long expectedLength = chunkIndex * MAX_CHUNK_SIZE;
                            FileInfo tempInfo = new FileInfo(tempPath);
                            if (tempInfo.Length != expectedLength)
                            {
                                this.DeleteUploadTempFile(tempPath);
                                result = this.BadRequest("Upload chunk sequence is inconsistent");
                                return result;
                            }
                        }

                        // Write chunk data to temp file
                        FileMode fileMode = chunkIndex == 0 ? FileMode.Create : FileMode.Append;
                        await using (FileStream fs = new FileStream(tempPath, fileMode, FileAccess.Write, FileShare.None, 81920, true))
                        {
                            await this.Request.Body.CopyToAsync(fs, cancellationToken);
                            await fs.FlushAsync(cancellationToken);
                        }

                        // Last chunk: rename temp file to final name
                        if (chunkIndex >= totalChunks - 1)
                        {
                            FileOperationResult permissionResult = null;
                            bool committed = this._workspaceService.TryExecuteMutation(workspaceToken, () =>
                            {
                                System.IO.File.Move(tempPath, destPath, true);
                                permissionResult = this._permissionService.ApplyDefaultCreationPermissions(destPath, false, cancellationToken);
                            });
                            if (!committed)
                            {
                                this.DeleteUploadTempFile(tempPath);
                                return this.StatusCode(409, "Workspace lease revoked");
                            }
                            if (permissionResult != null && !permissionResult.Success)
                            {
                                return this.StatusCode(500, permissionResult.ErrorMessage);
                            }
                        }

                        result = this.Ok(new { success = true, chunk = chunkIndex, total = totalChunks });
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        this.DeleteUploadTempFile(tempPath);
                        result = this.StatusCode(403, "Access denied: " + ex.Message);
                    }
                    catch (OperationCanceledException)
                    {
                        this.DeleteUploadTempFile(tempPath);
                        result = this.StatusCode(409, "Workspace lease revoked");
                    }
                    catch (IOException ex)
                    {
                        this.DeleteUploadTempFile(tempPath);
                        result = this.StatusCode(500, "I/O error: " + ex.Message);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Creates or finalizes one directory in a hierarchical upload
        /// </summary>
        /// <returns>Directory operation result</returns>
        [HttpPost("upload-directory")]
        public IActionResult UploadDirectory()
        {
            WorkspaceClientToken workspaceToken;
            if (!this.TryGetWorkspaceToken(out workspaceToken))
                return this.StatusCode(409, "Workspace lease revoked");

            string destinationDir = Uri.UnescapeDataString(this.Request.Headers["X-Destination-Dir"].ToString());
            string relativePath = Uri.UnescapeDataString(this.Request.Headers["X-Relative-Path"].ToString());
            bool finalize = string.Equals(this.Request.Headers["X-Finalize"].ToString(), "true", StringComparison.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(destinationDir) || !this._securityService.IsPathSafe(destinationDir) || !Directory.Exists(destinationDir))
                return this.BadRequest("Invalid destination directory");

            string directoryPath;
            string pathError;
            if (!this.TryResolveUploadPath(destinationDir, relativePath, out directoryPath, out pathError))
                return this.BadRequest(pathError);

            CancellationToken cancellationToken = this._workspaceService.GetRevocationToken(workspaceToken);
            try
            {
                bool created = false;
                FileOperationResult permissionResult = null;
                bool committed = this._workspaceService.TryExecuteMutation(workspaceToken, () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (finalize)
                    {
                        if (!Directory.Exists(directoryPath))
                            throw new DirectoryNotFoundException("Upload directory not found: " + relativePath);
                        permissionResult = this._permissionService.ApplyDefaultCreationPermissions(directoryPath, true, cancellationToken);
                    }
                    else
                    {
                        created = !Directory.Exists(directoryPath);
                        Directory.CreateDirectory(directoryPath);
                    }
                });

                if (!committed)
                    return this.StatusCode(409, "Workspace lease revoked");
                if (permissionResult != null && !permissionResult.Success)
                    return this.StatusCode(500, permissionResult.ErrorMessage);

                IActionResult result = this.Ok(new { success = true, created = created });
                return result;
            }
            catch (UnauthorizedAccessException ex)
            {
                return this.StatusCode(403, "Access denied: " + ex.Message);
            }
            catch (OperationCanceledException)
            {
                return this.StatusCode(409, "Workspace lease revoked");
            }
            catch (IOException ex)
            {
                return this.StatusCode(500, "I/O error: " + ex.Message);
            }
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Validates request lease headers and returns the current workspace token
        /// </summary>
        /// <param name="workspaceToken">Validated workspace token</param>
        /// <returns>True when the request controls the workspace</returns>
        private bool TryGetWorkspaceToken(out WorkspaceClientToken workspaceToken)
        {
            string attachmentId = this.Request.Headers["X-Bivium-Attachment"].ToString();
            string generationValue = this.Request.Headers["X-Bivium-Lease-Generation"].ToString();
            long generation;
            bool result = long.TryParse(generationValue, out generation);
            workspaceToken = result ? new WorkspaceClientToken(attachmentId, generation) : default;
            result = result && this._workspaceService.ValidateMutation(workspaceToken);
            return result;
        }

        /// <summary>
        /// Resolves a browser-relative upload path inside its destination directory
        /// </summary>
        /// <param name="destinationDir">Server destination directory</param>
        /// <param name="relativePath">Browser-relative path using slash separators</param>
        /// <param name="resolvedPath">Validated absolute path</param>
        /// <param name="errorMessage">Validation error</param>
        /// <returns>True when the path is valid and inside the destination</returns>
        private bool TryResolveUploadPath(string destinationDir, string relativePath, out string resolvedPath, out string errorMessage)
        {
            resolvedPath = "";
            errorMessage = "Invalid relative upload path";
            if (string.IsNullOrWhiteSpace(relativePath) || relativePath.StartsWith('/') || relativePath.StartsWith('\\') || Path.IsPathRooted(relativePath))
                return false;

            string[] parts = relativePath.Split('/');
            string combinedPath = destinationDir;
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                if (string.IsNullOrWhiteSpace(part) || part == "." || part == ".." || part.Contains('\\') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    return false;
                combinedPath = Path.Combine(combinedPath, part);
            }

            string fullDestination = Path.GetFullPath(destinationDir);
            string fullPath = Path.GetFullPath(combinedPath);
            if (!fullDestination.EndsWith(Path.DirectorySeparatorChar))
                fullDestination += Path.DirectorySeparatorChar;

            StringComparison comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!fullPath.StartsWith(fullDestination, comparison))
                return false;

            resolvedPath = fullPath;
            errorMessage = "";
            return true;
        }

        /// <summary>
        /// Removes the temporary file of a cancelled upload without hiding the primary result
        /// </summary>
        /// <param name="tempPath">Temporary path already validated and built by the controller</param>
        private void DeleteUploadTempFile(string tempPath)
        {
            if (string.IsNullOrEmpty(tempPath))
                return;

            try
            {
                if (System.IO.File.Exists(tempPath))
                    System.IO.File.Delete(tempPath);
            }
            catch (IOException)
            {
                // Best-effort cleanup must not hide the primary HTTP result
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort cleanup must not hide the primary HTTP result
            }
        }

        #endregion
    }

    /// <summary>
    /// Custom ActionResult that streams a ZIP archive directly to the response
    /// </summary>
    public class ZipStreamResult : IActionResult
    {
        #region Class Variables

        /// <summary>
        /// Source directory to zip
        /// </summary>
        private readonly string _sourcePath;

        /// <summary>
        /// Output file name for Content-Disposition header
        /// </summary>
        private readonly string _fileName;

        #endregion

        #region Constructor

        /// <summary>
        /// Creates a new ZipStreamResult
        /// </summary>
        /// <param name="sourcePath">Directory to zip</param>
        /// <param name="fileName">Download file name</param>
        public ZipStreamResult(string sourcePath, string fileName)
        {
            this._sourcePath = sourcePath;
            this._fileName = fileName;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Executes the result by streaming ZIP content to the response
        /// </summary>
        /// <param name="context">Action context</param>
        /// <returns>Task</returns>
        public System.Threading.Tasks.Task ExecuteResultAsync(ActionContext context)
        {
            HttpResponse response = context.HttpContext.Response;
            response.ContentType = "application/zip";
            response.Headers.Append("Content-Disposition", "attachment; filename=\"" + this._fileName + "\"");

            // Stream ZIP directly to response body (store mode, no compression)
            ZipArchive archive = new ZipArchive(response.Body, ZipArchiveMode.Create, true);
            this.AddDirectoryToZip(archive, this._sourcePath, "");
            archive.Dispose();

            return System.Threading.Tasks.Task.CompletedTask;
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Recursively adds directory contents to the ZIP archive
        /// </summary>
        /// <param name="archive">ZIP archive</param>
        /// <param name="sourceDir">Source directory path</param>
        /// <param name="entryPrefix">Entry path prefix for archive structure</param>
        private void AddDirectoryToZip(ZipArchive archive, string sourceDir, string entryPrefix)
        {
            DirectoryInfo dirInfo = new DirectoryInfo(sourceDir);

            // Add files
            FileInfo[] files = dirInfo.GetFiles();
            DirectoryInfo[] subDirs = dirInfo.GetDirectories();

            if (files.Length == 0 && subDirs.Length == 0 && !string.IsNullOrEmpty(entryPrefix))
            {
                archive.CreateEntry(entryPrefix + "/");
            }

            for (int i = 0; i < files.Length; i++)
            {
                string entryName = string.IsNullOrEmpty(entryPrefix) ? files[i].Name : entryPrefix + "/" + files[i].Name;
                ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);

                // Copy file content to zip entry
                Stream entryStream = entry.Open();
                FileStream fileStream = files[i].OpenRead();
                fileStream.CopyTo(entryStream);
                fileStream.Close();
                fileStream.Dispose();
                entryStream.Close();
                entryStream.Dispose();
            }

            // Add subdirectories recursively
            for (int i = 0; i < subDirs.Length; i++)
            {
                string subPrefix = string.IsNullOrEmpty(entryPrefix) ? subDirs[i].Name : entryPrefix + "/" + subDirs[i].Name;
                this.AddDirectoryToZip(archive, subDirs[i].FullName, subPrefix);
            }
        }

        #endregion
    }

    /// <summary>
    /// Custom ActionResult that streams multiple filesystem entries as one ZIP archive
    /// </summary>
    public class MultiZipStreamResult : IActionResult
    {
        #region Class Variables

        /// <summary>
        /// Source paths to zip
        /// </summary>
        private readonly List<string> _sourcePaths;

        /// <summary>
        /// Output file name for Content-Disposition header
        /// </summary>
        private readonly string _fileName;

        #endregion

        #region Constructor

        /// <summary>
        /// Creates a new MultiZipStreamResult
        /// </summary>
        /// <param name="sourcePaths">Files and directories to zip</param>
        /// <param name="fileName">Download file name</param>
        public MultiZipStreamResult(List<string> sourcePaths, string fileName)
        {
            this._sourcePaths = sourcePaths;
            this._fileName = fileName;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Executes the result by streaming ZIP content to the response
        /// </summary>
        /// <param name="context">Action context</param>
        /// <returns>Task</returns>
        public System.Threading.Tasks.Task ExecuteResultAsync(ActionContext context)
        {
            HttpResponse response = context.HttpContext.Response;
            response.ContentType = "application/zip";
            response.Headers.Append("Content-Disposition", "attachment; filename=\"" + this._fileName + "\"");

            ZipArchive archive = new ZipArchive(response.Body, ZipArchiveMode.Create, true);
            for (int i = 0; i < this._sourcePaths.Count; i++)
            {
                string sourcePath = this._sourcePaths[i];
                string entryName = Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

                if (System.IO.File.Exists(sourcePath))
                {
                    this.AddFileToZip(archive, sourcePath, entryName);
                }
                else if (Directory.Exists(sourcePath))
                {
                    this.AddDirectoryToZip(archive, sourcePath, entryName);
                }
            }
            archive.Dispose();

            return System.Threading.Tasks.Task.CompletedTask;
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Adds a file to the ZIP archive
        /// </summary>
        /// <param name="archive">ZIP archive</param>
        /// <param name="sourceFile">Source file path</param>
        /// <param name="entryName">ZIP entry name</param>
        private void AddFileToZip(ZipArchive archive, string sourceFile, string entryName)
        {
            ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);

            Stream entryStream = entry.Open();
            FileStream fileStream = System.IO.File.OpenRead(sourceFile);
            fileStream.CopyTo(entryStream);
            fileStream.Close();
            fileStream.Dispose();
            entryStream.Close();
            entryStream.Dispose();
        }

        /// <summary>
        /// Recursively adds directory contents to the ZIP archive under a root entry prefix
        /// </summary>
        /// <param name="archive">ZIP archive</param>
        /// <param name="sourceDir">Source directory path</param>
        /// <param name="entryPrefix">Entry path prefix</param>
        private void AddDirectoryToZip(ZipArchive archive, string sourceDir, string entryPrefix)
        {
            DirectoryInfo dirInfo = new DirectoryInfo(sourceDir);

            FileInfo[] files = dirInfo.GetFiles();
            DirectoryInfo[] subDirs = dirInfo.GetDirectories();

            if (files.Length == 0 && subDirs.Length == 0)
            {
                archive.CreateEntry(entryPrefix + "/");
            }

            for (int i = 0; i < files.Length; i++)
            {
                string entryName = entryPrefix + "/" + files[i].Name;
                this.AddFileToZip(archive, files[i].FullName, entryName);
            }

            for (int i = 0; i < subDirs.Length; i++)
            {
                string subPrefix = entryPrefix + "/" + subDirs[i].Name;
                this.AddDirectoryToZip(archive, subDirs[i].FullName, subPrefix);
            }
        }

        #endregion
    }
}
