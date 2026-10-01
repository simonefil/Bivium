namespace Bivium.Models
{
    /// <summary>
    /// Source entry and optional explicit mode for an internal transfer
    /// </summary>
    public class FileTransferEntry
    {
        #region Properties

        /// <summary>
        /// Server-side source path
        /// </summary>
        public string SourcePath { get; set; } = "";

        /// <summary>
        /// Explicit mode, or null to resolve it from the physical volumes
        /// </summary>
        public FileTransferMode? Mode { get; set; }

        #endregion

        #region Constructor

        /// <summary>
        /// Creates an empty transfer entry
        /// </summary>
        public FileTransferEntry()
        {
        }

        /// <summary>
        /// Creates a transfer entry for a server-side path
        /// </summary>
        /// <param name="sourcePath">Server-side source path</param>
        /// <param name="mode">Explicit mode, or null for automatic resolution</param>
        public FileTransferEntry(string sourcePath, FileTransferMode? mode = null)
        {
            this.SourcePath = sourcePath;
            this.Mode = mode;
        }

        #endregion
    }
}
