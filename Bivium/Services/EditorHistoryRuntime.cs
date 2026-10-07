using Bivium.Models;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

namespace Bivium.Services
{
    /// <summary>Document journal; all reads and commits happen under the workspace lock</summary>
    internal sealed class EditorHistoryRuntime
    {
        /// <summary>Authoritative units; the prefix is not copied at every checkpoint</summary>
        private readonly List<EditorHistoryUnit> _units = new List<EditorHistoryUnit>();
        /// <summary>Number of applied units</summary>
        private int _cursor;

        /// <summary>Materializes the history only for authorized hydration</summary>
        /// <returns>Immutable snapshot separate from the document</returns>
        internal EditorHistorySnapshot Capture() => new EditorHistorySnapshot(this._units.ToImmutableArray(), this._cursor);

        /// <summary>Validates the delta on a provisional tail and commits only if the final content matches</summary>
        /// <param name="before">Acknowledged content</param>
        /// <param name="after">Content declared by the checkpoint</param>
        /// <param name="mutations">Ordered deltas, without full history per keypress</param>
        /// <param name="expectedCursor">Cursor declared after the deltas</param>
        /// <returns>True only for consistent content and history</returns>
        internal bool TryApply(string before, string after, EditorHistoryMutation[] mutations, int expectedCursor)
        {
            if (mutations == null)
                return false;
            Dictionary<int, EditorHistoryUnit> tail = new Dictionary<int, EditorHistoryUnit>();
            int count = this._units.Count;
            int cursor = this._cursor;
            string content = before;
            EditorHistoryUnit Read(int index) => tail.TryGetValue(index, out EditorHistoryUnit staged) ? staged : this._units[index];
            try
            {
                foreach (EditorHistoryMutation mutation in mutations)
                {
                    if (mutation == null)
                        return false;
                    if (mutation.Kind == "Append")
                    {
                        EditorHistoryUnit unit = mutation.Unit;
                        if (unit == null || unit.GroupId == Guid.Empty || unit.Batches.IsDefaultOrEmpty || unit.BeforeViewState == null || unit.AfterViewState == null || unit.AfterSelections == null)
                            return false;
                        foreach (EditorHistoryBatch batch in unit.Batches)
                            content = ApplyBatch(content, batch, false);
                        bool merge = cursor == count && cursor > 0 && Read(cursor - 1).GroupId == unit.GroupId;
                        // An edit after undo truncates only the redo tail
                        count = cursor;
                        foreach (int index in tail.Keys.Where(index => index >= count).ToArray())
                            tail.Remove(index);
                        if (merge)
                        {
                            EditorHistoryUnit previous = Read(cursor - 1);
                            tail[cursor - 1] = unit with { Batches = previous.Batches.AddRange(unit.Batches), BeforeViewState = previous.BeforeViewState };
                        }
                        else
                        {
                            tail[count++] = unit;
                            cursor++;
                        }
                    }
                    else if (mutation.Kind == "Undo" && cursor > 0)
                    {
                        EditorHistoryUnit unit = Read(--cursor);
                        for (int index = unit.Batches.Length - 1; index >= 0; index--)
                            content = ApplyBatch(content, unit.Batches[index], true);
                    }
                    else if (mutation.Kind == "Redo" && cursor < count)
                    {
                        foreach (EditorHistoryBatch batch in Read(cursor++).Batches)
                            content = ApplyBatch(content, batch, false);
                    }
                    else
                        return false;
                }
            }
            catch (ArgumentException) { return false; }
            if (cursor != expectedCursor || !string.Equals(content, after, StringComparison.Ordinal))
                return false;
            if (this._units.Count > count)
                this._units.RemoveRange(count, this._units.Count - count);
            foreach (KeyValuePair<int, EditorHistoryUnit> item in tail.OrderBy(item => item.Key))
            {
                if (item.Key < this._units.Count)
                    this._units[item.Key] = item.Value;
                else
                    this._units.Add(item.Value);
            }
            this._cursor = cursor;
            return true;
        }

        /// <summary>Applies a simultaneous batch, also verifying the removed text; inversion without native history</summary>
        /// <param name="content">Text on which to verify the offsets</param>
        /// <param name="batch">Replacements of the same document version</param>
        /// <param name="inverse">True to apply the inverse during undo</param>
        /// <returns>Resulting text</returns>
        private static string ApplyBatch(string content, EditorHistoryBatch batch, bool inverse)
        {
            if (batch == null || batch.Changes.IsDefaultOrEmpty || batch.Changes.Any(change => change == null || change.Removed == null || change.Inserted == null))
                throw new ArgumentException("Invalid editor history batch");
            StringBuilder result = new StringBuilder();
            long shift = 0;
            int consumed = 0;
            foreach (EditorTextChange change in batch.Changes.OrderBy(change => change.Offset))
            {
                long offset = change.Offset + (inverse ? shift : 0);
                string removed = inverse ? change.Inserted : change.Removed;
                string inserted = inverse ? change.Removed : change.Inserted;
                if (change.Offset < 0 || offset < consumed || offset > content.Length || removed.Length > content.Length - offset || string.CompareOrdinal(content, (int)offset, removed, 0, removed.Length) != 0)
                    throw new ArgumentException("Editor history does not match content");
                result.Append(content, consumed, (int)offset - consumed);
                result.Append(inserted);
                consumed = (int)offset + removed.Length;
                shift += (long)change.Inserted.Length - change.Removed.Length;
            }
            result.Append(content, consumed, content.Length - consumed);
            return result.ToString();
        }
    }
}
