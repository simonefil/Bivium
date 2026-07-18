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
        public WorkspacePanelsSnapshot(WorkspacePanelSnapshot leftPanel, WorkspacePanelSnapshot rightPanel, int activePanel, bool singlePanelMode)
        {
            this.LeftPanel = leftPanel ?? throw new System.ArgumentNullException(nameof(leftPanel));
            this.RightPanel = rightPanel ?? throw new System.ArgumentNullException(nameof(rightPanel));
            this.ActivePanel = activePanel;
            this.SinglePanelMode = singlePanelMode;
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
            return other != null && this.ActivePanel == other.ActivePanel && this.SinglePanelMode == other.SinglePanelMode && this.LeftPanel == other.LeftPanel && this.RightPanel == other.RightPanel;
        }

        /// <summary>
        /// Returns the hash of the persistent panel value
        /// </summary>
        /// <returns>Snapshot hash</returns>
        public override int GetHashCode()
        {
            return HashCode.Combine(this.LeftPanel, this.RightPanel, this.ActivePanel, this.SinglePanelMode);
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

        #endregion
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
        /// <param name="cursorPath">Path of the item under the cursor</param>
        /// <param name="cursorIndex">Cursor fallback index</param>
        /// <param name="selectedPaths">Selected paths</param>
        /// <param name="sortField">Sort column</param>
        /// <param name="sortDirection">Sort direction</param>
        /// <param name="scrollAnchorPath">Path of the visible item used as scroll anchor</param>
        /// <param name="expandedDirectoryPaths">Paths of expanded directories in the tree</param>
        public WorkspacePanelSnapshot(string currentPath, string cursorPath, int cursorIndex, IEnumerable<string> selectedPaths, SortField sortField, SortDirection sortDirection, string scrollAnchorPath = "", IEnumerable<string> expandedDirectoryPaths = null)
        {
            List<string> selectedPathCopy = selectedPaths == null ? new List<string>() : new List<string>(selectedPaths);
            List<string> expandedPathCopy = expandedDirectoryPaths == null ? new List<string>() : new List<string>(expandedDirectoryPaths);
            this.CurrentPath = currentPath ?? "";
            this.CursorPath = cursorPath ?? "";
            this.CursorIndex = cursorIndex;
            this.SelectedPaths = new ReadOnlyCollection<string>(selectedPathCopy);
            this.SortField = sortField;
            this.SortDirection = sortDirection;
            this.ScrollAnchorPath = scrollAnchorPath ?? "";
            this.ExpandedDirectoryPaths = new ReadOnlyCollection<string>(expandedPathCopy);
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
            if (other == null || this.CurrentPath != other.CurrentPath || this.CursorPath != other.CursorPath || this.CursorIndex != other.CursorIndex || this.SortField != other.SortField || this.SortDirection != other.SortDirection || this.ScrollAnchorPath != other.ScrollAnchorPath)
                return false;

            return PathsEqual(this.SelectedPaths, other.SelectedPaths) && PathsEqual(this.ExpandedDirectoryPaths, other.ExpandedDirectoryPaths);
        }

        /// <summary>
        /// Returns the hash of the persistent panel value
        /// </summary>
        /// <returns>Snapshot hash</returns>
        public override int GetHashCode()
        {
            HashCode hash = new HashCode();
            hash.Add(this.CurrentPath);
            hash.Add(this.CursorPath);
            hash.Add(this.CursorIndex);
            hash.Add(this.SortField);
            hash.Add(this.SortDirection);
            hash.Add(this.ScrollAnchorPath);
            for (int i = 0; i < this.SelectedPaths.Count; i++)
                hash.Add(this.SelectedPaths[i]);
            for (int i = 0; i < this.ExpandedDirectoryPaths.Count; i++)
                hash.Add(this.ExpandedDirectoryPaths[i]);
            return hash.ToHashCode();
        }

        #endregion

        #region Private Methods

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
                if (left[i] != right[i])
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
        public string CursorPath { get; }

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

        #endregion
    }
}
