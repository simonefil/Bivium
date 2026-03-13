using Bivium.Models;

namespace Bivium.Services
{
    /// <summary>
    /// Interface for filesystem read operations
    /// </summary>
    public interface IFileSystemService
    {
        /// <summary>
        /// Gets all entries (files and directories) in a directory
        /// </summary>
        /// <param name="path">Directory path</param>
        /// <returns>List of entries, directories first then files</returns>
        List<FileSystemEntry> GetDirectoryContents(string path);

        /// <summary>
        /// Gets only subdirectories in a directory (for tree view)
        /// </summary>
        /// <param name="path">Directory path</param>
        /// <returns>List of directory entries</returns>
        List<FileSystemEntry> GetSubDirectories(string path);

        /// <summary>
        /// Gets available drive roots or mount points
        /// </summary>
        /// <returns>List of root entries</returns>
        List<FileSystemEntry> GetDriveRoots();

        /// <summary>
        /// Gets detailed info for a single entry
        /// </summary>
        /// <param name="path">File or directory path</param>
        /// <returns>Entry details</returns>
        FileSystemEntry GetEntryInfo(string path);

        /// <summary>
        /// Gets available free disk space for the drive containing the path
        /// </summary>
        /// <param name="path">Any path on the target drive</param>
        /// <returns>Free space in bytes</returns>
        long GetAvailableDiskSpace(string path);

        /// <summary>
        /// Gets total disk space for the drive containing the path
        /// </summary>
        /// <param name="path">Any path on the target drive</param>
        /// <returns>Total space in bytes</returns>
        long GetTotalDiskSpace(string path);

        /// <summary>
        /// Gets the parent directory path
        /// </summary>
        /// <param name="path">Current path</param>
        /// <returns>Parent path, or empty string if at root</returns>
        string GetParentPath(string path);

        /// <summary>
        /// Gets subdirectories matching a name prefix (for autocomplete)
        /// </summary>
        /// <param name="basePath">Parent directory to search in</param>
        /// <param name="prefix">Name prefix to match (case-insensitive)</param>
        /// <returns>List of matching directory names</returns>
        List<string> GetMatchingDirectories(string basePath, string prefix);

        /// <summary>
        /// Calculates the total size of a directory recursively
        /// </summary>
        /// <param name="path">Directory path</param>
        /// <param name="fileCount">Output: number of files found</param>
        /// <param name="dirCount">Output: number of subdirectories found</param>
        /// <returns>Total size in bytes</returns>
        long CalculateDirectorySize(string path, out int fileCount, out int dirCount);
    }
}
