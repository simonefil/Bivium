namespace Bivium.Components.Tree
{
    /// <summary>
    /// Descrive una modifica semantica dello stato di espansione dell'albero
    /// </summary>
    public sealed class DirectoryTreeExpansionChange
    {
        /// <summary>
        /// Percorso completo della directory
        /// </summary>
        public string Path { get; set; } = "";

        /// <summary>
        /// Indica se il nodo è espanso
        /// </summary>
        public bool Expanded { get; set; }
    }
}
