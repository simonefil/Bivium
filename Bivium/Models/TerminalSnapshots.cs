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
    /// Underline style exposed by the Bivium protocol
    /// </summary>
    public enum TerminalUnderlineStyle
    {
        None = 0,
        Single = 1,
        Double = 2,
        Curly = 3,
        Dotted = 4,
        Dashed = 5
    }

    /// <summary>
    /// Progress state reported by a terminal application
    /// </summary>
    public enum TerminalProgressState
    {
        None = 0,
        Normal = 1,
        Error = 2,
        Indeterminate = 3,
        Warning = 4
    }

    /// <summary>
    /// One transient request emitted by a terminal application
    /// </summary>
    public enum TerminalClientEventType
    {
        ClipboardWrite = 1,
        Notification = 2,
        Attention = 3
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
        /// Progress state explicitly reported through OSC 9;4
        /// </summary>
        public TerminalProgressState ProgressState { get; set; }

        /// <summary>
        /// Reported progress percentage when the state is determinate
        /// </summary>
        public int ProgressValue { get; set; }

        /// <summary>
        /// Whether the application requested attention and the tab has not been opened yet
        /// </summary>
        public bool AttentionRequested { get; set; }

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
        /// Whether this snapshot carries a replacement palette and special colors
        /// </summary>
        public bool ColorsIncluded { get; set; }

        /// <summary>
        /// Current coherent 256-entry palette as RGB values
        /// </summary>
        public IReadOnlyList<int> Palette { get; set; } = Array.Empty<int>();

        /// <summary>
        /// Current default foreground as an RGB value
        /// </summary>
        public int DefaultForeground { get; set; }

        /// <summary>
        /// Current default background as an RGB value
        /// </summary>
        public int DefaultBackground { get; set; }

        /// <summary>
        /// Current cursor color as an RGB value
        /// </summary>
        public int CursorColor { get; set; }

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
        /// Negotiated keyboard mode, used by the browser for additional keys
        /// </summary>
        public bool KittyKeyboardActive { get; set; }

        /// <summary>
        /// Whether the current shell has emitted OSC 133 integration marks
        /// </summary>
        public bool ShellIntegrationAvailable { get; set; }

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
    /// Serialized row with cells and wrapping metadata
    /// </summary>
    public sealed class TerminalLineSnapshot
    {
        /// <summary>
        /// Column of an OSC 133 prompt-start mark, or -1 when absent
        /// </summary>
        public int PromptColumn { get; set; } = -1;

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
        /// Underline shape independently from the compatibility style flag
        /// </summary>
        public TerminalUnderlineStyle UnderlineStyle { get; set; }

        /// <summary>
        /// Whether the underline has its own color instead of following the foreground
        /// </summary>
        public bool HasUnderlineColor { get; set; }

        /// <summary>
        /// Underline color as palette index or RGB value
        /// </summary>
        public int UnderlineColor { get; set; }

        /// <summary>
        /// Underline color interpretation mode
        /// </summary>
        public TerminalColorMode UnderlineColorMode { get; set; }

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

        /// <summary>
        /// Bounded transient UI requests carried with this notification
        /// </summary>
        public IReadOnlyList<TerminalClientEvent> ClientEvents { get; set; } = Array.Empty<TerminalClientEvent>();
    }

    /// <summary>
    /// Bounded plain-text request destined for the attached browser
    /// </summary>
    public sealed class TerminalClientEvent
    {
        /// <summary>
        /// Monotonic identifier of the transient request
        /// </summary>
        public long Id { get; set; }

        /// <summary>
        /// Terminal session that generated the request
        /// </summary>
        public int SessionId { get; set; }

        /// <summary>
        /// Client surface the request is addressed to
        /// </summary>
        public TerminalClientEventType Type { get; set; }

        /// <summary>
        /// Text title limited to the Razor renderer
        /// </summary>
        public string Title { get; set; } = "";

        /// <summary>
        /// Text content of the request
        /// </summary>
        public string Text { get; set; } = "";
    }
}
