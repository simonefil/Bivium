namespace Bivium.Models
{
    /// <summary>
    /// Holds the runtime state of one file panel
    /// </summary>
    public class PanelState
    {
        #region Properties

        /// <summary>
        /// Current directory path being displayed
        /// </summary>
        public string CurrentPath { get; set; } = "";

        /// <summary>
        /// List of entries in the current directory
        /// </summary>
        public List<FileSystemEntry> Entries { get; set; } = new List<FileSystemEntry>();

        /// <summary>
        /// List of selected entry full paths
        /// </summary>
        public List<string> SelectedPaths { get; set; } = new List<string>();

        /// <summary>
        /// Full path used as the stable selection range anchor
        /// </summary>
        public string SelectionAnchorPath { get; set; } = "";

        /// <summary>
        /// Current sort configuration
        /// </summary>
        public SortColumn CurrentSort { get; set; } = new SortColumn();

        /// <summary>
        /// Index of the cursor row for keyboard navigation
        /// </summary>
        public int CursorIndex { get; set; } = 0;

        /// <summary>
        /// Full path of the first visible row used to restore list scrolling
        /// </summary>
        public string ScrollAnchorPath { get; set; } = "";

        /// <summary>
        /// Semantic full paths of expanded directory-tree nodes
        /// </summary>
        public HashSet<string> ExpandedDirectoryPaths { get; set; } = new HashSet<string>();

        /// <summary>
        /// Normalized width ratio of the name column, or null for the legacy layout
        /// </summary>
        public double? NameColumnRatio { get; set; }

        /// <summary>
        /// Normalized width ratio of the size column, or null for the legacy layout
        /// </summary>
        public double? SizeColumnRatio { get; set; }

        /// <summary>
        /// Normalized width ratio of the date column, or null for the legacy layout
        /// </summary>
        public double? DateColumnRatio { get; set; }

        /// <summary>
        /// Normalized width ratio of the attributes column, or null for the legacy layout
        /// </summary>
        public double? AttributesColumnRatio { get; set; }

        /// <summary>
        /// Normalized width ratio of the owner column, or null for the legacy layout
        /// </summary>
        public double? OwnerColumnRatio { get; set; }

        #endregion

        #region Constructor

        /// <summary>
        /// Default constructor
        /// </summary>
        public PanelState()
        {
        }

        /// <summary>
        /// Creates a PanelState with the specified initial path
        /// </summary>
        /// <param name="path">Initial directory path</param>
        public PanelState(string path)
        {
            this.CurrentPath = path;
        }

        #endregion
    }
}
