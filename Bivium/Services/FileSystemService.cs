using Bivium.Models;

namespace Bivium.Services
{
    /// <summary>
    /// Provides filesystem read operations with security validation
    /// </summary>
    public class FileSystemService : IFileSystemService
    {
        #region Class Variables

        /// <summary>
        /// Security service for path validation
        /// </summary>
        private readonly SecurityService _securityService;

        #endregion

        #region Constructor

        /// <summary>
        /// Creates a new FileSystemService
        /// </summary>
        /// <param name="securityService">Security service instance</param>
        public FileSystemService(SecurityService securityService)
        {
            this._securityService = securityService;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Gets all entries (files and directories) in a directory
        /// </summary>
        /// <param name="path">Directory path</param>
        /// <returns>List of entries, directories first then files, sorted by name</returns>
        public List<FileSystemEntry> GetDirectoryContents(string path)
        {
            List<FileSystemEntry> result = new List<FileSystemEntry>();

            if (this._securityService.IsPathSafe(path))
            {
                DirectoryInfo dirInfo = new DirectoryInfo(path);

                if (dirInfo.Exists)
                {
                    // Get directories first
                    try
                    {
                        DirectoryInfo[] directories = dirInfo.GetDirectories();
                        Array.Sort(directories, (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

                        for (int i = 0; i < directories.Length; i++)
                        {
                            result.Add(new FileSystemEntry(directories[i]));
                        }
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // Skip directories we cannot access
                    }

                    // Then files
                    try
                    {
                        FileInfo[] files = dirInfo.GetFiles();
                        Array.Sort(files, (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

                        for (int i = 0; i < files.Length; i++)
                        {
                            result.Add(new FileSystemEntry(files[i]));
                        }
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // Skip files we cannot access
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Gets only subdirectories in a directory (for tree view)
        /// </summary>
        /// <param name="path">Directory path</param>
        /// <returns>List of directory entries sorted by name</returns>
        public List<FileSystemEntry> GetSubDirectories(string path)
        {
            List<FileSystemEntry> result = new List<FileSystemEntry>();

            if (this._securityService.IsPathSafe(path))
            {
                DirectoryInfo dirInfo = new DirectoryInfo(path);

                if (dirInfo.Exists)
                {
                    try
                    {
                        DirectoryInfo[] directories = dirInfo.GetDirectories();
                        Array.Sort(directories, (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

                        for (int i = 0; i < directories.Length; i++)
                        {
                            result.Add(new FileSystemEntry(directories[i]));
                        }
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // Skip directories we cannot access
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Gets available drive roots or mount points
        /// </summary>
        /// <returns>List of root entries</returns>
        public List<FileSystemEntry> GetDriveRoots()
        {
            List<FileSystemEntry> result = new List<FileSystemEntry>();
            DriveInfo[] drives = DriveInfo.GetDrives();

            for (int i = 0; i < drives.Length; i++)
            {
                if (drives[i].IsReady)
                {
                    DirectoryInfo rootDir = drives[i].RootDirectory;
                    FileSystemEntry entry = new FileSystemEntry(rootDir);
                    // Override name with drive label for readability
                    entry.Name = drives[i].Name;
                    result.Add(entry);
                }
            }

            return result;
        }

        /// <summary>
        /// Gets detailed info for a single entry
        /// </summary>
        /// <param name="path">File or directory path</param>
        /// <returns>Entry details, or empty entry if not found</returns>
        public FileSystemEntry GetEntryInfo(string path)
        {
            FileSystemEntry result = new FileSystemEntry();

            if (this._securityService.IsPathSafe(path))
            {
                if (Directory.Exists(path))
                {
                    DirectoryInfo dirInfo = new DirectoryInfo(path);
                    result = new FileSystemEntry(dirInfo);
                }
                else if (File.Exists(path))
                {
                    FileInfo fileInfo = new FileInfo(path);
                    result = new FileSystemEntry(fileInfo);
                }
            }

            return result;
        }

        /// <summary>
        /// Gets available free disk space for the drive containing the path
        /// </summary>
        /// <param name="path">Any path on the target drive</param>
        /// <returns>Free space in bytes, or 0 if unavailable</returns>
        public long GetAvailableDiskSpace(string path)
        {
            long result = 0;

            if (this._securityService.IsPathSafe(path))
            {
                DriveInfo driveInfo = this.GetDriveForPath(path);
                if (driveInfo != null && driveInfo.IsReady)
                {
                    result = driveInfo.AvailableFreeSpace;
                }
            }

            return result;
        }

        /// <summary>
        /// Gets total disk space for the drive containing the path
        /// </summary>
        /// <param name="path">Any path on the target drive</param>
        /// <returns>Total space in bytes, or 0 if unavailable</returns>
        public long GetTotalDiskSpace(string path)
        {
            long result = 0;

            if (this._securityService.IsPathSafe(path))
            {
                DriveInfo driveInfo = this.GetDriveForPath(path);
                if (driveInfo != null && driveInfo.IsReady)
                {
                    result = driveInfo.TotalSize;
                }
            }

            return result;
        }

        /// <summary>
        /// Gets the parent directory path
        /// </summary>
        /// <param name="path">Current path</param>
        /// <returns>Parent path, or empty string if at root</returns>
        public string GetParentPath(string path)
        {
            string result = "";

            if (this._securityService.IsPathSafe(path))
            {
                DirectoryInfo dirInfo = new DirectoryInfo(path);
                if (dirInfo.Parent != null)
                {
                    result = dirInfo.Parent.FullName;
                }
            }

            return result;
        }

        /// <summary>
        /// Calculates the total size of a directory recursively
        /// </summary>
        /// <param name="path">Directory path</param>
        /// <param name="fileCount">Output: number of files found</param>
        /// <param name="dirCount">Output: number of subdirectories found</param>
        /// <returns>Total size in bytes</returns>
        public long CalculateDirectorySize(string path, out int fileCount, out int dirCount)
        {
            long totalSize = 0;
            fileCount = 0;
            dirCount = 0;

            if (this._securityService.IsPathSafe(path))
            {
                DirectoryInfo dirInfo = new DirectoryInfo(path);

                if (dirInfo.Exists)
                {
                    this.CalculateDirectorySizeRecursive(dirInfo, ref totalSize, ref fileCount, ref dirCount);
                }
            }

            return totalSize;
        }

        /// <summary>
        /// Gets subdirectories matching a name prefix (for autocomplete)
        /// </summary>
        /// <param name="basePath">Parent directory to search in</param>
        /// <param name="prefix">Name prefix to match (case-insensitive)</param>
        /// <returns>List of matching full paths</returns>
        public List<string> GetMatchingDirectories(string basePath, string prefix)
        {
            List<string> result = new List<string>();

            if (this._securityService.IsPathSafe(basePath))
            {
                DirectoryInfo dirInfo = new DirectoryInfo(basePath);

                if (dirInfo.Exists)
                {
                    try
                    {
                        DirectoryInfo[] directories = dirInfo.GetDirectories();

                        for (int i = 0; i < directories.Length; i++)
                        {
                            if (string.IsNullOrEmpty(prefix) || directories[i].Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                            {
                                result.Add(directories[i].Name);
                            }
                        }

                        result.Sort(StringComparer.OrdinalIgnoreCase);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // Skip inaccessible directories
                    }
                }
            }

            return result;
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Recursively calculates directory size
        /// </summary>
        /// <param name="dirInfo">Directory to scan</param>
        /// <param name="totalSize">Running total of bytes</param>
        /// <param name="fileCount">Running count of files</param>
        /// <param name="dirCount">Running count of subdirectories</param>
        private void CalculateDirectorySizeRecursive(DirectoryInfo dirInfo, ref long totalSize, ref int fileCount, ref int dirCount)
        {
            // Sum file sizes
            try
            {
                FileInfo[] files = dirInfo.GetFiles();
                for (int i = 0; i < files.Length; i++)
                {
                    totalSize += files[i].Length;
                    fileCount++;
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Skip inaccessible files
            }

            // Recurse into subdirectories
            try
            {
                DirectoryInfo[] subdirs = dirInfo.GetDirectories();
                for (int i = 0; i < subdirs.Length; i++)
                {
                    dirCount++;
                    this.CalculateDirectorySizeRecursive(subdirs[i], ref totalSize, ref fileCount, ref dirCount);
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Skip inaccessible directories
            }
        }

        /// <summary>
        /// Gets the DriveInfo for the drive containing the specified path
        /// </summary>
        /// <param name="path">Path on the target drive</param>
        /// <returns>DriveInfo or null if not found</returns>
        private DriveInfo GetDriveForPath(string path)
        {
            DriveInfo result = null;
            string fullPath = Path.GetFullPath(path);
            string pathRoot = Path.GetPathRoot(fullPath);
            DriveInfo[] drives = DriveInfo.GetDrives();

            for (int i = 0; i < drives.Length; i++)
            {
                if (string.Equals(drives[i].Name, pathRoot, StringComparison.OrdinalIgnoreCase))
                {
                    result = drives[i];
                    break;
                }
            }

            return result;
        }

        #endregion
    }
}
