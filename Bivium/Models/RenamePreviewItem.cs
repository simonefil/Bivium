namespace Bivium.Models
{
    /// <summary>
    /// Represents a single file in the rename preview table
    /// </summary>
    public class RenamePreviewItem
    {
        /// <summary>
        /// Original filename (display name only)
        /// </summary>
        public string OriginalName { get; set; } = "";

        /// <summary>
        /// Full path of the original file
        /// </summary>
        public string OriginalFullPath { get; set; } = "";

        /// <summary>
        /// Computed new filename after all methods applied
        /// </summary>
        public string NewName { get; set; } = "";

        /// <summary>
        /// True if another item produces the same new name
        /// </summary>
        public bool HasConflict { get; set; } = false;

        /// <summary>
        /// True if the new name contains invalid filename characters
        /// </summary>
        public bool HasError { get; set; } = false;
    }
}
