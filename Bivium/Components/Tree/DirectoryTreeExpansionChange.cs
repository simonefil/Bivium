namespace Bivium.Components.Tree
{
    /// <summary>
    /// Describes a semantic change of the tree expansion state
    /// </summary>
    public sealed class DirectoryTreeExpansionChange
    {
        /// <summary>
        /// Full path of the directory
        /// </summary>
        public string Path { get; set; } = "";

        /// <summary>
        /// Indicates whether the node is expanded
        /// </summary>
        public bool Expanded { get; set; }
    }
}
