using Bivium.Models;
using System;
using System.Collections.Generic;
using System.Text;
using XTerm.Buffer;
using XTerm.Common;

namespace Bivium.Services
{
    /// <summary>
    /// Provenance of the archived cells, invalidated by writes and by XTerm recycling
    /// </summary>
    internal sealed class TerminalHistoryRowState
    {
        #region Proprietà

        /// <summary>
        /// Cells already consolidated in the external history, regardless of their text
        /// </summary>
        public bool[] Archived { get; }

        /// <summary>
        /// Provenance of an empty row, which owns no serialized cells
        /// </summary>
        public bool EmptyArchived { get; set; }

        #endregion

        #region Costruttore

        /// <summary>
        /// Allocates one logical bit for each column of the row
        /// </summary>
        /// <param name="length">Number of columns</param>
        public TerminalHistoryRowState(int length)
        {
            this.Archived = new bool[length];
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Retrieves the state only while the row keeps its own cache
        /// </summary>
        /// <param name="line">Actual buffer row</param>
        /// <returns>Valid provenance of the current row</returns>
        public static TerminalHistoryRowState Get(BufferLine line)
        {
            if (line.Cache is TerminalHistoryRowState state)
                return state;
            state = new TerminalHistoryRowState(line.Length);
            line.Cache = state;
            return state;
        }

        /// <summary>
        /// Extracts the still-mutable fragments without comparing text of different rows
        /// </summary>
        /// <param name="snapshot">Immutable content to filter</param>
        /// <param name="selected">Columns to consolidate, or all if null</param>
        /// <returns>Fragments in the original order</returns>
        public List<TerminalLineSnapshot> Extract(TerminalLineSnapshot snapshot, bool[] selected = null)
        {
            List<TerminalLineSnapshot> result = new List<TerminalLineSnapshot>();
            if (snapshot.Cells.Count == 0)
            {
                if (!this.EmptyArchived && (selected == null || selected.Length > 0 && selected[0]))
                    result.Add(snapshot);
                return result;
            }

            int column = 0;
            while (column < snapshot.Cells.Count)
            {
                if (this.Archived[column] || selected != null && !selected[column])
                {
                    column++;
                    continue;
                }
                int start = column;
                List<TerminalCellSnapshot> cells = new List<TerminalCellSnapshot>();
                StringBuilder text = new StringBuilder();
                int bytes = 32;
                while (column < snapshot.Cells.Count && !this.Archived[column] && (selected == null || selected[column]))
                {
                    TerminalCellSnapshot cell = snapshot.Cells[column++];
                    cells.Add(cell);
                    if (cell.Width > 0)
                        text.Append(cell.Content);
                    bytes += 40 + Encoding.UTF8.GetByteCount(cell.Content) + Encoding.UTF8.GetByteCount(cell.Hyperlink);
                }
                string fragmentText = text.ToString();
                result.Add(new TerminalLineSnapshot
                {
                    Wrapped = snapshot.Wrapped || start > 0,
                    LineAttribute = snapshot.LineAttribute,
                    PromptColumn = snapshot.PromptColumn >= start && snapshot.PromptColumn < column ? snapshot.PromptColumn - start : -1,
                    Cells = cells.AsReadOnly(),
                    Text = fragmentText,
                    SerializedBytes = bytes + 4 + Encoding.UTF8.GetByteCount(fragmentText)
                });
            }
            return result;
        }

        /// <summary>
        /// Also consolidates the padding, which the reflow can merge into a mixed row
        /// </summary>
        public void Commit()
        {
            Array.Fill(this.Archived, true);
            this.EmptyArchived = true;
        }

        #endregion
    }

    /// <summary>
    /// Carries the provenance using the public Resize on a copy with numeric markers
    /// </summary>
    internal sealed class TerminalHistoryReflow
    {
        #region Variabili di classe

        /// <summary>Real buffer, resized by the caller between construction and Complete</summary>
        private readonly TerminalBuffer _buffer;

        /// <summary>Marked copy needed only for the reflow of normal columns</summary>
        private readonly TerminalBuffer _shadow;

        /// <summary>Provenance frozen before the resize</summary>
        private readonly List<SourceRow> _sources = new List<SourceRow>();

        /// <summary>Row identities when the resize performs no reflow</summary>
        private readonly Dictionary<BufferLine, int> _indices = new Dictionary<BufferLine, int>();

        #endregion

        #region Costruttore

        /// <summary>
        /// Freezes only the viewport snapshots; the scrollback requires only provenance bits
        /// </summary>
        /// <param name="buffer">Normal or alternate buffer of the session</param>
        /// <param name="cols">New width</param>
        /// <param name="rows">New height</param>
        /// <param name="normal">True for the buffer that supports reflow</param>
        /// <param name="serialize">Bivium protocol serializer</param>
        public TerminalHistoryReflow(TerminalBuffer buffer, int cols, int rows, bool normal, Func<BufferLine, TerminalLineSnapshot> serialize)
        {
            this._buffer = buffer;
            bool reflow = normal && cols != buffer.Cols;
            if (reflow)
            {
                this._shadow = new TerminalBuffer(buffer.Cols, buffer.Rows, buffer.Lines.MaxLength - buffer.Rows, true);
                for (int row = 0; row < buffer.BaseY; row++)
                    this._shadow.ScrollUp(1);
                while (this._shadow.Lines.Length < buffer.Lines.Length)
                    this._shadow.Lines.Push(new BufferLine(buffer.Cols));
                while (this._shadow.Lines.Length > buffer.Lines.Length)
                    this._shadow.Lines.Pop();
            }

            int lastMeaningfulRow = buffer.BaseY + buffer.Y;
            for (int row = buffer.BaseY; row < buffer.Lines.Length; row++)
                if (buffer.Lines[row].GetTrimmedLength() > 0)
                    lastMeaningfulRow = Math.Max(lastMeaningfulRow, row);
            for (int row = 0; row < buffer.Lines.Length; row++)
            {
                BufferLine line = buffer.Lines[row];
                TerminalHistoryRowState state = TerminalHistoryRowState.Get(line);
                bool visible = row >= buffer.BaseY && row < buffer.BaseY + buffer.Rows;
                if (row < buffer.BaseY)
                    state.Commit();
                this._sources.Add(new SourceRow
                {
                    State = state,
                    Snapshot = visible && row <= lastMeaningfulRow ? serialize(line) : null,
                    Visible = new bool[line.Length]
                });
                this._indices.Add(line, row);
                if (!reflow)
                    continue;

                BufferLine copy = this._shadow.Lines[row];
                copy.Resize(line.Length, BufferCell.Space);
                copy.IsWrapped = line.IsWrapped;
                // Both metadata items prevent the group reflow in the same public API
                copy.LineAttribute = line.HasSizedRuns ? LineAttribute.DoubleWidth : line.LineAttribute;
                for (int column = 0; column < line.Length; column++)
                {
                    BufferCell cell = line[column];
                    cell.Attributes.SetFgColor(row + 1, 1);
                    cell.Attributes.SetBgColor(column + 1, 1);
                    copy[column] = cell;
                }
            }

            if (reflow)
            {
                this._shadow.SetScrollRegion(buffer.ScrollTop, buffer.ScrollBottom);
                this._shadow.SetLeftRightMargins(buffer.ScrollLeft, buffer.ScrollRight);
                this._shadow.SetCursorRaw(buffer.X, buffer.Y);
                this._shadow.ViewportY = buffer.ViewportY;
                this._shadow.Resize(cols, rows);
            }
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Archives what the resize moves out of the viewport and re-associates the surviving cells
        /// </summary>
        /// <param name="archive">Segmented archive of the session</param>
        public void Complete(TerminalHistoryArchive archive)
        {
            for (int row = this._buffer.BaseY; row < Math.Min(this._buffer.Lines.Length, this._buffer.BaseY + this._buffer.Rows); row++)
            {
                for (int column = 0; column < this._buffer.Lines[row].Length; column++)
                    if (this.TryGetSource(row, column, out SourceRow source, out int sourceColumn))
                        source.Visible[sourceColumn] = true;
            }

            // The archive is append-only: if a suffix disappears, it also consolidates the prefix
            // that is still visible. Its cells stay on screen but the export does not repeat them
            int lastSource = -1;
            int lastColumn = -1;
            for (int index = 0; index < this._sources.Count; index++)
            {
                SourceRow source = this._sources[index];
                if (source.Snapshot == null)
                    continue;
                int length = Math.Max(1, source.Snapshot.Cells.Count);
                for (int column = 0; column < Math.Min(length, source.Visible.Length); column++)
                {
                    bool archived = source.Snapshot.Cells.Count == 0 ? source.State.EmptyArchived : source.State.Archived[column];
                    if (!source.Visible[column] && !archived)
                    {
                        lastSource = index;
                        lastColumn = column;
                    }
                }
            }
            for (int index = 0; index <= lastSource; index++)
            {
                SourceRow source = this._sources[index];
                if (source.Snapshot == null)
                    continue;
                bool[] exited = new bool[source.Visible.Length];
                int length = index < lastSource ? exited.Length : lastColumn + 1;
                Array.Fill(exited, true, 0, length);
                foreach (TerminalLineSnapshot fragment in source.State.Extract(source.Snapshot, exited))
                    archive.Append(fragment);
                for (int column = 0; column < length; column++)
                    source.State.Archived[column] = true;
                if (length > 0)
                    source.State.EmptyArchived = true;
            }

            for (int row = 0; row < this._buffer.Lines.Length; row++)
            {
                BufferLine line = this._buffer.Lines[row];
                TerminalHistoryRowState state = new TerminalHistoryRowState(line.Length);
                for (int column = 0; column < line.Length; column++)
                {
                    if (!this.TryGetSource(row, column, out SourceRow source, out int sourceColumn))
                        continue;
                    state.Archived[column] = source.State.Archived[sourceColumn];
                    if (column == 0)
                        state.EmptyArchived = source.State.EmptyArchived;
                }
                line.Cache = state;
            }
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Reads the identity from the marker, never from the text or color of the real terminal
        /// </summary>
        /// <param name="row">Row after the resize</param>
        /// <param name="column">Column after the resize</param>
        /// <param name="source">Original row</param>
        /// <param name="sourceColumn">Original column</param>
        /// <returns>True for a surviving cell with known provenance</returns>
        private bool TryGetSource(int row, int column, out SourceRow source, out int sourceColumn)
        {
            source = null;
            sourceColumn = column;
            int sourceIndex;
            if (this._shadow == null)
            {
                if (!this._indices.TryGetValue(this._buffer.Lines[row], out sourceIndex))
                    return false;
            }
            else
            {
                if (row >= this._shadow.Lines.Length || column >= this._shadow.Lines[row].Length)
                    return false;
                BufferCell cell = this._shadow.Lines[row][column];
                BufferCell actual = this._buffer.Lines[row][column];
                // OSC 66 can erase a run during clipping: that cell did not survive
                if (actual.CodePoint != cell.CodePoint || actual.ClusterId != cell.ClusterId || actual.Width != cell.Width)
                    return false;
                if (cell.Attributes.GetFgColorMode() != 1 || cell.Attributes.GetBgColorMode() != 1)
                    return false;
                sourceIndex = cell.Attributes.GetFgColor() - 1;
                sourceColumn = cell.Attributes.GetBgColor() - 1;
            }
            if (sourceIndex < 0 || sourceIndex >= this._sources.Count)
                return false;
            source = this._sources[sourceIndex];
            return sourceColumn >= 0 && sourceColumn < source.State.Archived.Length;
        }

        #endregion

        #region Classi annidate

        /// <summary>
        /// A single identity allocation per row, without objects or text copies per cell
        /// </summary>
        private sealed class SourceRow
        {
            /// <summary>
            /// Provenance already consolidated in the original row
            /// </summary>
            public TerminalHistoryRowState State { get; set; }

            /// <summary>
            /// Snapshot of the original row when it belonged to the viewport
            /// </summary>
            public TerminalLineSnapshot Snapshot { get; set; }

            /// <summary>
            /// Original cells still visible after the resize
            /// </summary>
            public bool[] Visible { get; set; }
        }

        #endregion
    }
}
