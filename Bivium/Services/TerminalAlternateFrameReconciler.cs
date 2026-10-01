using Bivium.Models;
using System;
using System.Collections.Generic;
using XTerm.Buffer;
using XTerm.Common;

namespace Bivium.Services
{
    /// <summary>
    /// Conserva le righe ritirate da frame successivi del buffer alternate
    /// </summary>
    internal sealed class TerminalAlternateFrameReconciler
    {
        #region Variabili di classe

        /// <summary>
        /// Ultimo frame completo confrontabile, limitato a un viewport
        /// </summary>
        private List<FrameRow> _candidate = new List<FrameRow>();

        /// <summary>
        /// Indica che un boundary ha aperto un possibile redraw non ancora riconciliato
        /// </summary>
        private bool _pending;

        /// <summary>
        /// Snapshot di presentazione riutilizzabili soltanto per il viewport corrente
        /// </summary>
        private List<CachedRow> _rowCache = new List<CachedRow>();

        /// <summary>
        /// Buffer a cui appartiene la cache bounded
        /// </summary>
        private TerminalBuffer _cachedBuffer;

        /// <summary>
        /// Geometria della cache bounded
        /// </summary>
        private int _cachedRows;

        /// <summary>
        /// Larghezza della cache bounded
        /// </summary>
        private int _cachedCols;

        #endregion

        #region Proprietà

        /// <summary>
        /// Indica se la prossima write deve osservare il viewport
        /// </summary>
        public bool Pending { get { return this._pending; } }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Chiude il frame precedente e adotta sempre quello uscente come nuova base del redraw
        /// </summary>
        /// <param name="buffer">Buffer alternate corrente</param>
        /// <param name="rows">Altezza corrente del viewport</param>
        /// <param name="serialize">Serializzatore immutabile delle righe</param>
        /// <param name="append">Destinazione dei frammenti consolidati</param>
        public void BeforeRedraw(TerminalBuffer buffer, int rows, Func<BufferLine, TerminalLineSnapshot> serialize, Action<TerminalLineSnapshot> append)
        {
            ValidateArguments(buffer, serialize, append);
            List<FrameRow> current = Capture(buffer, rows, serialize);
            if (current.Count > 0)
            {
                if (this._candidate.Count > 0)
                    this.TryArchiveRetiredPrefix(current, append, out _);
                this._candidate = current;
            }
            this._pending = true;
        }

        /// <summary>
        /// Tenta la riconciliazione dopo la write soltanto quando un boundary è pendente
        /// </summary>
        /// <param name="buffer">Buffer alternate corrente</param>
        /// <param name="rows">Altezza corrente del viewport</param>
        /// <param name="serialize">Serializzatore immutabile delle righe</param>
        /// <param name="append">Destinazione dei frammenti consolidati</param>
        public void AfterWrite(TerminalBuffer buffer, int rows, Func<BufferLine, TerminalLineSnapshot> serialize, Action<TerminalLineSnapshot> append)
        {
            this.Reconcile(buffer, rows, serialize, append, false);
        }

        /// <summary>
        /// Conclude la riconciliazione alla fine di una transazione atomica
        /// </summary>
        /// <param name="buffer">Buffer alternate corrente</param>
        /// <param name="rows">Altezza corrente del viewport</param>
        /// <param name="serialize">Serializzatore immutabile delle righe</param>
        /// <param name="append">Destinazione dei frammenti consolidati</param>
        public void CompleteWrite(TerminalBuffer buffer, int rows, Func<BufferLine, TerminalLineSnapshot> serialize, Action<TerminalLineSnapshot> append)
        {
            this.Reconcile(buffer, rows, serialize, append, true);
        }

        /// <summary>
        /// Dimentica il candidato quando geometria o buffer cambiano
        /// </summary>
        public void Reset()
        {
            this._candidate.Clear();
            this._rowCache.Clear();
            this._cachedBuffer = null;
            this._cachedRows = 0;
            this._cachedCols = 0;
            this._pending = false;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Riconcilia il frame corrente, con chiusura opzionale del boundary pendente
        /// </summary>
        /// <param name="buffer">Buffer alternate corrente</param>
        /// <param name="rows">Altezza corrente del viewport</param>
        /// <param name="serialize">Serializzatore immutabile delle righe</param>
        /// <param name="append">Destinazione dei frammenti consolidati</param>
        /// <param name="complete">True quando la transazione atomica è conclusa</param>
        private void Reconcile(TerminalBuffer buffer, int rows, Func<BufferLine, TerminalLineSnapshot> serialize, Action<TerminalLineSnapshot> append, bool complete)
        {
            ValidateArguments(buffer, serialize, append);
            if (!this._pending)
                return;

            List<FrameRow> current = Capture(buffer, rows, serialize);
            if (current.Count == 0)
            {
                if (this._candidate.Count == 0 || complete)
                    this._pending = false;
                return;
            }

            if (this._candidate.Count == 0)
            {
                this._candidate = current;
                this._pending = false;
                return;
            }

            if (this.TryArchiveRetiredPrefix(current, append, out bool archived))
            {
                this._candidate = current;
                if (archived || complete)
                    this._pending = false;
            }
            else if (complete)
            {
                this._candidate = current;
                this._pending = false;
            }
        }

        /// <summary>
        /// Valida le dipendenze richieste da entrambe le fasi
        /// </summary>
        private static void ValidateArguments(TerminalBuffer buffer, Func<BufferLine, TerminalLineSnapshot> serialize, Action<TerminalLineSnapshot> append)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            if (serialize == null)
                throw new ArgumentNullException(nameof(serialize));
            if (append == null)
                throw new ArgumentNullException(nameof(append));
        }

        /// <summary>
        /// Congela le righe fino all'ultima che contiene celle significative
        /// </summary>
        private List<FrameRow> Capture(TerminalBuffer buffer, int rows, Func<BufferLine, TerminalLineSnapshot> serialize)
        {
            List<FrameRow> result = new List<FrameRow>();
            List<CachedRow> nextCache = new List<CachedRow>();
            bool sameGeometry = ReferenceEquals(this._cachedBuffer, buffer) && this._cachedRows == rows && this._cachedCols == buffer.Cols;
            int first = buffer.BaseY;
            int count = Math.Min(Math.Max(0, rows), Math.Max(0, buffer.Lines.Length - first));
            int last = first + count - 1;
            while (last >= first && buffer.Lines[last].GetTrimmedLength() == 0)
                last--;

            for (int index = first; index <= last; index++)
            {
                BufferLine line = buffer.Lines[index];
                int position = index - first;
                int promptColumn = GetPromptColumn(line);
                TerminalHistoryRowState state = TerminalHistoryRowState.Get(line);
                CachedRow cached = sameGeometry && position < this._rowCache.Count ? this._rowCache[position] : null;
                FrameRow frame;
                if (cached != null && cached.Position == position && ReferenceEquals(cached.Line, line) && ReferenceEquals(cached.State, state) && cached.Frame.Snapshot.Wrapped == line.IsWrapped && cached.Frame.Snapshot.PromptColumn == promptColumn)
                {
                    frame = cached.Frame;
                    nextCache.Add(cached);
                }
                else
                {
                    frame = new FrameRow(position, serialize(line), state);
                    nextCache.Add(new CachedRow(position, line, state, frame));
                }
                result.Add(frame);
            }

            this._rowCache = nextCache;
            this._cachedBuffer = buffer;
            this._cachedRows = rows;
            this._cachedCols = buffer.Cols;
            return result;
        }

        /// <summary>
        /// Legge il primo prompt mark, che può cambiare senza invalidare la cache della riga
        /// </summary>
        private static int GetPromptColumn(BufferLine line)
        {
            for (int i = 0; i < line.Marks.Count; i++)
                if (line.Marks[i].Kind == ShellIntegrationMark.PromptStart)
                    return line.Marks[i].Column;
            return -1;
        }

        /// <summary>
        /// Archivia soltanto le righe che precedono un anchor ordinato e non ambiguo
        /// </summary>
        private bool TryArchiveRetiredPrefix(List<FrameRow> current, Action<TerminalLineSnapshot> append, out bool archived)
        {
            archived = false;
            if (AreSameFrameReferences(this._candidate, current))
                return true;

            int stationaryPrefix = GetStationaryPrefixLength(this._candidate, current);
            int stationarySuffix = GetStationarySuffixLength(this._candidate, current, stationaryPrefix);
            int previousEnd = this._candidate.Count - stationarySuffix;
            int currentEnd = current.Count - stationarySuffix;
            if (stationaryPrefix == previousEnd && stationaryPrefix == currentEnd)
                return true;

            Anchor anchor = FindAnchor(this._candidate, stationaryPrefix, previousEnd, current, stationaryPrefix, currentEnd);
            if (anchor == null)
                return false;

            for (int i = stationaryPrefix; i < anchor.PreviousIndex; i++)
            {
                FrameRow row = this._candidate[i];
                foreach (TerminalLineSnapshot fragment in row.State.Extract(row.Snapshot))
                    append(fragment);
                row.State.Commit();
                archived = true;
            }

            return true;
        }

        /// <summary>
        /// Riconosce un viewport invariato senza confrontare testo o celle
        /// </summary>
        private static bool AreSameFrameReferences(List<FrameRow> previous, List<FrameRow> current)
        {
            if (previous.Count != current.Count)
                return false;
            for (int i = 0; i < previous.Count; i++)
                if (!ReferenceEquals(previous[i], current[i]))
                    return false;
            return true;
        }

        /// <summary>
        /// Conta l'header immutato nelle stesse posizioni del viewport
        /// </summary>
        private static int GetStationaryPrefixLength(List<FrameRow> previous, List<FrameRow> current)
        {
            int result = 0;
            int maximum = Math.Min(previous.Count, current.Count);
            while (result < maximum && previous[result].Position == current[result].Position && AreEquivalent(previous[result], current[result]))
                result++;
            return result;
        }

        /// <summary>
        /// Conta il footer immutato nelle stesse posizioni del viewport
        /// </summary>
        private static int GetStationarySuffixLength(List<FrameRow> previous, List<FrameRow> current, int prefix)
        {
            int result = 0;
            int maximum = Math.Min(previous.Count, current.Count) - prefix;
            while (result < maximum)
            {
                FrameRow previousRow = previous[previous.Count - result - 1];
                FrameRow currentRow = current[current.Count - result - 1];
                if (previousRow.Position != currentRow.Position || !AreEquivalent(previousRow, currentRow))
                    break;
                result++;
            }
            return result;
        }

        /// <summary>
        /// Cerca un anchor contiguo che inizi il nuovo body dopo almeno una riga ritirata
        /// </summary>
        private static Anchor FindAnchor(List<FrameRow> previous, int previousStart, int previousEnd, List<FrameRow> current, int currentStart, int currentEnd)
        {
            if (currentStart >= currentEnd || previousStart + 1 >= previousEnd)
                return null;

            int bestLength = 0;
            int bestIndex = -1;
            bool ambiguous = false;
            for (int previousIndex = previousStart + 1; previousIndex < previousEnd; previousIndex++)
            {
                int length = 0;
                while (previousIndex + length < previousEnd && currentStart + length < currentEnd && AreEquivalent(previous[previousIndex + length], current[currentStart + length]))
                    length++;
                if (length < bestLength)
                    continue;
                if (length == bestLength)
                {
                    if (length > 0)
                        ambiguous = true;
                    continue;
                }
                bestLength = length;
                bestIndex = previousIndex;
                ambiguous = false;
            }

            if (bestIndex < 0 || ambiguous)
                return null;
            if (bestLength >= 2 && CountMeaningfulRows(previous, bestIndex, bestLength) >= 2)
                return new Anchor(bestIndex);
            if (bestLength == 1 && bestIndex + 1 == previousEnd && IsUnique(previous, previousStart, previousEnd, previous[bestIndex]))
                return new Anchor(bestIndex);
            return null;
        }

        /// <summary>
        /// Conta le righe testuali usate come prova dell'ordine del body
        /// </summary>
        private static int CountMeaningfulRows(List<FrameRow> rows, int start, int count)
        {
            int result = 0;
            for (int i = start; i < start + count; i++)
                if (!string.IsNullOrEmpty(rows[i].Snapshot.Text))
                    result++;
            return result;
        }

        /// <summary>
        /// Verifica che un anchor di una riga non abbia omonimi nel body precedente
        /// </summary>
        private static bool IsUnique(List<FrameRow> rows, int start, int end, FrameRow expected)
        {
            int matches = 0;
            for (int i = start; i < end; i++)
            {
                if (AreEquivalent(rows[i], expected))
                    matches++;
                if (matches > 1)
                    return false;
            }
            return matches == 1;
        }

        /// <summary>
        /// Confronta il contratto plain-text e soft-wrap usando prima una firma O(1)
        /// </summary>
        private static bool AreEquivalent(FrameRow left, FrameRow right)
        {
            return left.Signature == right.Signature && left.Snapshot.Wrapped == right.Snapshot.Wrapped && string.Equals(left.Snapshot.Text, right.Snapshot.Text, StringComparison.Ordinal);
        }

        #endregion

        #region Classi annidate

        /// <summary>
        /// Associa uno snapshot alla riga e alla generazione di stato ancora valida
        /// </summary>
        private sealed class CachedRow
        {
            /// <summary>
            /// Crea una voce limitata al viewport corrente
            /// </summary>
            public CachedRow(int position, BufferLine line, TerminalHistoryRowState state, FrameRow frame)
            {
                this.Position = position;
                this.Line = line;
                this.State = state;
                this.Frame = frame;
            }

            /// <summary>
            /// Posizione nel viewport
            /// </summary>
            public int Position { get; }

            /// <summary>
            /// Identità della riga XTerm.NET
            /// </summary>
            public BufferLine Line { get; }

            /// <summary>
            /// Generazione invalidata da qualsiasi mutazione della riga
            /// </summary>
            public TerminalHistoryRowState State { get; }

            /// <summary>
            /// Snapshot riutilizzabile finché entrambe le identità restano valide
            /// </summary>
            public FrameRow Frame { get; }
        }

        /// <summary>
        /// Riga immutabile associata alla provenienza valida al momento della cattura
        /// </summary>
        private sealed class FrameRow
        {
            /// <summary>
            /// Crea una riga candidata
            /// </summary>
            public FrameRow(int position, TerminalLineSnapshot snapshot, TerminalHistoryRowState state)
            {
                this.Position = position;
                this.Snapshot = snapshot;
                this.State = state;
                this.Signature = HashCode.Combine(StringComparer.Ordinal.GetHashCode(snapshot.Text), snapshot.Wrapped);
            }

            /// <summary>
            /// Posizione nel viewport
            /// </summary>
            public int Position { get; }

            /// <summary>
            /// Contenuto congelato
            /// </summary>
            public TerminalLineSnapshot Snapshot { get; }

            /// <summary>
            /// Provenienza delle celle congelate
            /// </summary>
            public TerminalHistoryRowState State { get; }

            /// <summary>
            /// Firma economica del contratto plain-text e soft-wrap
            /// </summary>
            public int Signature { get; }
        }

        /// <summary>
        /// Posizione comprovata del body precedente che continua nel frame corrente
        /// </summary>
        private sealed class Anchor
        {
            /// <summary>
            /// Crea un anchor sul frame precedente
            /// </summary>
            public Anchor(int previousIndex)
            {
                this.PreviousIndex = previousIndex;
            }

            /// <summary>
            /// Prima riga precedente ancora presente nel frame corrente
            /// </summary>
            public int PreviousIndex { get; }
        }

        #endregion
    }
}
