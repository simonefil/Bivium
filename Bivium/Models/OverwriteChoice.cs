namespace Bivium.Models
{
    /// <summary>
    /// User choice when a paste operation would overwrite an existing file
    /// </summary>
    public enum OverwriteChoice
    {
        /// <summary>
        /// Overwrite the current file
        /// </summary>
        Yes,

        /// <summary>
        /// Overwrite the current file and all remaining conflicts
        /// </summary>
        YesToAll,

        /// <summary>
        /// Skip the current file
        /// </summary>
        No
    }
}
