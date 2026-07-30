using System;
using System.Collections.Generic;

namespace Bivium.Models
{
    /// <summary>
    /// DEC attributes of a terminal row exposed by the Bivium protocol
    /// </summary>
    public enum TerminalLineAttribute
    {
        Normal = 0,
        DoubleWidth = 1,
        DoubleHeightTop = 2,
        DoubleHeightBottom = 3
    }

    /// <summary>
    /// How to interpret a cell color value
    /// </summary>
    public enum TerminalColorMode
    {
        IndexedOrDefault = 0,
        Rgb = 1
    }

    /// <summary>
    /// Terminal cell styles exposed by the Bivium protocol
    /// </summary>
    [Flags]
    public enum TerminalCellAttributes
    {
        None = 0,
        Bold = 1,
        Dim = 2,
        Italic = 4,
        Underline = 8,
        Blink = 16,
        Inverse = 32,
        Invisible = 64,
        Strikethrough = 128,
        Overline = 256
    }

    /// <summary>
    /// Bounded snapshot of the entire terminal runtime
    /// </summary>
    public sealed class TerminalRuntimeSnapshot
    {
        /// <summary>
        /// Current terminal sessions
        /// </summary>
        public IReadOnlyList<TerminalSessionSnapshot> Sessions { get; set; } = Array.Empty<TerminalSessionSnapshot>();

        /// <summary>
        /// Active session identifier
        /// </summary>
        public int ActiveSessionId { get; set; }
    }

    /// <summary>
    /// Aggregate metrics without terminal content
    /// </summary>
    public sealed class TerminalRuntimeMetrics
    {
        /// <summary>
        /// Number of retained sessions
        /// </summary>
        public int SessionCount { get; set; }

        /// <summary>
        /// Number of sessions with a running process
        /// </summary>
        public int RunningSessionCount { get; set; }

        /// <summary>
        /// Number of registered runtime subscribers
        /// </summary>
        public int SubscriberCount { get; set; }

        /// <summary>
        /// Serialized bytes retained in global history
        /// </summary>
        public long HistoryBytes { get; set; }
    }

    /// <summary>
    /// Bounded terminal session snapshot
    /// </summary>
    public sealed class TerminalSessionSnapshot
    {
        /// <summary>
        /// Stable session identifier
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// Tab label
        /// </summary>
        public string Label { get; set; } = "";

        /// <summary>
        /// Initial working directory
        /// </summary>
        public string WorkingDirectory { get; set; } = "";

        /// <summary>
        /// Whether the process is still running
        /// </summary>
        public bool Running { get; set; }

        /// <summary>
        /// Whether the process has exited
        /// </summary>
        public bool Exited { get; set; }

        /// <summary>
        /// Whether the tab has unread output
        /// </summary>
        public bool HasUnreadOutput { get; set; }

        /// <summary>
        /// Current columns
        /// </summary>
        public int Cols { get; set; }

        /// <summary>
        /// Current rows
        /// </summary>
        public int Rows { get; set; }

        /// <summary>
        /// Monotonic session revision
        /// </summary>
        public long Revision { get; set; }

        /// <summary>
        /// First logical row still available
        /// </summary>
        public long HistoryStart { get; set; }

        /// <summary>
        /// Exclusive offset of the last history row
        /// </summary>
        public long HistoryEnd { get; set; }

        /// <summary>
        /// Whether previous history was truncated
        /// </summary>
        public bool HistoryTruncated { get; set; }

        /// <summary>
        /// Serialized bytes retained in history
        /// </summary>
        public long HistoryBytes { get; set; }

        /// <summary>
        /// Configured remote page size
        /// </summary>
        public int HistoryPageRows { get; set; }

        /// <summary>
        /// Current terminal screen
        /// </summary>
        public TerminalScreenSnapshot Screen { get; set; } = new TerminalScreenSnapshot();
    }

    /// <summary>
    /// Atomic handoff of session, screen and history tail at the same revision
    /// </summary>
    public sealed class TerminalAttachSnapshot
    {
        /// <summary>
        /// Bounded session snapshot
        /// </summary>
        public TerminalSessionSnapshot Session { get; set; }

        /// <summary>
        /// Last history page already prepared for the renderer
        /// </summary>
        public TerminalHistoryPage HistoryTail { get; set; } = new TerminalHistoryPage();
    }

    /// <summary>
    /// Bounded revisioned patch following a handoff
    /// </summary>
    public sealed class TerminalSessionPatch
    {
        /// <summary>
        /// Revision the client must have before applying the patch
        /// </summary>
        public long FromRevision { get; set; }

        /// <summary>
        /// Resulting revision
        /// </summary>
        public long ToRevision { get; set; }

        /// <summary>
        /// Whether the bounded journal no longer covers the client revision
        /// </summary>
        public bool RequiresResync { get; set; }

        /// <summary>
        /// Bounded replacement session state at the resulting revision
        /// </summary>
        public TerminalSessionSnapshot Session { get; set; }
    }

    /// <summary>
    /// Snapshot of the current terminal screen
    /// </summary>
    public sealed class TerminalScreenSnapshot
    {
        /// <summary>
        /// Whether the alternate buffer is active
        /// </summary>
        public bool AlternateBuffer { get; set; }

        /// <summary>
        /// Cursor column
        /// </summary>
        public int CursorX { get; set; }

        /// <summary>
        /// Cursor row
        /// </summary>
        public int CursorY { get; set; }

        /// <summary>
        /// Whether the cursor is visible
        /// </summary>
        public bool CursorVisible { get; set; }

        /// <summary>
        /// Whether a terminal application enabled VT mouse tracking
        /// </summary>
        public bool MouseTracking { get; set; }

        /// <summary>
        /// Current screen rows
        /// </summary>
        public IReadOnlyList<TerminalLineSnapshot> Lines { get; set; } = Array.Empty<TerminalLineSnapshot>();
    }

    /// <summary>
    /// Bounded terminal history page
    /// </summary>
    public sealed class TerminalHistoryPage
    {
        /// <summary>
        /// First requested and returned logical row
        /// </summary>
        public long Start { get; set; }

        /// <summary>
        /// Exclusive offset of the last available row
        /// </summary>
        public long End { get; set; }

        /// <summary>
        /// Session revision used for the page
        /// </summary>
        public long Revision { get; set; }

        /// <summary>
        /// Whether previous output existed and was later truncated
        /// </summary>
        public bool Truncated { get; set; }

        /// <summary>
        /// Returned rows
        /// </summary>
        public IReadOnlyList<TerminalLineSnapshot> Lines { get; set; } = Array.Empty<TerminalLineSnapshot>();
    }

    /// <summary>
    /// Point-in-time source for a complete retained-history export
    /// </summary>
    internal sealed class TerminalHistoryExportSnapshot
    {
        /// <summary>
        /// Session identifier
        /// </summary>
        public int SessionId { get; set; }

        /// <summary>
        /// Whether output older than the retained archive was discarded
        /// </summary>
        public bool Truncated { get; set; }

        /// <summary>
        /// Immutable archived rows retained by the runtime
        /// </summary>
        public IReadOnlyList<TerminalLineSnapshot> HistoryLines { get; set; } = Array.Empty<TerminalLineSnapshot>();

        /// <summary>
        /// Current terminal screen captured with the archive
        /// </summary>
        public TerminalScreenSnapshot Screen { get; set; } = new TerminalScreenSnapshot();
    }

    /// <summary>
    /// Bounded result for a history search portion
    /// </summary>
    public sealed class TerminalSearchPage
    {
        /// <summary>
        /// Found row or -1
        /// </summary>
        public long Found { get; set; } = -1;

        /// <summary>
        /// Offset from which to continue the search
        /// </summary>
        public long Next { get; set; }

        /// <summary>
        /// Whether no rows remain in the requested direction
        /// </summary>
        public bool Complete { get; set; }
    }

    /// <summary>
    /// Serialized row with cells and wrapping metadata
    /// </summary>
    public sealed class TerminalLineSnapshot
    {
        /// <summary>
        /// Logical row offset
        /// </summary>
        public long Index { get; set; }

        /// <summary>
        /// Whether the row continues the previous row
        /// </summary>
        public bool Wrapped { get; set; }

        /// <summary>
        /// DEC row attribute
        /// </summary>
        public TerminalLineAttribute LineAttribute { get; set; }

        /// <summary>
        /// Significant row cells
        /// </summary>
        public IReadOnlyList<TerminalCellSnapshot> Cells { get; set; } = Array.Empty<TerminalCellSnapshot>();

        /// <summary>
        /// Plain row text used by search and copy
        /// </summary>
        public string Text { get; set; } = "";

        /// <summary>
        /// Accounted serialized size
        /// </summary>
        public int SerializedBytes { get; set; }
    }

    /// <summary>
    /// Serialized terminal model cell
    /// </summary>
    public sealed class TerminalCellSnapshot
    {
        /// <summary>
        /// Unicode cell content
        /// </summary>
        public string Content { get; set; } = "";

        /// <summary>
        /// Cell width, including zero-width wide placeholders
        /// </summary>
        public int Width { get; set; }

        /// <summary>
        /// Foreground color as palette index, default sentinel or RGB value
        /// </summary>
        public int Foreground { get; set; }

        /// <summary>
        /// Foreground interpretation mode
        /// </summary>
        public TerminalColorMode ForegroundMode { get; set; }

        /// <summary>
        /// Background color as palette index, default sentinel or RGB value
        /// </summary>
        public int Background { get; set; }

        /// <summary>
        /// Background interpretation mode
        /// </summary>
        public TerminalColorMode BackgroundMode { get; set; }

        /// <summary>
        /// Stable Bivium protocol style flags
        /// </summary>
        public TerminalCellAttributes Attributes { get; set; }

        /// <summary>
        /// OSC 8 URI associated with the cell, when present
        /// </summary>
        public string Hyperlink { get; set; } = "";
    }

    /// <summary>
    /// Bounded event sent to terminal clients
    /// </summary>
    public sealed class TerminalRuntimeEvent
    {
        /// <summary>
        /// Affected session or zero for list changes
        /// </summary>
        public int SessionId { get; set; }

        /// <summary>
        /// Current runtime revision
        /// </summary>
        public long Revision { get; set; }

        /// <summary>
        /// Whether the client must reload the session list
        /// </summary>
        public bool SessionsChanged { get; set; }
    }
}
