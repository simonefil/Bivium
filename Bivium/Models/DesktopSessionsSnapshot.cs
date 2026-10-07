using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Bivium.Models
{
    /// <summary>Immutable metadata of the desktop sessions and internal clipboard</summary>
    public sealed record DesktopSessionsSnapshot
    {
        /// <summary>Creates a lightweight projection without editor content or renamer draft</summary>
        /// <param name="editorId">Identity of the editor session</param>
        /// <param name="editorTitle">Editor title</param>
        /// <param name="editorDirty">Document differing from the saved baseline</param>
        /// <param name="renamerId">Identity of the renamer session</param>
        /// <param name="clipboardPaths">Sources of the internal clipboard</param>
        /// <param name="clipboardIsCut">Cut mode</param>
        public DesktopSessionsSnapshot(Guid editorId = default, string editorTitle = "", bool editorDirty = false, Guid renamerId = default, IEnumerable<string> clipboardPaths = null, bool clipboardIsCut = false)
        {
            this.EditorId = editorId;
            this.EditorTitle = editorTitle ?? "";
            this.EditorDirty = editorDirty;
            this.RenamerId = renamerId;
            this.ClipboardPaths = new ReadOnlyCollection<string>(clipboardPaths == null ? new List<string>() : new List<string>(clipboardPaths));
            this.ClipboardIsCut = clipboardIsCut;
        }

        /// <summary>Editor identity, empty when closed</summary>
        public Guid EditorId { get; }
        /// <summary>Title derived from the server document</summary>
        public string EditorTitle { get; }
        /// <summary>Dirty state derived from the content and the baseline</summary>
        public bool EditorDirty { get; }
        /// <summary>Renamer identity, empty when closed</summary>
        public Guid RenamerId { get; }
        /// <summary>Clipboard paths, separate from the operating system clipboard</summary>
        public IReadOnlyList<string> ClipboardPaths { get; }
        /// <summary>True for cut, false for copy</summary>
        public bool ClipboardIsCut { get; }
    }

    /// <summary>Immutable document read only by the authorized adapter, not by the global snapshot</summary>
    public sealed record EditorSessionSnapshot(Guid Id, long Revision, string FilePath, string Content, string SavedContent, string ViewState, long SavedRevision = 0, string ModelEol = "")
    {
        /// <summary>Dirty computed against the baseline actually committed</summary>
        public bool IsDirty => !string.Equals(this.Content, this.SavedContent, StringComparison.Ordinal);
    }

    /// <summary>Immutable renamer draft; materialized JSON includes entries, stack, form and preview</summary>
    public sealed record RenamerSessionSnapshot(Guid Id, long Revision, string Draft);

    /// <summary>Checkpoint received through Blazor's native JS interop streaming</summary>
    public sealed class EditorCheckpoint
    {
        /// <summary>Current text</summary>
        public string Content { get; set; } = "";
        /// <summary>Serialized Monaco viewstate</summary>
        public string ViewState { get; set; } = "";
        /// <summary>Journal delta in the same commit as the content and viewstate</summary>
        public EditorHistoryMutation[] HistoryMutations { get; set; }
        /// <summary>Number of units applied after the deltas</summary>
        public int HistoryCursor { get; set; }
    }

    /// <summary>Typed payload of the renamer draft, rebuilt without recomputing the preview</summary>
    public sealed class RenamerDraft
    {
        /// <summary>Original entries</summary>
        public List<FileSystemEntry> Entries { get; set; } = new List<FileSystemEntry>();
        /// <summary>Ordered stack of methods</summary>
        public List<RenameMethod> Methods { get; set; } = new List<RenameMethod>();
        /// <summary>Exact preview, including random results</summary>
        public List<RenamePreviewItem> Preview { get; set; } = new List<RenamePreviewItem>();
        /// <summary>Method being edited</summary>
        public RenameMethod EditingMethod { get; set; } = new RenameMethod();
        /// <summary>Selected type</summary>
        public RenameMethodType SelectedMethodType { get; set; }
        /// <summary>Parameter error</summary>
        public string ParamsError { get; set; } = "";
        /// <summary>Displayed state</summary>
        public string StatusText { get; set; } = "";
        /// <summary>Partial outcome already produced by the existing workflow</summary>
        public bool DidRename { get; set; }
    }
}
