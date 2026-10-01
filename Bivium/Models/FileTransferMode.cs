namespace Bivium.Models
{
    /// <summary>
    /// Transfer mode for an internal file operation
    /// </summary>
    public enum FileTransferMode
    {
        /// <summary>
        /// Copies the source and keeps it in place
        /// </summary>
        Copy,

        /// <summary>
        /// Moves the source to the destination
        /// </summary>
        Move
    }
}
