using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Bivium.Models
{
    /// <summary>Metadati immutabili delle sessioni desktop e clipboard interna</summary>
    public sealed record DesktopSessionsSnapshot
    {
        /// <summary>Crea una proiezione leggera senza contenuto editor o draft renamer</summary>
        /// <param name="editorId">Identità della sessione editor</param>
        /// <param name="editorTitle">Titolo editor</param>
        /// <param name="editorDirty">Documento diverso dalla baseline salvata</param>
        /// <param name="renamerId">Identità della sessione renamer</param>
        /// <param name="clipboardPaths">Sorgenti della clipboard interna</param>
        /// <param name="clipboardIsCut">Modalità cut</param>
        public DesktopSessionsSnapshot(Guid editorId = default, string editorTitle = "", bool editorDirty = false, Guid renamerId = default, IEnumerable<string> clipboardPaths = null, bool clipboardIsCut = false)
        {
            this.EditorId = editorId;
            this.EditorTitle = editorTitle ?? "";
            this.EditorDirty = editorDirty;
            this.RenamerId = renamerId;
            this.ClipboardPaths = new ReadOnlyCollection<string>(clipboardPaths == null ? new List<string>() : new List<string>(clipboardPaths));
            this.ClipboardIsCut = clipboardIsCut;
        }

        /// <summary>Identità editor, vuota se chiuso</summary>
        public Guid EditorId { get; }
        /// <summary>Titolo derivato dal documento server</summary>
        public string EditorTitle { get; }
        /// <summary>Stato dirty derivato dal contenuto e dalla baseline</summary>
        public bool EditorDirty { get; }
        /// <summary>Identità renamer, vuota se chiuso</summary>
        public Guid RenamerId { get; }
        /// <summary>Paths della clipboard, separata dalla clipboard del sistema operativo</summary>
        public IReadOnlyList<string> ClipboardPaths { get; }
        /// <summary>True per cut, false per copy</summary>
        public bool ClipboardIsCut { get; }
    }

    /// <summary>Documento immutabile letto solo dall'adapter autorizzato, non dallo snapshot globale</summary>
    public sealed record EditorSessionSnapshot(Guid Id, long Revision, string FilePath, string Content, string SavedContent, string ViewState, long SavedRevision = 0, string ModelEol = "")
    {
        /// <summary>Dirty calcolato sulla baseline effettivamente committata</summary>
        public bool IsDirty => !string.Equals(this.Content, this.SavedContent, StringComparison.Ordinal);
    }

    /// <summary>Draft renamer immutabile; JSON materializzato comprende entry, stack, form e preview</summary>
    public sealed record RenamerSessionSnapshot(Guid Id, long Revision, string Draft);

    /// <summary>Checkpoint ricevuto attraverso lo streaming JS interop nativo di Blazor</summary>
    public sealed class EditorCheckpoint
    {
        /// <summary>Testo corrente</summary>
        public string Content { get; set; } = "";
        /// <summary>Viewstate Monaco serializzato</summary>
        public string ViewState { get; set; } = "";
        /// <summary>Delta del journal nello stesso commit del contenuto e viewstate</summary>
        public EditorHistoryMutation[] HistoryMutations { get; set; }
        /// <summary>Numero di unità applicate dopo i delta</summary>
        public int HistoryCursor { get; set; }
    }

    /// <summary>Payload tipizzato del draft renamer, ricostruito senza ricalcolare la preview</summary>
    public sealed class RenamerDraft
    {
        /// <summary>Entry originali</summary>
        public List<FileSystemEntry> Entries { get; set; } = new List<FileSystemEntry>();
        /// <summary>Stack ordinato dei metodi</summary>
        public List<RenameMethod> Methods { get; set; } = new List<RenameMethod>();
        /// <summary>Preview esatta, inclusi risultati casuali</summary>
        public List<RenamePreviewItem> Preview { get; set; } = new List<RenamePreviewItem>();
        /// <summary>Metodo in compilazione</summary>
        public RenameMethod EditingMethod { get; set; } = new RenameMethod();
        /// <summary>Tipo selezionato</summary>
        public RenameMethodType SelectedMethodType { get; set; }
        /// <summary>Errore dei parametri</summary>
        public string ParamsError { get; set; } = "";
        /// <summary>Stato visualizzato</summary>
        public string StatusText { get; set; } = "";
        /// <summary>Esito parziale già prodotto dal workflow esistente</summary>
        public bool DidRename { get; set; }
    }
}
