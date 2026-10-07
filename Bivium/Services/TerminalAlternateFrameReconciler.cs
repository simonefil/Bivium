using Bivium.Models;
using System;
using System.Collections.Generic;
using XTerm.Buffer;
using XTerm.Common;

namespace Bivium.Services
{
    /// <summary>
    /// Preserves the rows retired by subsequent frames of the alternate buffer
    /// </summary>
    internal sealed class TerminalAlternateFrameReconciler
    {
        #region Variabili di classe

        /// <summary>
        /// Last complete comparable frame, limited to one viewport
        /// </summary>
        private List<FrameRow> _candidate = new List<FrameRow>();

        /// <summary>
        /// Indicates that a boundary opened a possible redraw that is not yet reconciled
        /// </summary>
        private bool _pending;

        /// <summary>
        /// Presentation snapshots reusable only for the current viewport
        /// </summary>
        private List<CachedRow> _rowCache = new List<CachedRow>();

        /// <summary>
        /// Buffer that owns the bounded cache
        /// </summary>
        private TerminalBuffer _cachedBuffer;

        /// <summary>
        /// Geometry of the bounded cache
        /// </summary>
        private int _cachedRows;

        /// <summary>
        /// Width of the bounded cache
        /// </summary>
        private int _cachedCols;

        #endregion

        #region Proprietà

        /// <summary>
        /// Indicates whether the next write must observe the viewport
        /// </summary>
        public bool Pending { get { return this._pending; } }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Closes the previous frame and always adopts the outgoing one as the new redraw base
        /// </summary>
        /// <param name="buffer">Current alternate buffer</param>
        /// <param name="rows">Current viewport height</param>
        /// <param name="serialize">Immutable row serializer</param>
        /// <param name="append">Destination of the consolidated fragments</param>
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
        /// Attempts reconciliation after the write only when a boundary is pending
        /// </summary>
        /// <param name="buffer">Current alternate buffer</param>
        /// <param name="rows">Current viewport height</param>
        /// <param name="serialize">Immutable row serializer</param>
        /// <param name="append">Destination of the consolidated fragments</param>
        public void AfterWrite(TerminalBuffer buffer, int rows, Func<BufferLine, TerminalLineSnapshot> serialize, Action<TerminalLineSnapshot> append)
        {
            this.Reconcile(buffer, rows, serialize, append, false);
        }

        /// <summary>
        /// Completes the reconciliation at the end of an atomic transaction
        /// </summary>
        /// <param name="buffer">Current alternate buffer</param>
        /// <param name="rows">Current viewport height</param>
        /// <param name="serialize">Immutable row serializer</param>
        /// <param name="append">Destination of the consolidated fragments</param>
        public void CompleteWrite(TerminalBuffer buffer, int rows, Func<BufferLine, TerminalLineSnapshot> serialize, Action<TerminalLineSnapshot> append)
        {
            this.Reconcile(buffer, rows, serialize, append, true);
        }

        /// <summary>
        /// Forgets the candidate when geometry or buffer change
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
        /// Reconciles the current frame, with optional closing of the pending boundary
        /// </summary>
        /// <param name="buffer">Current alternate buffer</param>
        /// <param name="rows">Current viewport height</param>
        /// <param name="serialize">Immutable row serializer</param>
        /// <param name="append">Destination of the consolidated fragments</param>
        /// <param name="complete">True when the atomic transaction is complete</param>
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
        /// Validates the dependencies required by both phases
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
        /// Freezes the rows up to the last one that contains significant cells
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
        /// Reads the first prompt mark, which can change without invalidating the row cache
        /// </summary>
        private static int GetPromptColumn(BufferLine line)
        {
            for (int i = 0; i < line.Marks.Count; i++)
                if (line.Marks[i].Kind == ShellIntegrationMark.PromptStart)
                    return line.Marks[i].Column;
            return -1;
        }

        /// <summary>
        /// Archives only the rows that precede an ordered, unambiguous anchor
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
        /// Recognizes an unchanged viewport without comparing text or cells
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
        /// Counts the unchanged header at the same viewport positions
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
        /// Counts the unchanged footer at the same viewport positions
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
        /// Looks for a contiguous anchor that starts the new body after at least one retired row
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
        /// Counts the text rows used as proof of the body order
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
        /// Verifies that a single-row anchor has no namesakes in the previous body
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
        /// Compares the plain-text and soft-wrap contract, using an O(1) signature first
        /// </summary>
        private static bool AreEquivalent(FrameRow left, FrameRow right)
        {
            return left.Signature == right.Signature && left.Snapshot.Wrapped == right.Snapshot.Wrapped && string.Equals(left.Snapshot.Text, right.Snapshot.Text, StringComparison.Ordinal);
        }

        #endregion

        #region Classi annidate

        /// <summary>
        /// Associates a snapshot with the row and the still-valid state generation
        /// </summary>
        private sealed class CachedRow
        {
            /// <summary>
            /// Creates an entry limited to the current viewport
            /// </summary>
            public CachedRow(int position, BufferLine line, TerminalHistoryRowState state, FrameRow frame)
            {
                this.Position = position;
                this.Line = line;
                this.State = state;
                this.Frame = frame;
            }

            /// <summary>
            /// Position in the viewport
            /// </summary>
            public int Position { get; }

            /// <summary>
            /// XTerm.NET row identity
            /// </summary>
            public BufferLine Line { get; }

            /// <summary>
            /// Generation invalidated by any row mutation
            /// </summary>
            public TerminalHistoryRowState State { get; }

            /// <summary>
            /// Snapshot reusable as long as both identities remain valid
            /// </summary>
            public FrameRow Frame { get; }
        }

        /// <summary>
        /// Immutable row associated with the provenance valid at capture time
        /// </summary>
        private sealed class FrameRow
        {
            /// <summary>
            /// Creates a candidate row
            /// </summary>
            public FrameRow(int position, TerminalLineSnapshot snapshot, TerminalHistoryRowState state)
            {
                this.Position = position;
                this.Snapshot = snapshot;
                this.State = state;
                this.Signature = HashCode.Combine(StringComparer.Ordinal.GetHashCode(snapshot.Text), snapshot.Wrapped);
            }

            /// <summary>
            /// Position in the viewport
            /// </summary>
            public int Position { get; }

            /// <summary>
            /// Frozen content
            /// </summary>
            public TerminalLineSnapshot Snapshot { get; }

            /// <summary>
            /// Provenance of the frozen cells
            /// </summary>
            public TerminalHistoryRowState State { get; }

            /// <summary>
            /// Cheap signature of the plain-text and soft-wrap contract
            /// </summary>
            public int Signature { get; }
        }

        /// <summary>
        /// Proven position of the previous body that continues in the current frame
        /// </summary>
        private sealed class Anchor
        {
            /// <summary>
            /// Creates an anchor on the previous frame
            /// </summary>
            public Anchor(int previousIndex)
            {
                this.PreviousIndex = previousIndex;
            }

            /// <summary>
            /// First previous row still present in the current frame
            /// </summary>
            public int PreviousIndex { get; }
        }

        #endregion
    }
}
