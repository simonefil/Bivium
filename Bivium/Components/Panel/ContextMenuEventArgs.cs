using Bivium.Models;

namespace Bivium.Components.Panel
{
    /// <summary>
    /// Data of a context menu request
    /// </summary>
    public class ContextMenuEventArgs
    {
        /// <summary>
        /// Horizontal pointer coordinate
        /// </summary>
        public double X { get; set; }

        /// <summary>
        /// Vertical pointer coordinate
        /// </summary>
        public double Y { get; set; }

        /// <summary>
        /// Entry associated with the request, or null for the background
        /// </summary>
        public FileSystemEntry Entry { get; set; }
    }
}
