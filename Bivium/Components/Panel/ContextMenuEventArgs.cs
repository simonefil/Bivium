using Bivium.Models;

namespace Bivium.Components.Panel
{
    /// <summary>
    /// Dati di una richiesta di menu contestuale
    /// </summary>
    public class ContextMenuEventArgs
    {
        /// <summary>
        /// Coordinata orizzontale del puntatore
        /// </summary>
        public double X { get; set; }

        /// <summary>
        /// Coordinata verticale del puntatore
        /// </summary>
        public double Y { get; set; }

        /// <summary>
        /// Entry associata alla richiesta, oppure null per lo sfondo
        /// </summary>
        public FileSystemEntry Entry { get; set; }
    }
}
