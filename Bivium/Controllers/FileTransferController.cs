using System.IO.Compression;
using Microsoft.AspNetCore.Mvc;
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

        #endregion

        #region Constructor

        /// <summary>
        /// Creates a new FileTransferController
        /// </summary>
        /// <param name="securityService">Security service instance</param>
        public FileTransferController(SecurityService securityService)
        {
            this._securityService = securityService;
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
        public IActionResult Upload()
        {
            IActionResult result;

            string destinationDir = this.Request.Headers["X-Destination-Dir"].ToString();
            string fileName = this.Request.Headers["X-File-Name"].ToString();
            string chunkIndexStr = this.Request.Headers["X-Chunk-Index"].ToString();
            string totalChunksStr = this.Request.Headers["X-Total-Chunks"].ToString();

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
            else
            {
                int chunkIndex = 0;
                int totalChunks = 1;
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
                        string destPath = Path.Combine(destinationDir, fileName);
                        string tempPath = destPath + ".uploading";

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
                                result = this.BadRequest("Upload chunk sequence is inconsistent");
                                return result;
                            }
                        }

                        // Write chunk data to temp file
                        FileMode fileMode = chunkIndex == 0 ? FileMode.Create : FileMode.Append;
                        FileStream fs = new FileStream(tempPath, fileMode, FileAccess.Write);
                        this.Request.Body.CopyTo(fs);
                        fs.Flush();
                        fs.Close();
                        fs.Dispose();

                        // Last chunk: rename temp file to final name
                        if (chunkIndex >= totalChunks - 1)
                        {
                            // Remove existing file if present
                            if (System.IO.File.Exists(destPath))
                            {
                                System.IO.File.Delete(destPath);
                            }

                            System.IO.File.Move(tempPath, destPath);
                        }

                        result = this.Ok(new { success = true, chunk = chunkIndex, total = totalChunks });
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        result = this.StatusCode(403, "Access denied: " + ex.Message);
                    }
                    catch (IOException ex)
                    {
                        result = this.StatusCode(500, "I/O error: " + ex.Message);
                    }
                }
            }

            return result;
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
