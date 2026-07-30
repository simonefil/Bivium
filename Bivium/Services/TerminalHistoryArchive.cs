using Bivium.Models;
using System;
using System.Collections.Generic;
using System.Threading;

namespace Bivium.Services
{
    /// <summary>
    /// Segmented terminal archive indexed for one session
    /// </summary>
    internal sealed class TerminalHistoryArchive
    {
        #region Class Variables

        /// <summary>
        /// Maximum serialized session budget
        /// </summary>
        private readonly long _maxBytes;

        /// <summary>
        /// Target serialized segment size
        /// </summary>
        private readonly int _segmentTargetBytes;

        /// <summary>
        /// Segments ordered by logical offset
        /// </summary>
        private readonly LinkedList<HistorySegment> _segments = new LinkedList<HistorySegment>();

        /// <summary>
        /// Next logical offset to assign
        /// </summary>
        private long _nextIndex = 0;

        /// <summary>
        /// Serialized bytes currently retained
        /// </summary>
        private long _retainedBytes = 0;

        /// <summary>
        /// First logical offset still available
        /// </summary>
        private long _startIndex = 0;

        /// <summary>
        /// Whether at least one previous row was removed
        /// </summary>
        private bool _truncated = false;

        #endregion

        #region Constructor

        /// <summary>
        /// Creates a bounded archive
        /// </summary>
        /// <param name="maxBytes">Maximum session budget</param>
        /// <param name="segmentTargetBytes">Target segment size</param>
        public TerminalHistoryArchive(long maxBytes, int segmentTargetBytes)
        {
            this._maxBytes = Math.Max(1024 * 1024, maxBytes);
            this._segmentTargetBytes = Math.Max(16 * 1024, segmentTargetBytes);
        }

        #endregion

        #region Properties

        /// <summary>
        /// First logical row still available
        /// </summary>
        public long StartIndex { get { return this._startIndex; } }

        /// <summary>
        /// Exclusive offset of the last inserted row
        /// </summary>
        public long EndIndex { get { return this._nextIndex; } }

        /// <summary>
        /// Serialized bytes currently retained
        /// </summary>
        public long RetainedBytes { get { return this._retainedBytes; } }

        /// <summary>
        /// Whether previous history was truncated
        /// </summary>
        public bool Truncated { get { return this._truncated; } }

        /// <summary>
        /// Timestamp of the oldest segment
        /// </summary>
        public DateTime OldestTimestamp
        {
            get
            {
                return this._segments.First == null ? DateTime.MaxValue : this._segments.First.Value.CreatedAtUtc;
            }
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Adds a final row and assigns its logical offset
        /// </summary>
        /// <param name="line">Serialized row</param>
        public void Append(TerminalLineSnapshot line)
        {
            if (line == null)
                throw new ArgumentNullException(nameof(line));

            line.Index = this._nextIndex++;
            HistorySegment segment = this._segments.Last == null ? null : this._segments.Last.Value;
            if (segment == null || segment.SerializedBytes + line.SerializedBytes > this._segmentTargetBytes)
            {
                segment = new HistorySegment(line.Index);
                this._segments.AddLast(segment);
            }

            segment.Lines.Add(line);
            segment.SerializedBytes += line.SerializedBytes;
            segment.EndIndex = line.Index + 1;
            this._retainedBytes += line.SerializedBytes;
            this.TrimToPerTabBudget();
        }

        /// <summary>
        /// Returns a bounded page for a logical range
        /// </summary>
        /// <param name="start">First requested row</param>
        /// <param name="count">Maximum row count</param>
        /// <param name="revision">Session revision</param>
        /// <param name="cancellationToken">Current request token</param>
        /// <returns>History page</returns>
        public TerminalHistoryPage GetPage(long start, int count, long revision, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long safeStart = Math.Clamp(start, this._startIndex, this._nextIndex);
            int safeCount = Math.Clamp(count, 1, 1000);
            List<TerminalLineSnapshot> lines = new List<TerminalLineSnapshot>();

            // Implicitly skip previous segments and stop scanning when the page is complete
            LinkedListNode<HistorySegment> node = this._segments.First;
            while (node != null && lines.Count < safeCount)
            {
                cancellationToken.ThrowIfCancellationRequested();
                HistorySegment segment = node.Value;
                if (segment.EndIndex > safeStart)
                {
                    for (int i = 0; i < segment.Lines.Count && lines.Count < safeCount; i++)
                    {
                        TerminalLineSnapshot line = segment.Lines[i];
                        if (line.Index >= safeStart)
                            lines.Add(line);
                    }
                }

                node = node.Next;
            }

            TerminalHistoryPage result = new TerminalHistoryPage();
            result.Start = safeStart;
            result.End = this._nextIndex;
            result.Revision = revision;
            result.Truncated = this._truncated;
            result.Lines = lines.AsReadOnly();
            return result;
        }

        /// <summary>
        /// Copies retained row references for a point-in-time export
        /// </summary>
        /// <param name="cancellationToken">Current request token</param>
        /// <returns>All retained rows in logical order</returns>
        public IReadOnlyList<TerminalLineSnapshot> GetLinesSnapshot(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<TerminalLineSnapshot> lines = new List<TerminalLineSnapshot>();
            LinkedListNode<HistorySegment> node = this._segments.First;
            while (node != null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lines.AddRange(node.Value.Lines);
                node = node.Next;
            }

            return lines.AsReadOnly();
        }

        /// <summary>
        /// Searches a bounded number of rows without retaining the lock during a long search
        /// </summary>
        /// <param name="query">Text to search</param>
        /// <param name="start">Starting offset</param>
        /// <param name="forward">Search direction</param>
        /// <param name="count">Maximum rows to inspect</param>
        /// <param name="cancellationToken">Current request token</param>
        /// <returns>Result and cursor for the next page</returns>
        public TerminalSearchPage SearchPage(string query, long start, bool forward, int count, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int remaining = Math.Clamp(count, 1, 2000);
            TerminalSearchPage result = new TerminalSearchPage();
            result.Next = start;
            if (string.IsNullOrEmpty(query) || this._segments.Count == 0)
            {
                result.Complete = true;
                return result;
            }

            if (forward)
            {
                // Progressive search returns the next cursor without materializing all history
                long safeStart = Math.Clamp(start, this._startIndex, this._nextIndex);
                LinkedListNode<HistorySegment> node = this._segments.First;
                while (node != null && remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (node.Value.EndIndex > safeStart)
                    {
                        for (int i = 0; i < node.Value.Lines.Count && remaining > 0; i++)
                        {
                            TerminalLineSnapshot line = node.Value.Lines[i];
                            if (line.Index < safeStart)
                                continue;
                            remaining--;
                            result.Next = line.Index + 1;
                            if (line.Text.Contains(query, StringComparison.OrdinalIgnoreCase))
                            {
                                result.Found = line.Index;
                                result.Complete = true;
                                return result;
                            }
                        }
                    }
                    node = node.Next;
                }
                result.Complete = result.Next >= this._nextIndex;
            }
            else
            {
                // The reverse branch traverses segments and rows in descending order with the same budget
                long safeStart = Math.Clamp(start, this._startIndex, Math.Max(this._startIndex, this._nextIndex - 1));
                LinkedListNode<HistorySegment> node = this._segments.Last;
                while (node != null && remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (node.Value.StartIndex <= safeStart)
                    {
                        for (int i = node.Value.Lines.Count - 1; i >= 0 && remaining > 0; i--)
                        {
                            TerminalLineSnapshot line = node.Value.Lines[i];
                            if (line.Index > safeStart)
                                continue;
                            remaining--;
                            result.Next = line.Index - 1;
                            if (line.Text.Contains(query, StringComparison.OrdinalIgnoreCase))
                            {
                                result.Found = line.Index;
                                result.Complete = true;
                                return result;
                            }
                        }
                    }
                    node = node.Previous;
                }
                result.Complete = result.Next < this._startIndex;
            }

            return result;
        }

        /// <summary>
        /// Removes the oldest segment to satisfy the global budget
        /// </summary>
        /// <returns>Removed bytes</returns>
        public long TrimOldestSegment()
        {
            if (this._segments.First == null)
                return 0;

            HistorySegment segment = this._segments.First.Value;
            this._segments.RemoveFirst();
            this._retainedBytes -= segment.SerializedBytes;
            this._startIndex = segment.EndIndex;
            this._truncated = true;
            return segment.SerializedBytes;
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Applies the per-tab budget by removing the oldest segments
        /// </summary>
        private void TrimToPerTabBudget()
        {
            // Remove complete segments first to keep rotation cost independent from total size
            while (this._retainedBytes > this._maxBytes && this._segments.Count > 1)
            {
                this.TrimOldestSegment();
            }

            if (this._retainedBytes > this._maxBytes && this._segments.First != null)
            {
                // Trim an oversized single segment by row only as a last resort
                HistorySegment segment = this._segments.First.Value;
                while (this._retainedBytes > this._maxBytes && segment.Lines.Count > 0)
                {
                    TerminalLineSnapshot line = segment.Lines[0];
                    segment.Lines.RemoveAt(0);
                    segment.SerializedBytes -= line.SerializedBytes;
                    this._retainedBytes -= line.SerializedBytes;
                    this._startIndex = line.Index + 1;
                    this._truncated = true;
                }

                if (segment.Lines.Count == 0)
                    this._segments.RemoveFirst();
            }
        }

        #endregion

        #region Nested Classes

        /// <summary>
        /// Indexed history segment
        /// </summary>
        private sealed class HistorySegment
        {
            /// <summary>
            /// Creates a segment starting at a logical offset
            /// </summary>
            /// <param name="startIndex">First segment offset</param>
            public HistorySegment(long startIndex)
            {
                this.StartIndex = startIndex;
                this.EndIndex = startIndex;
                this.CreatedAtUtc = DateTime.UtcNow;
            }

            /// <summary>
            /// First logical segment offset
            /// </summary>
            public long StartIndex { get; }

            /// <summary>
            /// Final exclusive logical segment offset
            /// </summary>
            public long EndIndex { get; set; }

            /// <summary>
            /// Segment creation UTC instant
            /// </summary>
            public DateTime CreatedAtUtc { get; }

            /// <summary>
            /// Serialized bytes retained in the segment
            /// </summary>
            public int SerializedBytes { get; set; }

            /// <summary>
            /// Ordered segment rows
            /// </summary>
            public List<TerminalLineSnapshot> Lines { get; } = new List<TerminalLineSnapshot>();
        }

        #endregion
    }
}
