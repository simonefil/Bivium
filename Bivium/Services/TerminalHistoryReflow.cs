using Bivium.Models;
using System;
using System.Collections.Generic;
using System.Text;
using XTerm.Buffer;
using XTerm.Common;

namespace Bivium.Services
{
    /// <summary>
    /// Provenienza delle celle archiviate, invalidata dalle scritture e dal riciclo di XTerm
    /// </summary>
    internal sealed class TerminalHistoryRowState
    {
        #region Proprietà

        /// <summary>
        /// Celle già consolidate nello storico esterno, indipendentemente dal loro testo
        /// </summary>
        public bool[] Archived { get; }

        /// <summary>
        /// Provenienza di una riga vuota, che non possiede celle serializzate
        /// </summary>
        public bool EmptyArchived { get; set; }

        #endregion

        #region Costruttore

        /// <summary>
        /// Alloca un bit logico per ciascuna colonna della riga
        /// </summary>
        /// <param name="length">Numero di colonne</param>
        public TerminalHistoryRowState(int length)
        {
            this.Archived = new bool[length];
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Recupera lo stato soltanto finché la riga conserva la propria cache
        /// </summary>
        /// <param name="line">Riga reale del buffer</param>
        /// <returns>Provenienza valida della riga corrente</returns>
        public static TerminalHistoryRowState Get(BufferLine line)
        {
            if (line.Cache is TerminalHistoryRowState state)
                return state;
            state = new TerminalHistoryRowState(line.Length);
            line.Cache = state;
            return state;
        }

        /// <summary>
        /// Estrae i frammenti ancora mutabili senza confrontare testo di righe diverse
        /// </summary>
        /// <param name="snapshot">Contenuto immutabile da filtrare</param>
        /// <param name="selected">Colonne da consolidare, oppure tutte se null</param>
        /// <returns>Frammenti nell'ordine originale</returns>
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
        /// Consolida anche il padding, che il reflow può incorporare in una riga mista
        /// </summary>
        public void Commit()
        {
            Array.Fill(this.Archived, true);
            this.EmptyArchived = true;
        }

        #endregion
    }

    /// <summary>
    /// Trasporta la provenienza usando Resize pubblico su una copia con marcatori numerici
    /// </summary>
    internal sealed class TerminalHistoryReflow
    {
        #region Variabili di classe

        /// <summary>Buffer reale, ridimensionato dal chiamante tra costruzione e Complete</summary>
        private readonly TerminalBuffer _buffer;

        /// <summary>Copia marcata necessaria soltanto per il reflow delle colonne normali</summary>
        private readonly TerminalBuffer _shadow;

        /// <summary>Provenienza congelata prima del resize</summary>
        private readonly List<SourceRow> _sources = new List<SourceRow>();

        /// <summary>Identità delle righe quando il resize non esegue reflow</summary>
        private readonly Dictionary<BufferLine, int> _indices = new Dictionary<BufferLine, int>();

        #endregion

        #region Costruttore

        /// <summary>
        /// Congela soltanto gli snapshot del viewport; lo scrollback richiede solo bit di provenienza
        /// </summary>
        /// <param name="buffer">Buffer normale o alternate della sessione</param>
        /// <param name="cols">Nuova larghezza</param>
        /// <param name="rows">Nuova altezza</param>
        /// <param name="normal">True per il buffer che supporta il reflow</param>
        /// <param name="serialize">Serializzatore del protocollo Bivium</param>
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
                // Entrambi i metadati impediscono il reflow del gruppo nella stessa API pubblica
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
        /// Archivia ciò che il resize sposta fuori dal viewport e riassocia le celle superstiti
        /// </summary>
        /// <param name="archive">Archivio segmentato della sessione</param>
        public void Complete(TerminalHistoryArchive archive)
        {
            for (int row = this._buffer.BaseY; row < Math.Min(this._buffer.Lines.Length, this._buffer.BaseY + this._buffer.Rows); row++)
            {
                for (int column = 0; column < this._buffer.Lines[row].Length; column++)
                    if (this.TryGetSource(row, column, out SourceRow source, out int sourceColumn))
                        source.Visible[sourceColumn] = true;
            }

            // L'archivio è append-only: se sparisce un suffisso, consolida anche il prefisso
            // ancora visibile. Le sue celle restano a schermo ma l'export non le ripete
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
        /// Legge l'identità dal marcatore, mai dal testo o dal colore del terminale reale
        /// </summary>
        /// <param name="row">Riga dopo il resize</param>
        /// <param name="column">Colonna dopo il resize</param>
        /// <param name="source">Riga originaria</param>
        /// <param name="sourceColumn">Colonna originaria</param>
        /// <returns>True per una cella superstite con provenienza nota</returns>
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
                // OSC 66 può cancellare un run durante il clipping: quella cella non è sopravvissuta
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
        /// Una sola allocazione di identità per riga, senza oggetti o copie di testo per cella
        /// </summary>
        private sealed class SourceRow
        {
            /// <summary>
            /// Provenienza già consolidata nella riga originaria
            /// </summary>
            public TerminalHistoryRowState State { get; set; }

            /// <summary>
            /// Snapshot della riga originaria quando apparteneva al viewport
            /// </summary>
            public TerminalLineSnapshot Snapshot { get; set; }

            /// <summary>
            /// Celle originarie ancora visibili dopo il resize
            /// </summary>
            public bool[] Visible { get; set; }
        }

        #endregion
    }
}
