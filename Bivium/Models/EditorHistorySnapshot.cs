using System;
using System.Collections.Immutable;

namespace Bivium.Models
{
    /// <summary>Sostituzione UTF-16 verificabile sul contenuto prima dell'edit</summary>
    public sealed record EditorTextChange(int Offset, string Removed, string Inserted);

    /// <summary>Edit simultanei di un singolo evento contenuto Monaco</summary>
    public sealed record EditorHistoryBatch(ImmutableArray<EditorTextChange> Changes);

    /// <summary>Unità undo applicativa; una composizione può comprendere più batch</summary>
    public sealed record EditorHistoryUnit(Guid GroupId, ImmutableArray<EditorHistoryBatch> Batches, string BeforeViewState, string AfterViewState, string AfterSelections, bool Typing, long LastEditAt);

    /// <summary>History dettagliata per hydration, mai nella projection globale</summary>
    public sealed record EditorHistorySnapshot(ImmutableArray<EditorHistoryUnit> Units, int Cursor);

    /// <summary>Delta ordinato del journal; Append, Undo e Redo condividono il checkpoint documento</summary>
    public sealed class EditorHistoryMutation
    {
        /// <summary>Operazione applicativa, non comando Monaco serializzato</summary>
        public string Kind { get; set; } = "";
        /// <summary>Unità nuova oppure continuazione della digitazione contigua</summary>
        public EditorHistoryUnit Unit { get; set; }
    }
}
