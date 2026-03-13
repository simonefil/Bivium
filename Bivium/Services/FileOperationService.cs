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
        /// <returns>Operation result</returns>
        public FileOperationResult CopyEntries(List<string> sourcePaths, string destinationDir)
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
                    string destName = Path.GetFileName(source);
                    string destPath = Path.Combine(destinationDir, destName);

                    if (Directory.Exists(source))
                    {
                        // Recursive directory copy
                        this.CopyDirectoryRecursive(source, destPath);
                        processed++;
                    }
                    else if (File.Exists(source))
                    {
                        File.Copy(source, destPath, true);
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
        /// <returns>Operation result</returns>
        public FileOperationResult CopyEntriesWithProgress(List<string> sourcePaths, string destinationDir, Action<int, int, string> onProgress)
        {
            int processed = 0;
            int failed = 0;
            string lastError = "";

            // Count total files across all source paths
            int totalFiles = 0;
            for (int i = 0; i < sourcePaths.Count; i++)
            {
                totalFiles += this.CountFilesRecursive(sourcePaths[i]);
            }

            int currentCount = 0;

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
                    string destName = Path.GetFileName(source);
                    string destPath = Path.Combine(destinationDir, destName);

                    if (Directory.Exists(source))
                    {
                        // Recursive directory copy with progress
                        this.CopyDirectoryRecursiveWithProgress(source, destPath, onProgress, ref currentCount, totalFiles);
                        processed++;
                    }
                    else if (File.Exists(source))
                    {
                        File.Copy(source, destPath, true);
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
        /// <returns>Operation result</returns>
        public FileOperationResult MoveEntries(List<string> sourcePaths, string destinationDir)
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
                    string destName = Path.GetFileName(source);
                    string destPath = Path.Combine(destinationDir, destName);

                    if (Directory.Exists(source))
                    {
                        Directory.Move(source, destPath);
                        processed++;
                    }
                    else if (File.Exists(source))
                    {
                        File.Move(source, destPath, true);
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
        /// Moves files and directories to a destination with progress reporting
        /// </summary>
        /// <param name="sourcePaths">List of source file/directory paths</param>
        /// <param name="destinationDir">Destination directory path</param>
        /// <param name="onProgress">Callback invoked after each entry (currentEntry, totalEntries, currentEntryName)</param>
        /// <returns>Operation result</returns>
        public FileOperationResult MoveEntriesWithProgress(List<string> sourcePaths, string destinationDir, Action<int, int, string> onProgress)
        {
            int processed = 0;
            int failed = 0;
            string lastError = "";
            int totalEntries = sourcePaths.Count;

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
                    string destName = Path.GetFileName(source);
                    string destPath = Path.Combine(destinationDir, destName);

                    if (Directory.Exists(source))
                    {
                        Directory.Move(source, destPath);
                        processed++;
                        onProgress(i + 1, totalEntries, destName);
                    }
                    else if (File.Exists(source))
                    {
                        File.Move(source, destPath, true);
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
        /// Deletes files and directories
        /// </summary>
        /// <param name="paths">List of paths to delete</param>
        /// <returns>Operation result</returns>
        public FileOperationResult DeleteEntries(List<string> paths)
        {
            int processed = 0;
            int failed = 0;
            string lastError = "";

            for (int i = 0; i < paths.Count; i++)
            {
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
                        Directory.Delete(path, true);
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
        /// <returns>File content, or empty string on failure</returns>
        public string ReadFileText(string path, long maxSizeBytes)
        {
            string result = "";

            if (this._securityService.IsPathSafe(path) && File.Exists(path))
            {
                FileInfo fileInfo = new FileInfo(path);

                if (fileInfo.Length <= maxSizeBytes)
                {
                    try
                    {
                        result = File.ReadAllText(path);
                    }
                    catch (IOException)
                    {
                        // Return empty on read failure
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // Return empty on access denied
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Writes text content to a file
        /// </summary>
        /// <param name="path">File path</param>
        /// <param name="content">Text content to write</param>
        /// <returns>Operation result</returns>
        public FileOperationResult WriteFileText(string path, string content)
        {
            FileOperationResult result = new FileOperationResult();

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
                    File.WriteAllText(path, content);
                    result = FileOperationResult.Ok(1);
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

        #endregion

        #region Private Methods

        /// <summary>
        /// Recursively copies a directory and all its contents
        /// </summary>
        /// <param name="sourceDir">Source directory path</param>
        /// <param name="destDir">Destination directory path</param>
        private void CopyDirectoryRecursive(string sourceDir, string destDir)
        {
            // Create destination directory
            Directory.CreateDirectory(destDir);

            // Copy files
            DirectoryInfo sourceDirInfo = new DirectoryInfo(sourceDir);
            FileInfo[] files = sourceDirInfo.GetFiles();

            for (int i = 0; i < files.Length; i++)
            {
                string destFile = Path.Combine(destDir, files[i].Name);
                files[i].CopyTo(destFile, true);
            }

            // Copy subdirectories recursively
            DirectoryInfo[] subDirs = sourceDirInfo.GetDirectories();

            for (int i = 0; i < subDirs.Length; i++)
            {
                string destSubDir = Path.Combine(destDir, subDirs[i].Name);
                this.CopyDirectoryRecursive(subDirs[i].FullName, destSubDir);
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
        private void CopyDirectoryRecursiveWithProgress(string sourceDir, string destDir, Action<int, int, string> onProgress, ref int currentCount, int totalCount)
        {
            // Create destination directory
            Directory.CreateDirectory(destDir);

            // Copy files with progress
            DirectoryInfo sourceDirInfo = new DirectoryInfo(sourceDir);
            FileInfo[] files = sourceDirInfo.GetFiles();

            for (int i = 0; i < files.Length; i++)
            {
                string destFile = Path.Combine(destDir, files[i].Name);
                files[i].CopyTo(destFile, true);
                currentCount++;
                onProgress(currentCount, totalCount, files[i].Name);
            }

            // Copy subdirectories recursively
            DirectoryInfo[] subDirs = sourceDirInfo.GetDirectories();

            for (int i = 0; i < subDirs.Length; i++)
            {
                string destSubDir = Path.Combine(destDir, subDirs[i].Name);
                this.CopyDirectoryRecursiveWithProgress(subDirs[i].FullName, destSubDir, onProgress, ref currentCount, totalCount);
            }
        }

        /// <summary>
        /// Counts the total number of files in a path recursively
        /// </summary>
        /// <param name="path">File or directory path</param>
        /// <returns>Total file count</returns>
        private int CountFilesRecursive(string path)
        {
            int count = 0;

            if (File.Exists(path))
            {
                count = 1;
            }
            else if (Directory.Exists(path))
            {
                // Count files in this directory
                DirectoryInfo dirInfo = new DirectoryInfo(path);
                FileInfo[] files = dirInfo.GetFiles();
                count = files.Length;

                // Count files in subdirectories
                DirectoryInfo[] subDirs = dirInfo.GetDirectories();
                for (int i = 0; i < subDirs.Length; i++)
                {
                    count += this.CountFilesRecursive(subDirs[i].FullName);
                }
            }

            return count;
        }

        #endregion
    }
}
