using Bivium.Models;
using System.Collections.Generic;

namespace Bivium.Services
{
    /// <summary>Large contents owned by the workspace, accessible only under its lock</summary>
    internal sealed class DesktopSessionRuntime
    {
        /// <summary>Editor document, independent of the circuit</summary>
        internal EditorSessionSnapshot Editor { get; set; }
        /// <summary>Journal owned by the same document session, not by the Monaco circuit</summary>
        internal EditorHistoryRuntime EditorHistory { get; set; }
        /// <summary>Unconfirmed path of the left panel</summary>
        internal WorkspacePanelPathDraft LeftPathDraft { get; set; }
        /// <summary>Unconfirmed path of the right panel</summary>
        internal WorkspacePanelPathDraft RightPathDraft { get; set; }
        /// <summary>Last context menu opening with captured sources</summary>
        internal WorkspaceContextMenuDraft ContextMenu { get; set; }
        /// <summary>Draft of the current dialog owner; contains no form data</summary>
        internal Dictionary<string, WorkspaceDialogVisualDraft> Dialogs { get; } = new Dictionary<string, WorkspaceDialogVisualDraft>();
        /// <summary>Renamer draft, independent of the circuit</summary>
        internal RenamerSessionSnapshot Renamer { get; set; }
        /// <summary>Single browser view for each terminal, distinct from the terminal runtime data</summary>
        internal Dictionary<int, WorkspaceTerminalViewState> TerminalViews { get; } = new Dictionary<int, WorkspaceTerminalViewState>();
        /// <summary>View of the terminal window strip</summary>
        internal WorkspaceTerminalStripViewState TerminalStrip { get; set; } = new WorkspaceTerminalStripViewState(0);
        /// <summary>Popup of the current Renamer session only</summary>
        internal WorkspaceRenamerViewState RenamerView { get; set; }

        /// <summary>Releases the contents only on explicit reset or workspace stop</summary>
        internal void Clear()
        {
            this.Editor = null;
            this.EditorHistory = null;
            this.LeftPathDraft = null;
            this.RightPathDraft = null;
            this.ContextMenu = null;
            this.Dialogs.Clear();
            this.Renamer = null;
            this.RenamerView = null;
            this.TerminalViews.Clear();
            this.TerminalStrip = new WorkspaceTerminalStripViewState(0);
        }
    }
}
