using Bivium.Models;

namespace Bivium.Services
{
    /// <summary>
    /// Interface for archive compression and extraction operations
    /// </summary>
    public interface IArchiveService
    {
        /// <summary>
        /// Extracts an archive to a destination directory
        /// </summary>
        /// <param name="archivePath">Path to the archive file</param>
        /// <param name="destinationDir">Directory to extract into</param>
        /// <param name="onProgress">Progress callback (current, total, currentFileName)</param>
        /// <returns>Operation result</returns>
        FileOperationResult ExtractArchive(string archivePath, string destinationDir, Action<int, int, string> onProgress);

        /// <summary>
        /// Creates an archive from a list of files and directories
        /// </summary>
        /// <param name="outputPath">Output archive file path</param>
        /// <param name="sourcePaths">List of file/directory paths to compress</param>
        /// <param name="format">Archive format to create</param>
        /// <param name="onProgress">Progress callback (current, total, currentFileName)</param>
        /// <returns>Operation result</returns>
        FileOperationResult CreateArchive(string outputPath, List<string> sourcePaths, ArchiveFormat format, Action<int, int, string> onProgress);

        /// <summary>
        /// Checks if a file path has a supported archive extension
        /// </summary>
        /// <param name="path">File path to check</param>
        /// <returns>True if the file is a supported archive</returns>
        bool IsArchive(string path);

        /// <summary>
        /// Gets the default file extension for an archive format
        /// </summary>
        /// <param name="format">Archive format</param>
        /// <returns>File extension including dot (e.g. ".zip")</returns>
        string GetExtension(ArchiveFormat format);
    }
}
