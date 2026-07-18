using Bivium.Models;

namespace Bivium.Services
{
    /// <summary>
    /// Handles file manipulation operations (copy, move, delete, rename, mkdir)
    /// </summary>
    public class FileOperationService : IFileOperationService
    {
        #region Class Variables

        /// <summary>
        /// Security service for path validation
        /// </summary>
        private readonly SecurityService _securityService;

        #endregion

        #region Constructor

        /// <summary>
        /// Creates a new FileOperationService
        /// </summary>
        /// <param name="securityService">Security service instance</param>
        public FileOperationService(SecurityService securityService)
        {
            this._securityService = securityService;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Copies files and directories to a destination
        /// </summary>
        /// <param name="sourcePaths">List of source file/directory paths</param>
        /// <param name="destinationDir">Destination directory path</param>
        /// <param name="overwritePaths">Source paths approved for overwrite</param>
        /// <returns>Operation result</returns>
        public FileOperationResult CopyEntries(List<string> sourcePaths, string destinationDir, List<string> overwritePaths = null)
        {
            int processed = 0;
            int failed = 0;
            string lastError = "";

            for (int i = 0; i < sourcePaths.Count; i++)
            {
                string source = sourcePaths[i];

                if (!this._securityService.ArePathsSafe(source, destinationDir))
                {
                    failed++;
                    lastError = "Invalid path: " + source;
                    continue;
                }

                try
                {
                    bool overwrite = this.IsOverwriteRequested(source, overwritePaths);
                    string destPath = overwrite ? this.GetDefaultDestinationPath(source, destinationDir) : this.GetCopyDestinationPath(source, destinationDir);
                    string validationError = this.GetTransferValidationError(source, destPath, false);
                    if (!string.IsNullOrEmpty(validationError))
                    {
                        failed++;
                        lastError = validationError;
                        continue;
                    }

                    if (Directory.Exists(source))
                    {
                        // Recursive directory copy
                        this.CopyDirectoryRecursive(source, destPath);
                        processed++;
                    }
                    else if (File.Exists(source))
                    {
                        if (Directory.Exists(destPath))
                        {
                            failed++;
                            lastError = "Cannot overwrite directory with file: " + destPath;
                            continue;
                        }

                        File.Copy(source, destPath, overwrite);
                        processed++;
                    }
                    else
                    {
                        failed++;
                        lastError = "Source not found: " + source;
                    }
                }
                catch (UnauthorizedAccessException ex)
                {
                    failed++;
                    lastError = "Access denied: " + ex.Message;
                }
                catch (IOException ex)
                {
                    failed++;
                    lastError = "I/O error: " + ex.Message;
                }
            }

            FileOperationResult result = new FileOperationResult();
            result.Success = failed == 0;
            result.FilesProcessed = processed;
            result.FilesFailed = failed;
            result.ErrorMessage = lastError;
            return result;
        }

        /// <summary>
        /// Copies files and directories to a destination with progress reporting
        /// </summary>
        /// <param name="sourcePaths">List of source file/directory paths</param>
        /// <param name="destinationDir">Destination directory path</param>
        /// <param name="onProgress">Callback invoked after each file (currentFile, totalFiles, currentFileName)</param>
        /// <param name="overwritePaths">Source paths approved for overwrite</param>
        /// <param name="cancellationToken">Cancellation token for lease revocation</param>
        /// <returns>Operation result</returns>
        public FileOperationResult CopyEntriesWithProgress(List<string> sourcePaths, string destinationDir, Action<int, int, string> onProgress, List<string> overwritePaths = null, CancellationToken cancellationToken = default)
        {
            int processed = 0;
            int failed = 0;
            string lastError = "";

            // Count total files across all source paths
            int totalFiles = 0;
            for (int i = 0; i < sourcePaths.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                totalFiles += this.CountFilesRecursive(sourcePaths[i], cancellationToken);
            }

            int currentCount = 0;

            for (int i = 0; i < sourcePaths.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string source = sourcePaths[i];

                if (!this._securityService.ArePathsSafe(source, destinationDir))
                {
                    failed++;
                    lastError = "Invalid path: " + source;
                    continue;
                }

                try
                {
                    bool overwrite = this.IsOverwriteRequested(source, overwritePaths);
                    string destPath = overwrite ? this.GetDefaultDestinationPath(source, destinationDir) : this.GetCopyDestinationPath(source, destinationDir);
                    string validationError = this.GetTransferValidationError(source, destPath, false);
                    if (!string.IsNullOrEmpty(validationError))
                    {
                        failed++;
                        lastError = validationError;
                        continue;
                    }

                    if (Directory.Exists(source))
                    {
                        // Recursive directory copy with progress
                        this.CopyDirectoryRecursiveWithProgress(source, destPath, onProgress, ref currentCount, totalFiles, cancellationToken);
                        processed++;
                    }
                    else if (File.Exists(source))
                    {
                        if (Directory.Exists(destPath))
                        {
                            failed++;
                            lastError = "Cannot overwrite directory with file: " + destPath;
                            continue;
                        }

                        this.CopyFileCancellable(source, destPath, overwrite, cancellationToken);
                        currentCount++;
                        onProgress(currentCount, totalFiles, Path.GetFileName(source));
                        processed++;
                    }
                    else
                    {
                        failed++;
                        lastError = "Source not found: " + source;
                    }
                }
                catch (UnauthorizedAccessException ex)
                {
                    failed++;
                    lastError = "Access denied: " + ex.Message;
                }
                catch (IOException ex)
                {
                    failed++;
                    lastError = "I/O error: " + ex.Message;
                }
            }

            FileOperationResult result = new FileOperationResult();
            result.Success = failed == 0;
            result.FilesProcessed = processed;
            result.FilesFailed = failed;
            result.ErrorMessage = lastError;
            return result;
        }

        /// <summary>
        /// Moves files and directories to a destination
        /// </summary>
        /// <param name="sourcePaths">List of source file/directory paths</param>
        /// <param name="destinationDir">Destination directory path</param>
        /// <param name="overwritePaths">Source paths approved for overwrite</param>
        /// <returns>Operation result</returns>
        public FileOperationResult MoveEntries(List<string> sourcePaths, string destinationDir, List<string> overwritePaths = null)
        {
            int processed = 0;
            int failed = 0;
            string lastError = "";

            for (int i = 0; i < sourcePaths.Count; i++)
            {
                string source = sourcePaths[i];

                if (!this._securityService.ArePathsSafe(source, destinationDir))
                {
                    failed++;
                    lastError = "Invalid path: " + source;
                    continue;
                }

                try
                {
                    bool overwrite = this.IsOverwriteRequested(source, overwritePaths);
                    string destName = Path.GetFileName(source);
                    string destPath = Path.Combine(destinationDir, destName);
                    string validationError = this.GetTransferValidationError(source, destPath, true);
                    if (!string.IsNullOrEmpty(validationError))
                    {
                        failed++;
                        lastError = validationError;
                        continue;
                    }

                    if (Directory.Exists(source))
                    {
                        if (File.Exists(destPath))
                        {
                            failed++;
                            lastError = "Cannot overwrite file with directory: " + destPath;
                            continue;
                        }

                        if (Directory.Exists(destPath) && !overwrite)
                        {
                            failed++;
                            lastError = "Destination already exists: " + destPath;
                            continue;
                        }

                        if (Directory.Exists(destPath))
                        {
                            this.CopyDirectoryRecursive(source, destPath);
                            Directory.Delete(source, true);
                        }
                        else
                        {
                            Directory.Move(source, destPath);
                        }

                        processed++;
                    }
                    else if (File.Exists(source))
                    {
                        if (Directory.Exists(destPath))
                        {
                            failed++;
                            lastError = "Cannot overwrite directory with file: " + destPath;
                            continue;
                        }

                        if (File.Exists(destPath) && !overwrite)
                        {
                            failed++;
                            lastError = "Destination already exists: " + destPath;
                            continue;
                        }

                        File.Move(source, destPath, overwrite);
                        processed++;
                    }
                    else
                    {
                        failed++;
                        lastError = "Source not found: " + source;
                    }
                }
                catch (UnauthorizedAccessException ex)
                {
                    failed++;
                    lastError = "Access denied: " + ex.Message;
                }
                catch (IOException ex)
                {
                    // Check whether the move succeeded despite the exception
                    string destName2 = Path.GetFileName(source);
                    string destPath2 = Path.Combine(destinationDir, destName2);
                    bool movedAnyway = (File.Exists(destPath2) || Directory.Exists(destPath2)) && !File.Exists(source) && !Directory.Exists(source);

                    if (movedAnyway)
                    {
                        processed++;
                    }
                    else
                    {
                        failed++;
                        lastError = "I/O error: " + ex.Message;
                    }
                }
            }

            FileOperationResult result = new FileOperationResult();
            result.Success = failed == 0;
            result.FilesProcessed = processed;
            result.FilesFailed = failed;
            result.ErrorMessage = lastError;
            return result;
        }

        /// <summary>
        /// Moves files and directories to a destination with progress reporting
        /// </summary>
        /// <param name="sourcePaths">List of source file/directory paths</param>
        /// <param name="destinationDir">Destination directory path</param>
        /// <param name="onProgress">Callback invoked after each entry (currentEntry, totalEntries, currentEntryName)</param>
        /// <param name="overwritePaths">Source paths approved for overwrite</param>
        /// <param name="cancellationToken">Cancellation token for lease revocation</param>
        /// <returns>Operation result</returns>
        public FileOperationResult MoveEntriesWithProgress(List<string> sourcePaths, string destinationDir, Action<int, int, string> onProgress, List<string> overwritePaths = null, CancellationToken cancellationToken = default)
        {
            int processed = 0;
            int failed = 0;
            string lastError = "";
            int totalEntries = sourcePaths.Count;

            for (int i = 0; i < sourcePaths.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string source = sourcePaths[i];

                if (!this._securityService.ArePathsSafe(source, destinationDir))
                {
                    failed++;
                    lastError = "Invalid path: " + source;
                    continue;
                }

                try
                {
                    bool overwrite = this.IsOverwriteRequested(source, overwritePaths);
                    string destName = Path.GetFileName(source);
                    string destPath = Path.Combine(destinationDir, destName);
                    string validationError = this.GetTransferValidationError(source, destPath, true);
                    if (!string.IsNullOrEmpty(validationError))
                    {
                        failed++;
                        lastError = validationError;
                        continue;
                    }

                    if (Directory.Exists(source))
                    {
                        if (File.Exists(destPath))
                        {
                            failed++;
                            lastError = "Cannot overwrite file with directory: " + destPath;
                            continue;
                        }

                        if (Directory.Exists(destPath) && !overwrite)
                        {
                            failed++;
                            lastError = "Destination already exists: " + destPath;
                            continue;
                        }

                        if (Directory.Exists(destPath))
                        {
                            this.CopyDirectoryRecursive(source, destPath, cancellationToken);
                            cancellationToken.ThrowIfCancellationRequested();
                            Directory.Delete(source, true);
                        }
                        else
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            Directory.Move(source, destPath);
                        }

                        processed++;
                        onProgress(i + 1, totalEntries, destName);
                    }
                    else if (File.Exists(source))
                    {
                        if (Directory.Exists(destPath))
                        {
                            failed++;
                            lastError = "Cannot overwrite directory with file: " + destPath;
                            continue;
                        }

                        if (File.Exists(destPath) && !overwrite)
                        {
                            failed++;
                            lastError = "Destination already exists: " + destPath;
                            continue;
                        }

                        cancellationToken.ThrowIfCancellationRequested();
                        File.Move(source, destPath, overwrite);
                        processed++;
                        onProgress(i + 1, totalEntries, destName);
                    }
                    else
                    {
                        failed++;
                        lastError = "Source not found: " + source;
                    }
                }
                catch (UnauthorizedAccessException ex)
                {
                    failed++;
                    lastError = "Access denied: " + ex.Message;
                }
                catch (IOException ex)
                {
                    // Check whether the move succeeded despite the exception
                    string destName2 = Path.GetFileName(source);
                    string destPath2 = Path.Combine(destinationDir, destName2);
                    bool movedAnyway = (File.Exists(destPath2) || Directory.Exists(destPath2)) && !File.Exists(source) && !Directory.Exists(source);

                    if (movedAnyway)
                    {
                        processed++;
                        onProgress(i + 1, totalEntries, destName2);
                    }
                    else
                    {
                        failed++;
                        lastError = "I/O error: " + ex.Message;
                    }
                }
            }

            FileOperationResult result = new FileOperationResult();
            result.Success = failed == 0;
            result.FilesProcessed = processed;
            result.FilesFailed = failed;
            result.ErrorMessage = lastError;
            return result;
        }

        /// <summary>
        /// Deletes files and directories
        /// </summary>
        /// <param name="paths">List of paths to delete</param>
        /// <param name="cancellationToken">Cancellation token for lease revocation</param>
        /// <returns>Operation result</returns>
        public FileOperationResult DeleteEntries(List<string> paths, CancellationToken cancellationToken = default)
        {
            int processed = 0;
            int failed = 0;
            string lastError = "";

            for (int i = 0; i < paths.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string path = paths[i];

                if (!this._securityService.IsPathSafe(path))
                {
                    failed++;
                    lastError = "Invalid path: " + path;
                    continue;
                }

                try
                {
                    if (Directory.Exists(path))
                    {
                        this.DeleteDirectoryCancellable(path, cancellationToken);
                        processed++;
                    }
                    else if (File.Exists(path))
                    {
                        File.Delete(path);
                        processed++;
                    }
                    else
                    {
                        failed++;
                        lastError = "Not found: " + path;
                    }
                }
                catch (UnauthorizedAccessException ex)
                {
                    failed++;
                    lastError = "Access denied: " + ex.Message;
                }
                catch (IOException ex)
                {
                    failed++;
                    lastError = "I/O error: " + ex.Message;
                }
            }

            FileOperationResult result = new FileOperationResult();
            result.Success = failed == 0;
            result.FilesProcessed = processed;
            result.FilesFailed = failed;
            result.ErrorMessage = lastError;
            return result;
        }

        /// <summary>
        /// Renames a file or directory
        /// </summary>
        /// <param name="path">Current path</param>
        /// <param name="newName">New name (not full path, just the name)</param>
        /// <returns>Operation result</returns>
        public FileOperationResult RenameEntry(string path, string newName)
        {
            FileOperationResult result = new FileOperationResult();

            if (!this._securityService.IsPathSafe(path))
            {
                result = FileOperationResult.Fail("Invalid path: " + path);
            }
            else if (string.IsNullOrWhiteSpace(newName))
            {
                result = FileOperationResult.Fail("Name cannot be empty");
            }
            else if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                result = FileOperationResult.Fail("Name contains invalid characters");
            }
            else
            {
                try
                {
                    string parentDir = Path.GetDirectoryName(path);
                    string newPath = Path.Combine(parentDir, newName);

                    if (Directory.Exists(path))
                    {
                        Directory.Move(path, newPath);
                        result = FileOperationResult.Ok(1);
                    }
                    else if (File.Exists(path))
                    {
                        File.Move(path, newPath);
                        result = FileOperationResult.Ok(1);
                    }
                    else
                    {
                        result = FileOperationResult.Fail("Not found: " + path);
                    }
                }
                catch (UnauthorizedAccessException ex)
                {
                    result = FileOperationResult.Fail("Access denied: " + ex.Message);
                }
                catch (IOException ex)
                {
                    result = FileOperationResult.Fail("I/O error: " + ex.Message);
                }
            }

            return result;
        }

        /// <summary>
        /// Creates a new directory
        /// </summary>
        /// <param name="parentPath">Parent directory path</param>
        /// <param name="name">Name for the new directory</param>
        /// <returns>Operation result</returns>
        public FileOperationResult CreateDirectory(string parentPath, string name)
        {
            FileOperationResult result = new FileOperationResult();

            if (!this._securityService.IsPathSafe(parentPath))
            {
                result = FileOperationResult.Fail("Invalid path: " + parentPath);
            }
            else if (string.IsNullOrWhiteSpace(name))
            {
                result = FileOperationResult.Fail("Name cannot be empty");
            }
            else if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                result = FileOperationResult.Fail("Name contains invalid characters");
            }
            else
            {
                try
                {
                    string newPath = Path.Combine(parentPath, name);

                    if (Directory.Exists(newPath))
                    {
                        result = FileOperationResult.Fail("Directory already exists: " + name);
                    }
                    else
                    {
                        Directory.CreateDirectory(newPath);
                        result = FileOperationResult.Ok(1);
                    }
                }
                catch (UnauthorizedAccessException ex)
                {
                    result = FileOperationResult.Fail("Access denied: " + ex.Message);
                }
                catch (IOException ex)
                {
                    result = FileOperationResult.Fail("I/O error: " + ex.Message);
                }
            }

            return result;
        }

        /// <summary>
        /// Creates a new empty file
        /// </summary>
        /// <param name="parentPath">Parent directory path</param>
        /// <param name="name">File name</param>
        /// <returns>Operation result</returns>
        public FileOperationResult CreateFile(string parentPath, string name)
        {
            FileOperationResult result = new FileOperationResult();

            if (!this._securityService.IsPathSafe(parentPath))
            {
                result = FileOperationResult.Fail("Invalid path: " + parentPath);
            }
            else if (string.IsNullOrWhiteSpace(name))
            {
                result = FileOperationResult.Fail("Name cannot be empty");
            }
            else if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                result = FileOperationResult.Fail("Name contains invalid characters");
            }
            else
            {
                try
                {
                    string newPath = Path.Combine(parentPath, name);

                    if (File.Exists(newPath))
                    {
                        result = FileOperationResult.Fail("File already exists: " + name);
                    }
                    else
                    {
                        // Create empty file
                        FileStream fs = File.Create(newPath);
                        fs.Close();
                        fs.Dispose();
                        result = FileOperationResult.Ok(1);
                    }
                }
                catch (UnauthorizedAccessException ex)
                {
                    result = FileOperationResult.Fail("Access denied: " + ex.Message);
                }
                catch (IOException ex)
                {
                    result = FileOperationResult.Fail("I/O error: " + ex.Message);
                }
            }

            return result;
        }

        /// <summary>
        /// Reads text content from a file
        /// </summary>
        /// <param name="path">File path</param>
        /// <param name="maxSizeBytes">Maximum allowed file size in bytes</param>
        /// <returns>File text result</returns>
        public FileTextResult ReadFileText(string path, long maxSizeBytes)
        {
            FileTextResult result;

            if (!this._securityService.IsPathSafe(path))
            {
                result = FileTextResult.Fail("Invalid path: " + path);
            }
            else if (!File.Exists(path))
            {
                result = FileTextResult.Fail("File not found: " + path);
            }
            else
            {
                try
                {
                    FileInfo fileInfo = new FileInfo(path);

                    if (fileInfo.Length > maxSizeBytes)
                    {
                        result = FileTextResult.Fail("File is too large to edit");
                    }
                    else
                    {
                        result = FileTextResult.Ok(File.ReadAllText(path));
                    }
                }
                catch (UnauthorizedAccessException ex)
                {
                    result = FileTextResult.Fail("Access denied: " + ex.Message);
                }
                catch (IOException ex)
                {
                    result = FileTextResult.Fail("I/O error: " + ex.Message);
                }
            }

            return result;
        }

        /// <summary>
        /// Writes content to a temporary file and publishes it only after final authorization
        /// </summary>
        /// <param name="path">Existing file path</param>
        /// <param name="content">Content to write</param>
        /// <param name="tryCommit">Callback that atomically authorizes the final move</param>
        /// <param name="cancellationToken">Lease revocation token</param>
        /// <returns>Operation result</returns>
        public async System.Threading.Tasks.Task<FileOperationResult> WriteFileTextAsync(string path, string content, Func<Action, bool> tryCommit, CancellationToken cancellationToken = default)
        {
            FileOperationResult result;
            string tempPath = "";

            if (tryCommit == null)
                throw new ArgumentNullException(nameof(tryCommit));

            if (!this._securityService.IsPathSafe(path))
            {
                result = FileOperationResult.Fail("Invalid path: " + path);
            }
            else if (!File.Exists(path))
            {
                result = FileOperationResult.Fail("File not found: " + path);
            }
            else
            {
                try
                {
                    // The long write targets a temporary file without retaining the lease lock
                    cancellationToken.ThrowIfCancellationRequested();
                    tempPath = path + ".bivium-save-" + Guid.NewGuid().ToString("N") + ".tmp";
                    await File.WriteAllTextAsync(tempPath, content, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();

                    // Only the final publish runs inside the atomic workspace-authorized mutation
                    bool committed = tryCommit(() => File.Move(tempPath, path, true));
                    if (committed)
                    {
                        tempPath = "";
                        result = FileOperationResult.Ok(1);
                    }
                    else
                    {
                        result = FileOperationResult.Fail("Workspace lease revoked");
                    }
                }
                catch (UnauthorizedAccessException ex)
                {
                    result = FileOperationResult.Fail("Access denied: " + ex.Message);
                }
                catch (IOException ex)
                {
                    result = FileOperationResult.Fail("I/O error: " + ex.Message);
                }
                catch (OperationCanceledException)
                {
                    result = FileOperationResult.Fail("Workspace lease revoked");
                }
                finally
                {
                    if (!string.IsNullOrEmpty(tempPath))
                    {
                        try
                        {
                            if (File.Exists(tempPath))
                                File.Delete(tempPath);
                        }
                        catch (IOException)
                        {
                            // Best-effort cleanup must not hide the primary result
                        }
                        catch (UnauthorizedAccessException)
                        {
                            // Best-effort cleanup must not hide the primary result
                        }
                    }
                }
            }

            return result;
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Builds a unique destination path for copy operations
        /// </summary>
        /// <param name="sourcePath">Source file or directory path</param>
        /// <param name="destinationDir">Destination directory</param>
        /// <returns>Unique destination path</returns>
        private string GetCopyDestinationPath(string sourcePath, string destinationDir)
        {
            string sourceName = Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            string destinationPath = Path.Combine(destinationDir, sourceName);

            if (!File.Exists(destinationPath) && !Directory.Exists(destinationPath))
            {
                return destinationPath;
            }

            bool isDirectory = Directory.Exists(sourcePath);
            string name = isDirectory ? sourceName : Path.GetFileNameWithoutExtension(sourceName);
            string extension = isDirectory ? "" : Path.GetExtension(sourceName);

            destinationPath = Path.Combine(destinationDir, name + " - Copy" + extension);
            int copyIndex = 2;
            while (File.Exists(destinationPath) || Directory.Exists(destinationPath))
            {
                destinationPath = Path.Combine(destinationDir, name + " - Copy (" + copyIndex + ")" + extension);
                copyIndex++;
            }

            return destinationPath;
        }

        /// <summary>
        /// Builds the direct destination path without adding copy suffixes
        /// </summary>
        /// <param name="sourcePath">Source file or directory path</param>
        /// <param name="destinationDir">Destination directory</param>
        /// <returns>Direct destination path</returns>
        private string GetDefaultDestinationPath(string sourcePath, string destinationDir)
        {
            string sourceName = Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            string result = Path.Combine(destinationDir, sourceName);
            return result;
        }

        /// <summary>
        /// Checks whether the source path was approved for overwrite
        /// </summary>
        /// <param name="sourcePath">Source path</param>
        /// <param name="overwritePaths">Approved source paths</param>
        /// <returns>True if overwrite is approved</returns>
        private bool IsOverwriteRequested(string sourcePath, List<string> overwritePaths)
        {
            bool result = false;

            if (overwritePaths != null)
            {
                for (int i = 0; i < overwritePaths.Count; i++)
                {
                    if (this.AreSamePath(sourcePath, overwritePaths[i]))
                    {
                        result = true;
                        break;
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Validates whether a copy or move target is meaningful and safe
        /// </summary>
        /// <param name="sourcePath">Source path</param>
        /// <param name="destinationPath">Resolved destination path</param>
        /// <param name="isMove">True if this is a move operation</param>
        /// <returns>Error message, or empty if valid</returns>
        private string GetTransferValidationError(string sourcePath, string destinationPath, bool isMove)
        {
            string result = "";
            string sourceFull = Path.GetFullPath(sourcePath);
            string destinationFull = Path.GetFullPath(destinationPath);

            if (this.AreSamePath(sourceFull, destinationFull))
            {
                result = isMove ? "Source is already in the destination: " + sourcePath : "Cannot copy an entry onto itself: " + sourcePath;
            }
            else if (Directory.Exists(sourcePath) && this.IsPathInsideDirectory(destinationFull, sourceFull))
            {
                result = "Cannot " + (isMove ? "move" : "copy") + " a directory into itself: " + sourcePath;
            }

            return result;
        }

        /// <summary>
        /// Checks if two paths resolve to the same path
        /// </summary>
        /// <param name="left">First path</param>
        /// <param name="right">Second path</param>
        /// <returns>True if paths are equal</returns>
        private bool AreSamePath(string left, string right)
        {
            bool result = string.Equals(Path.TrimEndingDirectorySeparator(left), Path.TrimEndingDirectorySeparator(right), this.GetPathComparison());
            return result;
        }

        /// <summary>
        /// Checks whether a path is inside a directory
        /// </summary>
        /// <param name="path">Path to test</param>
        /// <param name="directory">Parent directory</param>
        /// <returns>True if path is inside directory</returns>
        private bool IsPathInsideDirectory(string path, string directory)
        {
            string fullPath = Path.GetFullPath(path);
            string fullDirectory = Path.GetFullPath(directory);

            if (!fullDirectory.EndsWith(Path.DirectorySeparatorChar))
            {
                fullDirectory += Path.DirectorySeparatorChar;
            }

            bool result = fullPath.StartsWith(fullDirectory, this.GetPathComparison());
            return result;
        }

        /// <summary>
        /// Gets the appropriate path comparison for the current platform
        /// </summary>
        /// <returns>String comparison for filesystem paths</returns>
        private StringComparison GetPathComparison()
        {
            StringComparison result = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return result;
        }

        /// <summary>
        /// Recursively copies a directory and all its contents
        /// </summary>
        /// <param name="sourceDir">Source directory path</param>
        /// <param name="destDir">Destination directory path</param>
        private void CopyDirectoryRecursive(string sourceDir, string destDir, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Create destination directory
            Directory.CreateDirectory(destDir);

            // Copy files
            DirectoryInfo sourceDirInfo = new DirectoryInfo(sourceDir);
            FileInfo[] files = sourceDirInfo.GetFiles();

            for (int i = 0; i < files.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string destFile = Path.Combine(destDir, files[i].Name);
                this.CopyFileCancellable(files[i].FullName, destFile, true, cancellationToken);
            }

            // Copy subdirectories recursively
            DirectoryInfo[] subDirs = sourceDirInfo.GetDirectories();

            for (int i = 0; i < subDirs.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string destSubDir = Path.Combine(destDir, subDirs[i].Name);
                this.CopyDirectoryRecursive(subDirs[i].FullName, destSubDir, cancellationToken);
            }
        }

        /// <summary>
        /// Recursively copies a directory and all its contents with progress reporting
        /// </summary>
        /// <param name="sourceDir">Source directory path</param>
        /// <param name="destDir">Destination directory path</param>
        /// <param name="onProgress">Progress callback (currentFile, totalFiles, fileName)</param>
        /// <param name="currentCount">Current file counter (passed by reference)</param>
        /// <param name="totalCount">Total number of files to copy</param>
        private void CopyDirectoryRecursiveWithProgress(string sourceDir, string destDir, Action<int, int, string> onProgress, ref int currentCount, int totalCount, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Create destination directory
            Directory.CreateDirectory(destDir);

            // Copy files with progress
            DirectoryInfo sourceDirInfo = new DirectoryInfo(sourceDir);
            FileInfo[] files = sourceDirInfo.GetFiles();

            for (int i = 0; i < files.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string destFile = Path.Combine(destDir, files[i].Name);
                this.CopyFileCancellable(files[i].FullName, destFile, true, cancellationToken);
                currentCount++;
                onProgress(currentCount, totalCount, files[i].Name);
            }

            // Copy subdirectories recursively
            DirectoryInfo[] subDirs = sourceDirInfo.GetDirectories();

            for (int i = 0; i < subDirs.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string destSubDir = Path.Combine(destDir, subDirs[i].Name);
                this.CopyDirectoryRecursiveWithProgress(subDirs[i].FullName, destSubDir, onProgress, ref currentCount, totalCount, cancellationToken);
            }
        }

        /// <summary>
        /// Counts the total number of files in a path recursively
        /// </summary>
        /// <param name="path">File or directory path</param>
        /// <returns>Total file count</returns>
        private int CountFilesRecursive(string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = 0;

            if (File.Exists(path))
            {
                count = 1;
            }
            else if (Directory.Exists(path))
            {
                try
                {
                    // Count files in this directory
                    DirectoryInfo dirInfo = new DirectoryInfo(path);
                    FileInfo[] files = dirInfo.GetFiles();
                    count = files.Length;

                    // Count files in subdirectories
                    DirectoryInfo[] subDirs = dirInfo.GetDirectories();
                    for (int i = 0; i < subDirs.Length; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        count += this.CountFilesRecursive(subDirs[i].FullName, cancellationToken);
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    count = 0;
                }
                catch (IOException)
                {
                    count = 0;
                }
            }

            return count;
        }

        /// <summary>
        /// Copies one file in blocks while observing revocation even for very large files
        /// </summary>
        private void CopyFileCancellable(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileMode destinationMode = overwrite ? FileMode.Create : FileMode.CreateNew;
            using FileStream source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using FileStream destination = new FileStream(destinationPath, destinationMode, FileAccess.Write, FileShare.None);
            source.CopyToAsync(destination, 128 * 1024, cancellationToken).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Recursively removes a directory while checking revocation between entries
        /// </summary>
        private void DeleteDirectoryCancellable(string directoryPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileAttributes attributes = File.GetAttributes(directoryPath);
            if ((attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
            {
                Directory.Delete(directoryPath, false);
                return;
            }

            string[] files = Directory.GetFiles(directoryPath);
            for (int i = 0; i < files.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.Delete(files[i]);
            }

            string[] directories = Directory.GetDirectories(directoryPath);
            for (int i = 0; i < directories.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                this.DeleteDirectoryCancellable(directories[i], cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            Directory.Delete(directoryPath, false);
        }

        #endregion
    }
}
