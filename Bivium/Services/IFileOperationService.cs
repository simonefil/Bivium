using Bivium.Models;

namespace Bivium.Services
{
    /// <summary>
    /// Interface for file manipulation operations
    /// </summary>
    public interface IFileOperationService
    {
        /// <summary>
        /// Copies files and directories to a destination
        /// </summary>
        /// <param name="sourcePaths">List of source file/directory paths</param>
        /// <param name="destinationDir">Destination directory path</param>
        /// <returns>Operation result</returns>
        FileOperationResult CopyEntries(List<string> sourcePaths, string destinationDir);

        /// <summary>
        /// Copies files and directories to a destination with progress reporting
        /// </summary>
        /// <param name="sourcePaths">List of source file/directory paths</param>
        /// <param name="destinationDir">Destination directory path</param>
        /// <param name="onProgress">Callback invoked after each file (currentFile, totalFiles, currentFileName)</param>
        /// <returns>Operation result</returns>
        FileOperationResult CopyEntriesWithProgress(List<string> sourcePaths, string destinationDir, Action<int, int, string> onProgress);

        /// <summary>
        /// Moves files and directories to a destination
        /// </summary>
        /// <param name="sourcePaths">List of source file/directory paths</param>
        /// <param name="destinationDir">Destination directory path</param>
        /// <returns>Operation result</returns>
        FileOperationResult MoveEntries(List<string> sourcePaths, string destinationDir);

        /// <summary>
        /// Moves files and directories to a destination with progress reporting
        /// </summary>
        /// <param name="sourcePaths">List of source file/directory paths</param>
        /// <param name="destinationDir">Destination directory path</param>
        /// <param name="onProgress">Callback invoked after each entry (currentEntry, totalEntries, currentEntryName)</param>
        /// <returns>Operation result</returns>
        FileOperationResult MoveEntriesWithProgress(List<string> sourcePaths, string destinationDir, Action<int, int, string> onProgress);

        /// <summary>
        /// Deletes files and directories
        /// </summary>
        /// <param name="paths">List of paths to delete</param>
        /// <returns>Operation result</returns>
        FileOperationResult DeleteEntries(List<string> paths);

        /// <summary>
        /// Renames a file or directory
        /// </summary>
        /// <param name="path">Current path</param>
        /// <param name="newName">New name (not full path, just the name)</param>
        /// <returns>Operation result</returns>
        FileOperationResult RenameEntry(string path, string newName);

        /// <summary>
        /// Creates a new directory
        /// </summary>
        /// <param name="parentPath">Parent directory path</param>
        /// <param name="name">Name for the new directory</param>
        /// <returns>Operation result</returns>
        FileOperationResult CreateDirectory(string parentPath, string name);

        /// <summary>
        /// Creates a new empty file
        /// </summary>
        /// <param name="parentPath">Parent directory path</param>
        /// <param name="name">File name</param>
        /// <returns>Operation result</returns>
        FileOperationResult CreateFile(string parentPath, string name);

        /// <summary>
        /// Reads text content from a file
        /// </summary>
        /// <param name="path">File path</param>
        /// <param name="maxSizeBytes">Maximum allowed file size in bytes</param>
        /// <returns>File content, or empty string on failure</returns>
        string ReadFileText(string path, long maxSizeBytes);

        /// <summary>
        /// Writes text content to a file
        /// </summary>
        /// <param name="path">File path</param>
        /// <param name="content">Text content to write</param>
        /// <returns>Operation result</returns>
        FileOperationResult WriteFileText(string path, string content);
    }
}
