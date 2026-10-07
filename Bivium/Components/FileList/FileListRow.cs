using Bivium.Models;

namespace Bivium.Components.FileList
{
    /// <summary>
    /// Visual-only row type of the file grid
    /// </summary>
    internal enum FileListRowKind
    {
        /// <summary>
        /// Navigation to the parent, excluded from domain interactions
        /// </summary>
        Parent,

        /// <summary>
        /// Real entry in the authoritative state
        /// </summary>
        Entry
    }

    /// <summary>
    /// Immutable UI adapter, not serialized nor inserted into PanelState.Entries
    /// </summary>
    internal sealed class FileListRow
    {
        /// <summary>
        /// Creates the visual projection of an entry or of the navigation to the parent
        /// </summary>
        /// <param name="kind">Row type</param>
        /// <param name="entry">Real entry, null for the parent</param>
        /// <param name="parentPath">Destination for the parent row only</param>
        public FileListRow(FileListRowKind kind, FileSystemEntry entry, string parentPath)
        {
            this.Kind = kind;
            this.Entry = entry;
            this.ParentPath = parentPath;
        }

        /// <summary>
        /// Visual row type
        /// </summary>
        public FileListRowKind Kind { get; }

        /// <summary>
        /// Reference to the real entry without copying the domain
        /// </summary>
        public FileSystemEntry Entry { get; }

        /// <summary>
        /// Parent navigation destination
        /// </summary>
        public string ParentPath { get; }
    }
}
