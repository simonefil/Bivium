using Bivium.Models;
using System.Collections.Generic;

namespace Bivium.Services
{
    /// <summary>Contenuti corposi posseduti dal workspace, accessibili soltanto sotto il suo lock</summary>
    internal sealed class DesktopSessionRuntime
    {
        /// <summary>Documento editor, indipendente dal circuito</summary>
        internal EditorSessionSnapshot Editor { get; set; }
        /// <summary>Journal posseduto dalla stessa document session, non dal circuito Monaco</summary>
        internal EditorHistoryRuntime EditorHistory { get; set; }
        /// <summary>Path non confermato del pannello sinistro</summary>
        internal WorkspacePanelPathDraft LeftPathDraft { get; set; }
        /// <summary>Path non confermato del pannello destro</summary>
        internal WorkspacePanelPathDraft RightPathDraft { get; set; }
        /// <summary>Menu applicativo visuale, separato dai comandi</summary>
        internal WorkspaceMenuDraft Menu { get; set; } = new WorkspaceMenuDraft(0, []);
        /// <summary>Ultima apertura context menu con sorgenti catturate</summary>
        internal WorkspaceContextMenuDraft ContextMenu { get; set; }
        /// <summary>Draft dell'owner dialog corrente; non contiene dati di form</summary>
        internal Dictionary<string, WorkspaceDialogVisualDraft> Dialogs { get; } = new Dictionary<string, WorkspaceDialogVisualDraft>();
        /// <summary>Draft renamer, indipendente dal circuito</summary>
        internal RenamerSessionSnapshot Renamer { get; set; }
        /// <summary>Vista browser unica per ciascun terminale, distinta dai dati del runtime terminale</summary>
        internal Dictionary<int, WorkspaceTerminalViewState> TerminalViews { get; } = new Dictionary<int, WorkspaceTerminalViewState>();
        /// <summary>Vista della strip della finestra terminale</summary>
        internal WorkspaceTerminalStripViewState TerminalStrip { get; set; } = new WorkspaceTerminalStripViewState(0);
        /// <summary>Popup della sola sessione Renamer corrente</summary>
        internal WorkspaceRenamerViewState RenamerView { get; set; }

        /// <summary>Rilascia i contenuti soltanto al reset esplicito o allo stop del workspace</summary>
        internal void Clear()
        {
            this.Editor = null;
            this.EditorHistory = null;
            this.LeftPathDraft = null;
            this.RightPathDraft = null;
            this.Menu = new WorkspaceMenuDraft(0, []);
            this.ContextMenu = null;
            this.Dialogs.Clear();
            this.Renamer = null;
            this.RenamerView = null;
            this.TerminalViews.Clear();
            this.TerminalStrip = new WorkspaceTerminalStripViewState(0);
        }
    }
}
