using System.Collections.Immutable;

namespace Bivium.Models
{
    /// <summary>Visual state of the path editor; no navigation or callback in the draft</summary>
    public sealed record WorkspacePanelPathDraft(long Revision, string BasePath, bool Editing, string Text, ImmutableArray<string> Candidates, ImmutableArray<string> Matches, int MatchIndex, string ParentDirectory, string CycleValue, int SelectionStart = 0, int SelectionEnd = 0, bool Focused = true);

    /// <summary>
    /// Identifies one known Details-view column
    /// </summary>
    public enum FileListColumnId
    {
        /// <summary>
        /// Entry name
        /// </summary>
        Name,

        /// <summary>
        /// Entry size
        /// </summary>
        Size,

        /// <summary>
        /// Last-modified date
        /// </summary>
        Date,

        /// <summary>
        /// Filesystem attributes
        /// </summary>
        Attributes,

        /// <summary>
        /// Filesystem owner
        /// </summary>
        Owner
    }

    /// <summary>
    /// Mutable runtime state of one Details-view column
    /// </summary>
    public sealed class FileListColumnState
    {
        /// <summary>
        /// Creates a column state
        /// </summary>
        /// <param name="id">Stable column identity</param>
        /// <param name="width">Measured width in pixels</param>
        /// <param name="visible">Whether the column is visible</param>
        public FileListColumnState(FileListColumnId id, double width, bool visible)
        {
            this.Id = id;
            this.Width = width;
            this.Visible = visible;
        }

        /// <summary>
        /// Stable column identity
        /// </summary>
        public FileListColumnId Id { get; set; }

        /// <summary>
        /// Measured width in pixels
        /// </summary>
        public double Width { get; set; }

        /// <summary>
        /// Whether the column is visible
        /// </summary>
        public bool Visible { get; set; }
    }

    /// <summary>
    /// Atomic file-list interaction understood by Commander
    /// </summary>
    public enum FileListInteractionIntent
    {
        /// <summary>
        /// Click a semantic entry path
        /// </summary>
        Click,

        /// <summary>
        /// Move focus to the previous entry
        /// </summary>
        MovePrevious,

        /// <summary>
        /// Move focus to the next entry
        /// </summary>
        MoveNext,

        /// <summary>
        /// Move focus to the first entry
        /// </summary>
        MoveFirst,

        /// <summary>
        /// Move focus to the last entry
        /// </summary>
        MoveLast,

        /// <summary>
        /// Move focus one visible page backward
        /// </summary>
        MovePagePrevious,

        /// <summary>
        /// Move focus one visible page forward
        /// </summary>
        MovePageNext,

        /// <summary>
        /// Toggle selection of the focused entry
        /// </summary>
        ToggleFocused,

        /// <summary>
        /// Select every visible entry
        /// </summary>
        SelectAll,

        /// <summary>
        /// Clear selection without moving focus
        /// </summary>
        ClearSelection,

        /// <summary>
        /// Activate the focused entry
        /// </summary>
        ActivateFocused,

        /// <summary>
        /// Moves the focus without changing the selection
        /// </summary>
        Focus
    }

    /// <summary>
    /// Complete request for one atomic Commander-owned file-list interaction
    /// </summary>
    public sealed class FileListInteractionRequest
    {
        /// <summary>
        /// Interaction kind
        /// </summary>
        public FileListInteractionIntent Intent { get; set; }

        /// <summary>
        /// Semantic target path used by click interactions
        /// </summary>
        public string TargetPath { get; set; } = "";

        /// <summary>
        /// Whether existing selection is preserved or unioned
        /// </summary>
        public bool Control { get; set; }

        /// <summary>
        /// Whether selection extends from the stable anchor
        /// </summary>
        public bool Shift { get; set; }

        /// <summary>
        /// Visible row count used by page movements
        /// </summary>
        public int PageSize { get; set; }
    }

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
        /// Semantic full path holding file-list focus independently from selection
        /// </summary>
        public string FocusedPath { get; set; } = "";

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
        /// Previous directory paths, with the nearest destination last
        /// </summary>
        public List<string> BackHistory { get; set; } = new List<string>();

        /// <summary>
        /// Forward directory paths, with the nearest destination last
        /// </summary>
        public List<string> ForwardHistory { get; set; } = new List<string>();

        /// <summary>
        /// Authoritative ordered Details-view columns, or empty until a legacy layout is measured
        /// </summary>
        public List<FileListColumnState> Columns { get; set; } = new List<FileListColumnState>();

        /// <summary>
        /// Monotonic transient request observed by FilePanel to enter path editing
        /// </summary>
        public long PathEditRequestVersion { get; set; }

        /// <summary>
        /// Vertical percentage occupied by the tree in the Radzen variant
        /// </summary>
        public double TreeSizePercent { get; set; } = 30;

        /// <summary>
        /// Whether the tree of the Radzen variant is collapsed
        /// </summary>
        public bool TreeCollapsed { get; set; }

        /// <summary>
        /// Commander-owned reducer callback used by file-list UI interactions
        /// </summary>
        public Action<FileListInteractionRequest> InteractionRequested { get; set; }

        /// <summary>
        /// Commander-owned callback accepting a complete measured column layout
        /// </summary>
        public Action<IEnumerable<FileListColumnState>> ColumnLayoutRequested { get; set; }

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
