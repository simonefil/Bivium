using Bivium.Models;

namespace Bivium.Components.FileList
{
    /// <summary>
    /// Tipo di riga esclusivamente visuale della griglia file
    /// </summary>
    internal enum FileListRowKind
    {
        /// <summary>
        /// Navigazione al padre, esclusa dalle interazioni di dominio
        /// </summary>
        Parent,

        /// <summary>
        /// Entry reale nello stato autoritativo
        /// </summary>
        Entry
    }

    /// <summary>
    /// Adapter UI immutabile, non serializzato né inserito in PanelState.Entries
    /// </summary>
    internal sealed class FileListRow
    {
        /// <summary>
        /// Crea la proiezione visuale di una entry o della navigazione al padre
        /// </summary>
        /// <param name="kind">Tipo della riga</param>
        /// <param name="entry">Entry reale, null per il padre</param>
        /// <param name="parentPath">Destinazione soltanto per la riga padre</param>
        public FileListRow(FileListRowKind kind, FileSystemEntry entry, string parentPath)
        {
            this.Kind = kind;
            this.Entry = entry;
            this.ParentPath = parentPath;
        }

        /// <summary>
        /// Tipo della riga visuale
        /// </summary>
        public FileListRowKind Kind { get; }

        /// <summary>
        /// Riferimento all'entry reale senza copia del dominio
        /// </summary>
        public FileSystemEntry Entry { get; }

        /// <summary>
        /// Destinazione della navigazione al padre
        /// </summary>
        public string ParentPath { get; }
    }
}
