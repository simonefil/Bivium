using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Bivium.Models
{
    /// <summary>
    /// Immutable Bivium workspace snapshot
    /// </summary>
    public sealed record BiviumWorkspaceSnapshot
    {
        #region Constructor

        /// <summary>
        /// Creates a snapshot of the workspace
        /// </summary>
        /// <param name="revision">Monotonic state revision</param>
        /// <param name="panels">Persistent panel state or null when not initialized yet</param>
        /// <param name="floatingWindows">Persistent floating-window state</param>
        /// <param name="activeClientLease">Active client lease or null</param>
        public BiviumWorkspaceSnapshot(long revision, WorkspacePanelsSnapshot panels, FloatingWindowsSnapshot floatingWindows, ActiveClientLeaseSnapshot activeClientLease)
        {
            this.Revision = revision;
            this.Panels = panels;
            this.FloatingWindows = floatingWindows ?? new FloatingWindowsSnapshot();
            this.ActiveClientLease = activeClientLease;
        }

        #endregion

        #region Properties

        /// <summary>
        /// Monotonic state revision
        /// </summary>
        public long Revision { get; }

        /// <summary>
        /// Persistent panel state or null when not initialized yet
        /// </summary>
        public WorkspacePanelsSnapshot Panels { get; }

        /// <summary>
        /// Persistent floating-window state
        /// </summary>
        public FloatingWindowsSnapshot FloatingWindows { get; }

        /// <summary>
        /// Active client lease or null
        /// </summary>
        public ActiveClientLeaseSnapshot ActiveClientLease { get; }

        #endregion
    }

    /// <summary>
    /// Token presented by each mutating operation
    /// </summary>
    /// <param name="AttachmentId">Attachment identifier</param>
    /// <param name="Generation">Generation of the lease</param>
    public sealed record WorkspaceClientToken(string AttachmentId, long Generation);

    /// <summary>
    /// Aggregate workspace metrics without sensitive client data
    /// </summary>
    public sealed class WorkspaceRuntimeMetrics
    {
        /// <summary>
        /// Number of registered attachments
        /// </summary>
        public int AttachmentCount { get; set; }

        /// <summary>
        /// Number of registered workspace subscribers
        /// </summary>
        public int SubscriberCount { get; set; }

        /// <summary>
        /// Whether an active lease exists
        /// </summary>
        public bool HasActiveLease { get; set; }

        /// <summary>
        /// Current workspace revision
        /// </summary>
        public long Revision { get; set; }
    }

    /// <summary>
    /// Exclusive lease of the active browser
    /// </summary>
    public sealed record ActiveClientLeaseSnapshot
    {
        /// <summary>
        /// Creates the snapshot of a lease client
        /// </summary>
        /// <param name="attachmentId">Owning attachment identifier</param>
        /// <param name="generation">Monotonic lease generation</param>
        /// <param name="remoteIp">IP address observed by the server</param>
        /// <param name="clientLabel">Short client label</param>
        /// <param name="connectedAtUtc">Connection UTC instant</param>
        /// <param name="lastActivityUtc">Last-activity UTC instant</param>
        /// <param name="connected">Whether the browser is connected</param>
        public ActiveClientLeaseSnapshot(string attachmentId, long generation, string remoteIp, string clientLabel, DateTime connectedAtUtc, DateTime lastActivityUtc, bool connected = true)
        {
            this.AttachmentId = attachmentId ?? "";
            this.Generation = generation;
            this.RemoteIp = remoteIp ?? "";
            this.ClientLabel = clientLabel ?? "";
            this.ConnectedAtUtc = connectedAtUtc;
            this.LastActivityUtc = lastActivityUtc;
            this.Connected = connected;
        }

        /// <summary>
        /// Owning attachment identifier
        /// </summary>
        public string AttachmentId { get; }

        /// <summary>
        /// Monotonic lease generation
        /// </summary>
        public long Generation { get; }

        /// <summary>
        /// IP address observed by the server
        /// </summary>
        public string RemoteIp { get; }

        /// <summary>
        /// Short client label
        /// </summary>
        public string ClientLabel { get; }

        /// <summary>
        /// Connection UTC instant
        /// </summary>
        public DateTime ConnectedAtUtc { get; }

        /// <summary>
        /// Last-activity UTC instant
        /// </summary>
        public DateTime LastActivityUtc { get; }

        /// <summary>
        /// Whether the browser is connected
        /// </summary>
        public bool Connected { get; }
    }

    /// <summary>
    /// Client attachment or takeover result
    /// </summary>
    public sealed class WorkspaceAttachResult
    {
        /// <summary>
        /// Identifier assigned to the attachment
        /// </summary>
        public string AttachmentId { get; set; } = "";

        /// <summary>
        /// Whether the attachment owns control
        /// </summary>
        public bool HasControl { get; set; }

        /// <summary>
        /// Whether takeover confirmation is required
        /// </summary>
        public bool RequiresTakeover { get; set; }

        /// <summary>
        /// Currently authoritative lease
        /// </summary>
        public ActiveClientLeaseSnapshot ActiveLease { get; set; }

        /// <summary>
        /// Workspace revision observed during the operation
        /// </summary>
        public long WorkspaceRevision { get; set; }
    }

    /// <summary>
    /// Immutable snapshot of persistent floating windows
    /// </summary>
    public sealed record FloatingWindowsSnapshot
    {
        /// <summary>
        /// Creates the initial window state
        /// </summary>
        public FloatingWindowsSnapshot()
            : this(new FloatingWindowSnapshot(false, false, 100, 80, 800, 400, 0, 0, 0, "terminal-input"))
        {
        }

        /// <summary>
        /// Creates the window state
        /// </summary>
        /// <param name="terminal">Terminal window state</param>
        public FloatingWindowsSnapshot(FloatingWindowSnapshot terminal)
        {
            this.Terminal = terminal ?? throw new System.ArgumentNullException(nameof(terminal));
        }

        /// <summary>
        /// Terminal window state
        /// </summary>
        public FloatingWindowSnapshot Terminal { get; }
    }

    /// <summary>
    /// Immutable floating-window snapshot
    /// </summary>
    public sealed record FloatingWindowSnapshot
    {
        /// <summary>
        /// Creates the snapshot of a window
        /// </summary>
        /// <param name="visible">Whether the window is visible</param>
        /// <param name="minimized">Whether the window is minimized</param>
        /// <param name="left">Horizontal coordinate</param>
        /// <param name="top">Vertical coordinate</param>
        /// <param name="width">Window width</param>
        /// <param name="height">Window height</param>
        /// <param name="viewportWidth">Source viewport width</param>
        /// <param name="viewportHeight">Source viewport height</param>
        /// <param name="mruOrder">Logical MRU order</param>
        /// <param name="focusTarget">Semantic focus target</param>
        public FloatingWindowSnapshot(bool visible, bool minimized, double left, double top, double width, double height, double viewportWidth, double viewportHeight, long mruOrder, string focusTarget)
        {
            this.Visible = visible;
            this.Minimized = minimized;
            this.Left = left;
            this.Top = top;
            this.Width = width;
            this.Height = height;
            this.ViewportWidth = viewportWidth;
            this.ViewportHeight = viewportHeight;
            this.MruOrder = mruOrder;
            this.FocusTarget = focusTarget ?? "";
        }

        /// <summary>
        /// Whether the window is visible
        /// </summary>
        public bool Visible { get; }

        /// <summary>
        /// Whether the window is minimized
        /// </summary>
        public bool Minimized { get; }

        /// <summary>
        /// Horizontal coordinate
        /// </summary>
        public double Left { get; }

        /// <summary>
        /// Vertical coordinate
        /// </summary>
        public double Top { get; }

        /// <summary>
        /// Window width
        /// </summary>
        public double Width { get; }

        /// <summary>
        /// Window height
        /// </summary>
        public double Height { get; }

        /// <summary>
        /// Source viewport width
        /// </summary>
        public double ViewportWidth { get; }

        /// <summary>
        /// Source viewport height
        /// </summary>
        public double ViewportHeight { get; }

        /// <summary>
        /// Logical MRU order
        /// </summary>
        public long MruOrder { get; }

        /// <summary>
        /// Semantic focus target
        /// </summary>
        public string FocusTarget { get; }
    }

    /// <summary>
    /// Immutable commander panel snapshot
    /// </summary>
    public sealed record WorkspacePanelsSnapshot
    {
        #region Constructor

        /// <summary>
        /// Creates a panel snapshot
        /// </summary>
        /// <param name="leftPanel">Left panel state</param>
        /// <param name="rightPanel">Right panel state</param>
        /// <param name="activePanel">Active panel index</param>
        /// <param name="singlePanelMode">Whether single-panel mode is active</param>
        /// <param name="outerSizePercent">Percentuale occupata dal pannello sinistro</param>
        /// <param name="collapsedPanelIndex">Indice del pannello compresso, oppure -1</param>
        public WorkspacePanelsSnapshot(WorkspacePanelSnapshot leftPanel, WorkspacePanelSnapshot rightPanel, int activePanel, bool singlePanelMode, double outerSizePercent = 50, int collapsedPanelIndex = -1)
        {
            this.LeftPanel = leftPanel ?? throw new System.ArgumentNullException(nameof(leftPanel));
            this.RightPanel = rightPanel ?? throw new System.ArgumentNullException(nameof(rightPanel));
            this.ActivePanel = activePanel;
            this.SinglePanelMode = singlePanelMode;
            this.OuterSizePercent = double.IsFinite(outerSizePercent) ? Math.Clamp(outerSizePercent, 20, 80) : 50;
            this.CollapsedPanelIndex = collapsedPanelIndex is 0 or 1 ? collapsedPanelIndex : -1;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Compares the persistent value of two panel snapshots
        /// </summary>
        /// <param name="other">Snapshot to compare</param>
        /// <returns>True when both snapshots represent the same state</returns>
        public bool Equals(WorkspacePanelsSnapshot other)
        {
            return other != null && this.ActivePanel == other.ActivePanel && this.SinglePanelMode == other.SinglePanelMode && this.OuterSizePercent == other.OuterSizePercent && this.CollapsedPanelIndex == other.CollapsedPanelIndex && this.LeftPanel == other.LeftPanel && this.RightPanel == other.RightPanel;
        }

        /// <summary>
        /// Returns the hash of the persistent panel value
        /// </summary>
        /// <returns>Snapshot hash</returns>
        public override int GetHashCode()
        {
            return HashCode.Combine(this.LeftPanel, this.RightPanel, this.ActivePanel, this.SinglePanelMode, this.OuterSizePercent, this.CollapsedPanelIndex);
        }

        #endregion

        #region Properties

        /// <summary>
        /// Left panel state
        /// </summary>
        public WorkspacePanelSnapshot LeftPanel { get; }

        /// <summary>
        /// Right panel state
        /// </summary>
        public WorkspacePanelSnapshot RightPanel { get; }

        /// <summary>
        /// Active panel index
        /// </summary>
        public int ActivePanel { get; }

        /// <summary>
        /// Whether single-panel mode is active
        /// </summary>
        public bool SinglePanelMode { get; }

        /// <summary>
        /// Percentuale occupata dal pannello sinistro nel layout Radzen
        /// </summary>
        public double OuterSizePercent { get; }

        /// <summary>
        /// Indice del pannello compresso nel layout Radzen, oppure -1
        /// </summary>
        public int CollapsedPanelIndex { get; }

        #endregion
    }

    /// <summary>
    /// Immutable Details-view column snapshot
    /// </summary>
    public sealed record FileListColumnSnapshot
    {
        /// <summary>
        /// Creates a column snapshot
        /// </summary>
        /// <param name="id">Stable column identity</param>
        /// <param name="width">Measured width in pixels</param>
        /// <param name="visible">Whether the column is visible</param>
        public FileListColumnSnapshot(FileListColumnId id, double width, bool visible)
        {
            this.Id = id;
            this.Width = width;
            this.Visible = visible;
        }

        /// <summary>
        /// Stable column identity
        /// </summary>
        public FileListColumnId Id { get; }

        /// <summary>
        /// Measured width in pixels
        /// </summary>
        public double Width { get; }

        /// <summary>
        /// Whether the column is visible
        /// </summary>
        public bool Visible { get; }
    }

    /// <summary>
    /// Immutable snapshot of persistent panel state
    /// </summary>
    public sealed record WorkspacePanelSnapshot
    {
        #region Constructor

        /// <summary>
        /// Creates a snapshot of persistent panel state
        /// </summary>
        /// <param name="currentPath">Current directory</param>
        /// <param name="cursorPath">Semantic path of the focused item</param>
        /// <param name="cursorIndex">Cursor fallback index</param>
        /// <param name="selectedPaths">Selected paths</param>
        /// <param name="sortField">Sort column</param>
        /// <param name="sortDirection">Sort direction</param>
        /// <param name="scrollAnchorPath">Path of the visible item used as scroll anchor</param>
        /// <param name="expandedDirectoryPaths">Paths of expanded directories in the tree</param>
        /// <param name="nameColumnRatio">Normalized name-column width</param>
        /// <param name="sizeColumnRatio">Normalized size-column width</param>
        /// <param name="dateColumnRatio">Normalized date-column width</param>
        /// <param name="attributesColumnRatio">Normalized attributes-column width</param>
        /// <param name="ownerColumnRatio">Normalized owner-column width</param>
        /// <param name="selectionAnchorPath">Stable range-selection anchor path</param>
        /// <param name="backHistory">Previous directory paths, nearest last</param>
        /// <param name="forwardHistory">Forward directory paths, nearest last</param>
        /// <param name="columns">Complete ordered measured column layout</param>
        /// <param name="treeSizePercent">Percentuale verticale occupata dall'albero</param>
        /// <param name="treeCollapsed">Indica se l'albero è compresso</param>
        public WorkspacePanelSnapshot(string currentPath, string cursorPath, int cursorIndex, IEnumerable<string> selectedPaths, SortField sortField, SortDirection sortDirection, string scrollAnchorPath = "", IEnumerable<string> expandedDirectoryPaths = null, double? nameColumnRatio = null, double? sizeColumnRatio = null, double? dateColumnRatio = null, double? attributesColumnRatio = null, double? ownerColumnRatio = null, string selectionAnchorPath = "", IEnumerable<string> backHistory = null, IEnumerable<string> forwardHistory = null, IEnumerable<FileListColumnState> columns = null, double treeSizePercent = 30, bool treeCollapsed = false)
        {
            this.CurrentPath = currentPath ?? "";
            this.FocusedPath = cursorPath ?? "";
            this.CursorIndex = Math.Max(0, cursorIndex);
            this.SelectedPaths = CopyDistinctPaths(selectedPaths, int.MaxValue);
            this.SortField = Enum.IsDefined(sortField) ? sortField : SortField.Name;
            this.SortDirection = Enum.IsDefined(sortDirection) ? sortDirection : SortDirection.Ascending;
            this.ScrollAnchorPath = scrollAnchorPath ?? "";
            this.ExpandedDirectoryPaths = CopyDistinctPaths(expandedDirectoryPaths, int.MaxValue);
            this.SelectionAnchorPath = selectionAnchorPath ?? "";
            this.BackHistory = CopyDistinctPaths(backHistory, 128);
            this.ForwardHistory = CopyDistinctPaths(forwardHistory, 128);
            this.Columns = CopyValidColumns(columns);
            this.TreeSizePercent = double.IsFinite(treeSizePercent) ? Math.Clamp(treeSizePercent, 15, 85) : 30;
            this.TreeCollapsed = treeCollapsed;
            this.SetColumnRatios(nameColumnRatio, sizeColumnRatio, dateColumnRatio, attributesColumnRatio, ownerColumnRatio);
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Compares the persistent value of two panel snapshots
        /// </summary>
        /// <param name="other">Snapshot to compare</param>
        /// <returns>True when both snapshots represent the same state</returns>
        public bool Equals(WorkspacePanelSnapshot other)
        {
            if (other == null || !PathEquals(this.CurrentPath, other.CurrentPath) || !PathEquals(this.FocusedPath, other.FocusedPath) || this.CursorIndex != other.CursorIndex || this.SortField != other.SortField || this.SortDirection != other.SortDirection || !PathEquals(this.ScrollAnchorPath, other.ScrollAnchorPath) || !PathEquals(this.SelectionAnchorPath, other.SelectionAnchorPath) || this.NameColumnRatio != other.NameColumnRatio || this.SizeColumnRatio != other.SizeColumnRatio || this.DateColumnRatio != other.DateColumnRatio || this.AttributesColumnRatio != other.AttributesColumnRatio || this.OwnerColumnRatio != other.OwnerColumnRatio || this.TreeSizePercent != other.TreeSizePercent || this.TreeCollapsed != other.TreeCollapsed)
                return false;

            return PathsEqual(this.SelectedPaths, other.SelectedPaths) && PathsEqual(this.ExpandedDirectoryPaths, other.ExpandedDirectoryPaths) && PathsEqual(this.BackHistory, other.BackHistory) && PathsEqual(this.ForwardHistory, other.ForwardHistory) && ColumnsEqual(this.Columns, other.Columns);
        }

        /// <summary>
        /// Returns the hash of the persistent panel value
        /// </summary>
        /// <returns>Snapshot hash</returns>
        public override int GetHashCode()
        {
            HashCode hash = new HashCode();
            StringComparer pathComparer = GetPathComparer();
            hash.Add(this.CurrentPath, pathComparer);
            hash.Add(this.FocusedPath, pathComparer);
            hash.Add(this.CursorIndex);
            hash.Add(this.SortField);
            hash.Add(this.SortDirection);
            hash.Add(this.ScrollAnchorPath, pathComparer);
            hash.Add(this.SelectionAnchorPath, pathComparer);
            hash.Add(this.NameColumnRatio);
            hash.Add(this.SizeColumnRatio);
            hash.Add(this.DateColumnRatio);
            hash.Add(this.AttributesColumnRatio);
            hash.Add(this.OwnerColumnRatio);
            hash.Add(this.TreeSizePercent);
            hash.Add(this.TreeCollapsed);
            for (int i = 0; i < this.SelectedPaths.Count; i++)
                hash.Add(this.SelectedPaths[i], pathComparer);
            for (int i = 0; i < this.ExpandedDirectoryPaths.Count; i++)
                hash.Add(this.ExpandedDirectoryPaths[i], pathComparer);
            for (int i = 0; i < this.BackHistory.Count; i++)
                hash.Add(this.BackHistory[i], pathComparer);
            for (int i = 0; i < this.ForwardHistory.Count; i++)
                hash.Add(this.ForwardHistory[i], pathComparer);
            for (int i = 0; i < this.Columns.Count; i++)
                hash.Add(this.Columns[i]);
            return hash.ToHashCode();
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Copies, de-duplicates and bounds a path sequence while retaining its most recent values
        /// </summary>
        private static IReadOnlyList<string> CopyDistinctPaths(IEnumerable<string> paths, int maximumCount)
        {
            List<string> source = paths == null ? new List<string>() : new List<string>(paths);
            List<string> result = new List<string>();
            HashSet<string> seen = new HashSet<string>(GetPathComparer());
            int start = Math.Max(0, source.Count - maximumCount);
            for (int i = start; i < source.Count; i++)
            {
                string path = source[i] ?? "";
                if (!string.IsNullOrWhiteSpace(path) && seen.Add(path))
                    result.Add(path);
            }
            return new ReadOnlyCollection<string>(result);
        }

        /// <summary>
        /// Copies a complete valid column layout or returns the legacy empty representation
        /// </summary>
        private static IReadOnlyList<FileListColumnSnapshot> CopyValidColumns(IEnumerable<FileListColumnState> columns)
        {
            List<FileListColumnState> source = columns == null ? new List<FileListColumnState>() : new List<FileListColumnState>(columns);
            List<FileListColumnSnapshot> result = new List<FileListColumnSnapshot>();
            HashSet<FileListColumnId> seen = new HashSet<FileListColumnId>();
            if (source.Count != Enum.GetValues<FileListColumnId>().Length)
                return new ReadOnlyCollection<FileListColumnSnapshot>(result);

            for (int i = 0; i < source.Count; i++)
            {
                FileListColumnState column = source[i];
                if (column == null || !Enum.IsDefined(column.Id) || !seen.Add(column.Id) || !double.IsFinite(column.Width) || column.Width <= 0 || (column.Id == FileListColumnId.Name && !column.Visible))
                    return new ReadOnlyCollection<FileListColumnSnapshot>(new List<FileListColumnSnapshot>());
                result.Add(new FileListColumnSnapshot(column.Id, column.Width, column.Visible));
            }

            return new ReadOnlyCollection<FileListColumnSnapshot>(result);
        }

        /// <summary>
        /// Compares complete ordered column layouts
        /// </summary>
        private static bool ColumnsEqual(IReadOnlyList<FileListColumnSnapshot> left, IReadOnlyList<FileListColumnSnapshot> right)
        {
            if (left == null || right == null || left.Count != right.Count)
                return false;
            for (int i = 0; i < left.Count; i++)
            {
                if (left[i] != right[i])
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Returns the platform filesystem path comparer
        /// </summary>
        private static StringComparer GetPathComparer()
        {
            return OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        }

        /// <summary>
        /// Compares two semantic paths using platform filesystem rules
        /// </summary>
        private static bool PathEquals(string left, string right)
        {
            return GetPathComparer().Equals(left ?? "", right ?? "");
        }

        /// <summary>
        /// Accepts and normalizes only a complete positive finite column configuration
        /// </summary>
        private void SetColumnRatios(double? name, double? size, double? date, double? attributes, double? owner)
        {
            if (!name.HasValue || !size.HasValue || !date.HasValue || !attributes.HasValue || !owner.HasValue || !double.IsFinite(name.Value) || !double.IsFinite(size.Value) || !double.IsFinite(date.Value) || !double.IsFinite(attributes.Value) || !double.IsFinite(owner.Value) || name.Value <= 0 || size.Value <= 0 || date.Value <= 0 || attributes.Value <= 0 || owner.Value <= 0)
                return;

            double total = name.Value + size.Value + date.Value + attributes.Value + owner.Value;
            if (!double.IsFinite(total) || total <= 0)
                return;

            double normalizedName = Math.Round(name.Value / total, 12);
            double normalizedSize = Math.Round(size.Value / total, 12);
            double normalizedDate = Math.Round(date.Value / total, 12);
            double normalizedAttributes = Math.Round(attributes.Value / total, 12);
            double normalizedOwner = 1 - normalizedName - normalizedSize - normalizedDate - normalizedAttributes;
            if (normalizedName <= 0 || normalizedSize <= 0 || normalizedDate <= 0 || normalizedAttributes <= 0 || normalizedOwner <= 0)
                return;

            this.NameColumnRatio = normalizedName;
            this.SizeColumnRatio = normalizedSize;
            this.DateColumnRatio = normalizedDate;
            this.AttributesColumnRatio = normalizedAttributes;
            this.OwnerColumnRatio = normalizedOwner;
        }

        /// <summary>
        /// Compares two ordered path sequences
        /// </summary>
        /// <param name="left">First sequence</param>
        /// <param name="right">Second sequence</param>
        /// <returns>True when both sequences contain the same paths in the same order</returns>
        private static bool PathsEqual(IReadOnlyList<string> left, IReadOnlyList<string> right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left == null || right == null || left.Count != right.Count)
                return false;

            for (int i = 0; i < left.Count; i++)
            {
                if (!PathEquals(left[i], right[i]))
                    return false;
            }

            return true;
        }

        #endregion

        #region Properties

        /// <summary>
        /// Current directory
        /// </summary>
        public string CurrentPath { get; }

        /// <summary>
        /// Path of the item under the cursor
        /// </summary>
        public string FocusedPath { get; }

        /// <summary>
        /// Compatibility alias for the semantic focused path
        /// </summary>
        public string CursorPath { get { return this.FocusedPath; } }

        /// <summary>
        /// Cursor fallback index
        /// </summary>
        public int CursorIndex { get; }

        /// <summary>
        /// Selected paths
        /// </summary>
        public IReadOnlyList<string> SelectedPaths { get; }

        /// <summary>
        /// Sort column
        /// </summary>
        public SortField SortField { get; }

        /// <summary>
        /// Sort direction
        /// </summary>
        public SortDirection SortDirection { get; }

        /// <summary>
        /// Path of the visible item used as scroll anchor
        /// </summary>
        public string ScrollAnchorPath { get; }

        /// <summary>
        /// Semantic paths of expanded directories in the tree
        /// </summary>
        public IReadOnlyList<string> ExpandedDirectoryPaths { get; }

        /// <summary>
        /// Stable range-selection anchor path
        /// </summary>
        public string SelectionAnchorPath { get; }

        /// <summary>
        /// Previous directory paths, nearest last
        /// </summary>
        public IReadOnlyList<string> BackHistory { get; }

        /// <summary>
        /// Forward directory paths, nearest last
        /// </summary>
        public IReadOnlyList<string> ForwardHistory { get; }

        /// <summary>
        /// Complete ordered measured columns, or empty for a legacy layout
        /// </summary>
        public IReadOnlyList<FileListColumnSnapshot> Columns { get; }

        /// <summary>
        /// Percentuale verticale occupata dall'albero
        /// </summary>
        public double TreeSizePercent { get; }

        /// <summary>
        /// Indica se l'albero è compresso
        /// </summary>
        public bool TreeCollapsed { get; }

        /// <summary>
        /// Normalized name-column width, or null for the legacy layout
        /// </summary>
        public double? NameColumnRatio { get; private set; }

        /// <summary>
        /// Normalized size-column width, or null for the legacy layout
        /// </summary>
        public double? SizeColumnRatio { get; private set; }

        /// <summary>
        /// Normalized date-column width, or null for the legacy layout
        /// </summary>
        public double? DateColumnRatio { get; private set; }

        /// <summary>
        /// Normalized attributes-column width, or null for the legacy layout
        /// </summary>
        public double? AttributesColumnRatio { get; private set; }

        /// <summary>
        /// Normalized owner-column width, or null for the legacy layout
        /// </summary>
        public double? OwnerColumnRatio { get; private set; }

        #endregion
    }
}
