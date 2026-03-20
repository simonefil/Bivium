namespace Bivium.Models
{
    /// <summary>
    /// Available rename method types for the Advanced Renamer
    /// </summary>
    public enum RenameMethodType
    {
        /// <summary>
        /// Find and replace text in filename
        /// </summary>
        Replace,

        /// <summary>
        /// Insert text at a specific position
        /// </summary>
        Add,

        /// <summary>
        /// Remove characters by position or pattern
        /// </summary>
        Remove,

        /// <summary>
        /// Change filename capitalization
        /// </summary>
        NewCase,

        /// <summary>
        /// Replace filename with a pattern using tags
        /// </summary>
        NewName,

        /// <summary>
        /// Trim characters from filename edges
        /// </summary>
        Trim
    }
}
