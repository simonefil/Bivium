using System;
using System.Collections.Immutable;

namespace Bivium.Models
{
    /// <summary>UTF-16 replacement verifiable against the content before the edit</summary>
    public sealed record EditorTextChange(int Offset, string Removed, string Inserted);

    /// <summary>Simultaneous edits of a single Monaco content event</summary>
    public sealed record EditorHistoryBatch(ImmutableArray<EditorTextChange> Changes);

    /// <summary>Application undo unit; a composition may span multiple batches</summary>
    public sealed record EditorHistoryUnit(Guid GroupId, ImmutableArray<EditorHistoryBatch> Batches, string BeforeViewState, string AfterViewState, string AfterSelections, bool Typing, long LastEditAt);

    /// <summary>Detailed history for hydration, never in the global projection</summary>
    public sealed record EditorHistorySnapshot(ImmutableArray<EditorHistoryUnit> Units, int Cursor);

    /// <summary>Ordered journal delta; Append, Undo and Redo share the document checkpoint</summary>
    public sealed class EditorHistoryMutation
    {
        /// <summary>Application operation, not a serialized Monaco command</summary>
        public string Kind { get; set; } = "";
        /// <summary>New unit or continuation of contiguous typing</summary>
        public EditorHistoryUnit Unit { get; set; }
    }
}
