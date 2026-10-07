using Bivium.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Bivium.Services
{
    public sealed partial class BiviumWorkspaceService
    {
        #region Variabili di classe

        /// <summary>Runtime dei draft separato dalla proiezione globale leggera</summary>
        private readonly DesktopSessionRuntime _desktopRuntime = new DesktopSessionRuntime();

        #endregion

        #region Metodi pubblici

        /// <summary>Legge la vista della sessione terminale già verificata dal suo adapter runtime</summary>
        /// <param name="token">Lease del mount</param>
        /// <param name="sessionId">Identità stabile terminale</param>
        /// <returns>Vista oppure default, mai dati del terminale</returns>
        internal WorkspaceTerminalViewState GetTerminalViewState(WorkspaceClientToken token, int sessionId)
        {
            lock (this._lock)
                return this.ValidateLeaseLocked(token) && !this.IsStopped && sessionId > 0 ? this._desktopRuntime.TerminalViews.GetValueOrDefault(sessionId) ?? new WorkspaceTerminalViewState(0, sessionId, 0, false, 0, 0, 0, true) : null;
        }

        /// <summary>CAS browser separato dal journal del terminal runtime, ammesso nel drain</summary>
        /// <param name="token">Lease publisher</param>
        /// <param name="draft">Vista catturata</param>
        /// <param name="owner">Snapshot interno verificato dal runtime, non ricevuto dal browser</param>
        /// <returns>Revisione acknowledged oppure -1</returns>
        internal long PublishTerminalViewState(WorkspaceClientToken token, WorkspaceTerminalViewState draft, TerminalSessionSnapshot owner)
        {
            lock (this._lock)
            {
                if (!this.ValidateLeaseLocked(token) || this.IsStopped || draft == null || owner?.Id != draft.SessionId || draft.ObservedRevision < 0 || draft.ObservedRevision > owner.Revision || draft.TopRow < 0 || !double.IsFinite(draft.RowFraction) || draft.RowFraction < 0 || draft.RowFraction >= 1 || !double.IsFinite(draft.HorizontalCells) || draft.HorizontalCells < 0 || draft.PromptRow < -1 || (draft.SelectionAnchor == null) != (draft.SelectionFocus == null) || (draft.SelectionAnchor != null && (draft.SelectionAnchor.Row < 0 || draft.SelectionAnchor.Column < 0 || draft.SelectionFocus.Row < 0 || draft.SelectionFocus.Column < 0)))
                    return -1;
                if ((this._desktopRuntime.TerminalViews.GetValueOrDefault(draft.SessionId)?.Revision ?? 0) != draft.Revision)
                    return -1;
                // Il runtime può avanzare o applicare retention mentre il checkpoint è in transito.
                // Un cambio buffer resta esplicito: sarà l'hydration a scartare gli anchor incompatibili.
                if (draft.AlternateBuffer == owner.Screen.AlternateBuffer)
                {
                    long minimum = draft.AlternateBuffer ? 0 : owner.HistoryStart;
                    long maximum = Math.Max(minimum, (draft.AlternateBuffer ? 0 : owner.HistoryEnd) + owner.Screen.Lines.Count - 1);
                    int lastColumn = Math.Max(0, owner.Cols - 1);
                    draft = draft with
                    {
                        TopRow = Math.Clamp(draft.TopRow, minimum, maximum),
                        PromptRow = draft.AlternateBuffer || draft.PromptRow < 0 ? -1 : Math.Clamp(draft.PromptRow, minimum, maximum),
                        SelectionAnchor = draft.SelectionAnchor == null ? null : new WorkspaceTerminalPosition(Math.Clamp(draft.SelectionAnchor.Row, minimum, maximum), Math.Clamp(draft.SelectionAnchor.Column, 0, lastColumn)),
                        SelectionFocus = draft.SelectionFocus == null ? null : new WorkspaceTerminalPosition(Math.Clamp(draft.SelectionFocus.Row, minimum, maximum), Math.Clamp(draft.SelectionFocus.Column, 0, lastColumn))
                    };
                }
                this._desktopRuntime.TerminalViews[draft.SessionId] = draft with { Revision = draft.Revision + 1 };
                this.CommitDesktopLocked(this._snapshot.FloatingWindows);
                return draft.Revision + 1;
            }
        }

        /// <summary>Rilascia viste di sessioni che il terminal runtime non rappresenta più</summary>
        /// <param name="token">Lease dell'adapter</param>
        /// <param name="sessionIds">Identità lette dal runtime, mai dal checkpoint browser</param>
        internal void ReconcileTerminalViews(WorkspaceClientToken token, int[] sessionIds)
        {
            lock (this._lock)
            {
                if (!this.ValidateLeaseLocked(token) || this.IsStopped)
                    return;
                foreach (int id in this._desktopRuntime.TerminalViews.Keys.Where(id => !sessionIds.Contains(id)).ToArray())
                    this._desktopRuntime.TerminalViews.Remove(id);
            }
        }

        /// <summary>Legge lo scroll della strip senza modificare il tab attivo</summary>
        /// <param name="token">Lease del mount</param>
        /// <returns>Vista strip oppure null</returns>
        internal WorkspaceTerminalStripViewState GetTerminalStripViewState(WorkspaceClientToken token)
        {
            lock (this._lock)
                return this.ValidateLeaseLocked(token) && !this.IsStopped ? this._desktopRuntime.TerminalStrip : null;
        }

        /// <summary>CAS della sola strip; header rimossi verranno clamped nel DOM reale</summary>
        /// <param name="token">Lease publisher</param>
        /// <param name="draft">Anchor semantico e scroll</param>
        /// <returns>Revisione acknowledged oppure -1</returns>
        internal long PublishTerminalStripViewState(WorkspaceClientToken token, WorkspaceTerminalStripViewState draft)
        {
            lock (this._lock)
            {
                if (!this.ValidateLeaseLocked(token) || this.IsStopped || draft == null || draft.Revision != this._desktopRuntime.TerminalStrip.Revision || draft.AnchorSessionId < 0 || !double.IsFinite(draft.AnchorFraction) || draft.AnchorFraction < 0 || draft.AnchorFraction > 1 || !double.IsFinite(draft.Left) || !double.IsFinite(draft.Top) || draft.Left < 0 || draft.Top < 0)
                    return -1;
                this._desktopRuntime.TerminalStrip = draft with { Revision = draft.Revision + 1 };
                this.CommitDesktopLocked(this._snapshot.FloatingWindows);
                return draft.Revision + 1;
            }
        }

        /// <summary>Hydration dei popup soltanto per la stessa sessione Renamer</summary>
        /// <param name="token">Lease del mount</param>
        /// <param name="sessionId">Owner Renamer</param>
        /// <returns>Vista immutabile oppure null</returns>
        internal WorkspaceRenamerViewState GetRenamerViewState(WorkspaceClientToken token, Guid sessionId)
        {
            lock (this._lock)
                return this.ValidateLeaseLocked(token) && !this.IsStopped && this._desktopRuntime.Renamer?.Id == sessionId ? this._desktopRuntime.RenamerView?.SessionId == sessionId ? this._desktopRuntime.RenamerView : new WorkspaceRenamerViewState(0, sessionId, [], Scrolls: [], Selections: []) : null;
        }

        /// <summary>CAS dei popup, mai una modifica della form o della preview</summary>
        /// <param name="token">Lease publisher</param>
        /// <param name="draft">Popup montati della sessione</param>
        /// <returns>Revisione acknowledged oppure -1</returns>
        internal long PublishRenamerViewState(WorkspaceClientToken token, WorkspaceRenamerViewState draft)
        {
            lock (this._lock)
            {
                if (!this.ValidateLeaseLocked(token) || this.IsStopped || draft == null || this._desktopRuntime.Renamer?.Id != draft.SessionId || draft.Popups.IsDefault || draft.Popups.Select(item => item?.ControlId).Distinct().Count() != draft.Popups.Length || draft.Popups.Any(item => item == null || item.ControlId is not ("renamer-method" or "renamer-remove-mode" or "renamer-case-mode" or "renamer-case-scope" or "renamer-trim-location" or "renamer-trim-scope") || item.Popup == null || item.Popup.HighlightIndex < -1 || !double.IsFinite(item.Popup.ScrollTop) || !double.IsFinite(item.Popup.ScrollLeft) || item.Popup.ScrollTop < 0 || item.Popup.ScrollLeft < 0))
                    return -1;
                if (draft.FocusKey == null || draft.FocusKey.Length > 256 || draft.Scrolls.IsDefault || draft.Selections.IsDefault || draft.Scrolls.Select(item => item?.Key).Distinct().Count() != draft.Scrolls.Length || draft.Selections.Select(item => item?.Key).Distinct().Count() != draft.Selections.Length || draft.Scrolls.Any(item => item == null || item.Key is not ("renamer-body" or "renamer-preview" or "renamer-preview-grid" or "renamer-config" or "renamer-stack") || !double.IsFinite(item.Top) || !double.IsFinite(item.Left) || item.Top < 0 || item.Left < 0) || draft.Selections.Any(item => item == null || string.IsNullOrEmpty(item.Key) || !item.Key.StartsWith("name:renamer-", StringComparison.Ordinal) || item.Key.Length > 256 || item.Start < 0 || item.End < item.Start || item.Direction is not ("none" or "forward" or "backward")))
                    return -1;
                if ((this._desktopRuntime.RenamerView?.SessionId == draft.SessionId ? this._desktopRuntime.RenamerView.Revision : 0) != draft.Revision)
                    return -1;
                this._desktopRuntime.RenamerView = draft with { Revision = draft.Revision + 1 };
                this.CommitDesktopLocked(this._snapshot.FloatingWindows);
                return draft.Revision + 1;
            }
        }

        /// <summary>Hydration autorizzata del menu app-owned</summary>
        /// <param name="token">Lease del mount</param>
        /// <returns>Draft visuale oppure null</returns>
        internal WorkspaceMenuDraft GetMenuDraft(WorkspaceClientToken token)
        {
            lock (this._lock)
                return this.ValidateLeaseLocked(token) && !this.IsStopped ? this._desktopRuntime.Menu : null;
        }

        /// <summary>Checkpoint menu ordinato, ammesso anche durante il drain</summary>
        /// <param name="token">Lease publisher</param>
        /// <param name="draft">Identità e contesto visuale osservati</param>
        /// <returns>Nuova revisione oppure -1, senza retry implicito</returns>
        internal long PublishMenuDraft(WorkspaceClientToken token, WorkspaceMenuDraft draft)
        {
            lock (this._lock)
            {
                if (!this.ValidateLeaseLocked(token) || this.IsStopped || draft == null || draft.Revision != this._desktopRuntime.Menu.Revision || draft.OpenIds.IsDefault || draft.OpenIds.Any(id => id is not ("menu-file" or "menu-edit" or "menu-view" or "menu-settings" or "menu-help" or "menu-settings-theme")) || draft.NavigationParent == null || draft.ActiveItem == null)
                    return -1;
                this._desktopRuntime.Menu = draft with { Revision = draft.Revision + 1 };
                this.CommitDesktopLocked(this._snapshot.FloatingWindows);
                return this._desktopRuntime.Menu.Revision;
            }
        }

        /// <summary>Legge l'apertura senza rileggere entry o flag dal filesystem</summary>
        /// <param name="token">Lease del mount</param>
        /// <returns>Apertura catturata oppure null</returns>
        internal WorkspaceContextMenuDraft GetContextMenuDraft(WorkspaceClientToken token)
        {
            lock (this._lock)
                return this.ValidateLeaseLocked(token) && !this.IsStopped ? this._desktopRuntime.ContextMenu : null;
        }

        /// <summary>CAS dell'apertura, chiusura o posizione del context menu</summary>
        /// <param name="token">Lease publisher</param>
        /// <param name="draft">Stato catturato dall'owner Commander</param>
        /// <returns>Draft acknowledged oppure null</returns>
        internal WorkspaceContextMenuDraft PublishContextMenuDraft(WorkspaceClientToken token, WorkspaceContextMenuDraft draft)
        {
            lock (this._lock)
            {
                if (!this.ValidateLeaseLocked(token) || this.IsStopped || draft == null || draft.Id == Guid.Empty || draft.Revision != (this._desktopRuntime.ContextMenu?.Revision ?? 0) || draft.PanelIndex is not (0 or 1) || draft.SelectedPaths.IsDefault || draft.Entries.IsDefault || !double.IsFinite(draft.X) || !double.IsFinite(draft.Y) || !double.IsFinite(draft.ViewportWidth) || !double.IsFinite(draft.ViewportHeight))
                    return null;
                this._desktopRuntime.ContextMenu = draft with { Revision = draft.Revision + 1 };
                this.CommitDesktopLocked(this._snapshot.FloatingWindows);
                return this._desktopRuntime.ContextMenu;
            }
        }

        /// <summary>Hydration visuale legata alla domanda/sessione, mai all'ultimo dialog generico</summary>
        /// <param name="token">Lease del mount</param>
        /// <param name="draft">Identità della superficie da montare</param>
        /// <returns>Stato visuale oppure default della stessa identità</returns>
        internal WorkspaceDialogVisualDraft GetDialogVisualDraft(WorkspaceClientToken token, WorkspaceDialogVisualDraft draft)
        {
            lock (this._lock)
            {
                if (!this.ValidateLeaseLocked(token) || this.IsStopped || !this.IsDialogVisualOwnerLocked(draft))
                    return null;
                return this._desktopRuntime.Dialogs.TryGetValue(draft.Surface, out WorkspaceDialogVisualDraft current) && current.OwnerId == draft.OwnerId && current.QuestionId == draft.QuestionId && current.Phase == draft.Phase ? current with { TextareaGeometries = current.TextareaGeometries?.ToArray() } : draft with { Revision = 0 };
            }
        }

        /// <summary>CAS del solo stato visuale; non tocca draft form o runner</summary>
        /// <param name="token">Lease publisher</param>
        /// <param name="draft">Focus e scroll della superficie proprietaria</param>
        /// <returns>Nuova revisione oppure -1</returns>
        internal long PublishDialogVisualDraft(WorkspaceClientToken token, WorkspaceDialogVisualDraft draft)
        {
            lock (this._lock)
            {
                if (!this.ValidateLeaseLocked(token) || this.IsStopped || !this.IsDialogVisualOwnerLocked(draft) || draft.FocusKey == null || draft.Scrolls.IsDefault || draft.Scrolls.Any(scroll => scroll == null || scroll.Key == null || !double.IsFinite(scroll.Top) || !double.IsFinite(scroll.Left)) || draft.SelectionStart < -1 || draft.SelectionEnd < draft.SelectionStart || draft.SelectionDirection is not ("none" or "forward" or "backward") || (draft.Surface == "auth" && draft.SelectionStart != -1))
                    return -1;
                WorkspaceDialogVisualDraft current = this.GetDialogVisualDraft(token, draft);
                if (current == null || current.Revision != draft.Revision)
                    return -1;
                if (draft.FormatPopup != null && (draft.Surface != "compress" || draft.FormatPopup.HighlightIndex < -1 || draft.FormatPopup.HighlightIndex >= 6 || !double.IsFinite(draft.FormatPopup.ScrollTop) || !double.IsFinite(draft.FormatPopup.ScrollLeft) || draft.FormatPopup.ScrollTop < 0 || draft.FormatPopup.ScrollLeft < 0))
                    return -1;
                if (draft.TextareaGeometries != null && draft.TextareaGeometries.Any(geometry => draft.Surface != "extensions" || geometry == null || geometry.Key != "name:editor-extensions-textarea" || !double.IsFinite(geometry.Width) || !double.IsFinite(geometry.Height) || geometry.Width <= 0 || geometry.Height <= 0 || geometry.Resize is not ("vertical" or "horizontal" or "both")))
                    return -1;
                // Rimuove identità terminate, senza accumulare history di dialog o dati sensibili
                foreach (string key in this._desktopRuntime.Dialogs.Where(item => !this.IsDialogVisualOwnerLocked(item.Value)).Select(item => item.Key).ToArray())
                    this._desktopRuntime.Dialogs.Remove(key);
                this._desktopRuntime.Dialogs[draft.Surface] = draft with { Revision = draft.Revision + 1, TextareaGeometries = draft.TextareaGeometries?.ToArray() };
                this.CommitDesktopLocked(this._snapshot.FloatingWindows);
                return draft.Revision + 1;
            }
        }

        /// <summary>Legge il path draft della directory corrente senza eseguire navigazione</summary>
        /// <param name="token">Lease del mount</param>
        /// <param name="panelId">Identità left/right esistente</param>
        /// <returns>Draft tipizzato oppure null senza autorità</returns>
        internal WorkspacePanelPathDraft GetPanelPathDraft(WorkspaceClientToken token, string panelId)
        {
            lock (this._lock)
                return this.ValidateLeaseLocked(token) && !this.IsStopped ? this.GetPanelPathDraftLocked(panelId) : null;
        }

        /// <summary>Checkpoint visuale CAS; aggiorna il watermark globale senza rileggere i pannelli</summary>
        /// <param name="token">Lease del publisher</param>
        /// <param name="panelId">Identità left/right</param>
        /// <param name="expectedRevision">Revisione del draft osservato</param>
        /// <param name="draft">Testo, ciclo e focus del path editor</param>
        /// <returns>Draft acknowledged oppure null per publisher obsoleto</returns>
        internal WorkspacePanelPathDraft PublishPanelPathDraft(WorkspaceClientToken token, string panelId, long expectedRevision, WorkspacePanelPathDraft draft)
        {
            lock (this._lock)
            {
                WorkspacePanelPathDraft current = this.GetPanelPathDraftLocked(panelId);
                if (!this.ValidateLeaseLocked(token) || this.IsStopped || current == null || draft == null || current.Revision != expectedRevision || current.BasePath != draft.BasePath || draft.Text == null || draft.Candidates.IsDefault || draft.Matches.IsDefault || draft.SelectionStart < 0 || draft.SelectionEnd < draft.SelectionStart || draft.SelectionEnd > draft.Text.Length)
                    return null;
                WorkspacePanelPathDraft acknowledged = draft with { Revision = current.Revision + 1 };
                if (panelId == "left") this._desktopRuntime.LeftPathDraft = acknowledged;
                else this._desktopRuntime.RightPathDraft = acknowledged;
                // Il drain vede questa revisione; nessuna notifica provoca un listing per keypress
                this.CommitDesktopLocked(this._snapshot.FloatingWindows);
                return acknowledged;
            }
        }

        /// <summary>Legge il documento soltanto per il lease attivo</summary>
        /// <param name="token">Lease del lettore</param>
        /// <returns>Documento immutabile oppure null</returns>
        internal EditorSessionSnapshot GetEditorSession(WorkspaceClientToken token)
        {
            lock (this._lock)
                return this.ValidateLeaseLocked(token) ? this._desktopRuntime.Editor : null;
        }

        /// <summary>Hydration history soltanto per la stessa revisione del documento autorizzato</summary>
        /// <param name="token">Lease del mount</param>
        /// <param name="id">Documento catturato</param>
        /// <param name="revision">Revisione catturata insieme al contenuto</param>
        /// <returns>History coerente oppure null per un mount obsoleto</returns>
        internal EditorHistorySnapshot GetEditorHistory(WorkspaceClientToken token, Guid id, long revision)
        {
            lock (this._lock)
                return this.ValidateLeaseLocked(token) && this._desktopRuntime.Editor?.Id == id && this._desktopRuntime.Editor.Revision == revision ? this._desktopRuntime.EditorHistory?.Capture() : null;
        }

        /// <summary>Concorda la base Monaco prima degli edit, senza creare undo o sporcare la baseline</summary>
        /// <param name="token">Lease del mount</param>
        /// <param name="id">Documento del modello appena creato</param>
        /// <param name="revision">Revisione della hydration</param>
        /// <param name="content">getValue pubblico del modello</param>
        /// <param name="eol">getEOL pubblico effettivo</param>
        /// <returns>Sessione inizializzata oppure null per una base incoerente</returns>
        internal EditorSessionSnapshot InitializeEditorModel(WorkspaceClientToken token, Guid id, long revision, string content, string eol)
        {
            lock (this._lock)
            {
                EditorSessionSnapshot session = this._desktopRuntime.Editor;
                if (!this.ValidateLeaseLocked(token) || this.IsStopped || session?.Id != id || session.Revision != revision || (eol != "\n" && eol != "\r\n"))
                    return null;
                if (!string.IsNullOrEmpty(session.ModelEol))
                    return session.ModelEol == eol && session.Content == content ? session : null;
                if (session.Revision != 0 || session.Content != session.SavedContent || content != NormalizeEditorEol(session.Content, eol))
                    return null;
                // È una concordanza della rappresentazione iniziale, non una modifica dell'utente o del file
                session = session with { Revision = session.Revision + 1, Content = content, SavedContent = content, ModelEol = eol };
                this._desktopRuntime.Editor = session;
                return session;
            }
        }

        /// <summary>Legge il draft soltanto per il lease attivo</summary>
        /// <param name="token">Lease del lettore</param>
        /// <returns>Draft immutabile oppure null</returns>
        internal RenamerSessionSnapshot GetRenamerSession(WorkspaceClientToken token)
        {
            lock (this._lock)
                return this.ValidateLeaseLocked(token) ? this._desktopRuntime.Renamer : null;
        }

        /// <summary>Apre un documento senza sostituire una sessione già aperta</summary>
        /// <param name="token">Lease del chiamante</param>
        /// <param name="filePath">File già letto dal workflow autorizzato</param>
        /// <param name="content">Contenuto iniziale</param>
        /// <returns>Sessione autorevole</returns>
        internal EditorSessionSnapshot OpenEditorSession(WorkspaceClientToken token, string filePath, string content)
        {
            Action<BiviumWorkspaceSnapshot>[] subscribers;
            BiviumWorkspaceSnapshot snapshot;
            EditorSessionSnapshot result;
            lock (this._lock)
            {
                this.RequireDesktopLeaseLocked(token);
                if (this._desktopRuntime.Editor != null)
                    return this._desktopRuntime.Editor;
                result = new EditorSessionSnapshot(Guid.NewGuid(), 0, filePath, content ?? "", content ?? "", "");
                this._desktopRuntime.Editor = result;
                this._desktopRuntime.EditorHistory = new EditorHistoryRuntime();
                FloatingWindowsSnapshot windows = this._snapshot.FloatingWindows;
                FloatingWindowSnapshot editor = windows.Editor with { Visible = true, Minimized = false };
                snapshot = this.CommitDesktopLocked(new FloatingWindowsSnapshot(windows.Terminal, editor, windows.Renamer));
                subscribers = this.GetSubscribers();
            }
            this.NotifySubscribers(subscribers, snapshot);
            return result;
        }

        /// <summary>Apre un draft renamer già materializzato senza rigenerarne la preview</summary>
        /// <param name="token">Lease del chiamante</param>
        /// <param name="draft">Payload serializzato della form</param>
        /// <returns>Sessione autorevole</returns>
        internal RenamerSessionSnapshot OpenRenamerSession(WorkspaceClientToken token, string draft)
        {
            Action<BiviumWorkspaceSnapshot>[] subscribers;
            BiviumWorkspaceSnapshot snapshot;
            RenamerSessionSnapshot result;
            lock (this._lock)
            {
                this.RequireDesktopLeaseLocked(token);
                if (this._desktopRuntime.Renamer != null)
                    return this._desktopRuntime.Renamer;
                result = new RenamerSessionSnapshot(Guid.NewGuid(), 0, draft);
                this._desktopRuntime.Renamer = result;
                FloatingWindowsSnapshot windows = this._snapshot.FloatingWindows;
                FloatingWindowSnapshot renamer = windows.Renamer with { Visible = true, Minimized = false };
                snapshot = this.CommitDesktopLocked(new FloatingWindowsSnapshot(windows.Terminal, windows.Editor, renamer));
                subscribers = this.GetSubscribers();
            }
            this.NotifySubscribers(subscribers, snapshot);
            return result;
        }

        /// <summary>Accetta un checkpoint ordinato del documento e del viewstate</summary>
        /// <param name="token">Lease del chiamante</param>
        /// <param name="id">Identità documento</param>
        /// <param name="expectedRevision">Revisione del precedente checkpoint confermato</param>
        /// <param name="checkpoint">Contenuto, delta history, cursor e viewstate atomicamente coerenti</param>
        /// <param name="session">Sessione dopo il tentativo</param>
        /// <returns>True se accettato; nessun retry implicito sul draft</returns>
        internal bool TryUpdateEditorDraft(WorkspaceClientToken token, Guid id, long expectedRevision, EditorCheckpoint checkpoint, out EditorSessionSnapshot session)
        {
            Action<BiviumWorkspaceSnapshot>[] subscribers = Array.Empty<Action<BiviumWorkspaceSnapshot>>();
            BiviumWorkspaceSnapshot snapshot;
            lock (this._lock)
            {
                if (!this.ValidateLeaseLocked(token))
                {
                    session = null;
                    return false;
                }
                session = this._desktopRuntime.Editor;
                if (session == null || session.Id != id || session.Revision != expectedRevision)
                    return false;
                if (checkpoint == null || checkpoint.Content == null || checkpoint.ViewState == null || this._desktopRuntime.EditorHistory == null || !this._desktopRuntime.EditorHistory.TryApply(session.Content, checkpoint.Content, checkpoint.HistoryMutations, checkpoint.HistoryCursor))
                    return false;
                bool wasDirty = session.IsDirty;
                session = session with { Revision = session.Revision + 1, Content = checkpoint.Content, ViewState = checkpoint.ViewState };
                this._desktopRuntime.Editor = session;
                // Pubblica solo transizioni dirty, mai checkpoint di testo nelle notifiche globali
                if (wasDirty != session.IsDirty)
                {
                    this.CommitDesktopLocked(this._snapshot.FloatingWindows);
                    subscribers = this.GetSubscribers();
                }
                snapshot = this._snapshot;
            }
            this.NotifySubscribers(subscribers, snapshot);
            return true;
        }

        /// <summary>Accetta un draft renamer soltanto sulla revisione dalla quale deriva</summary>
        /// <param name="token">Lease del chiamante</param>
        /// <param name="id">Identità sessione</param>
        /// <param name="expectedRevision">Revisione attesa</param>
        /// <param name="draft">Form e preview materializzata</param>
        /// <param name="session">Sessione autorevole</param>
        /// <returns>True se accettato</returns>
        internal bool TryUpdateRenamerDraft(WorkspaceClientToken token, Guid id, long expectedRevision, string draft, out RenamerSessionSnapshot session)
        {
            lock (this._lock)
            {
                if (!this.ValidateLeaseLocked(token))
                {
                    session = null;
                    return false;
                }
                session = this._desktopRuntime.Renamer;
                if (session == null || session.Id != id || session.Revision != expectedRevision)
                    return false;
                if (this._workflowRuntime.Current?.Kind == WorkspaceWorkflowKind.BatchRename && this._workflowRuntime.Current.InvocationParameters.RenamerSessionId == id && this._workflowRuntime.Current.IsActive)
                    return string.Equals(session.Draft, draft, StringComparison.Ordinal);
                session = session with { Revision = session.Revision + 1, Draft = draft };
                this._desktopRuntime.Renamer = session;
                return true;
            }
        }

        /// <summary>Committa file e baseline nella stessa sezione critica del lease</summary>
        /// <param name="token">Lease che ha richiesto il save</param>
        /// <param name="id">Documento da salvare</param>
        /// <param name="revision">Revisione del testo salvato, per impedire che un save più vecchio superi uno nuovo</param>
        /// <param name="content">Testo effettivamente scritto nel file temporaneo</param>
        /// <param name="commit">Commit filesystem breve fornito dal servizio esistente</param>
        /// <returns>True se il commit è stato autorizzato</returns>
        internal bool TryCommitEditorSave(WorkspaceClientToken token, Guid id, long revision, string content, Action commit)
        {
            Action<BiviumWorkspaceSnapshot>[] subscribers;
            BiviumWorkspaceSnapshot snapshot;
            lock (this._lock)
            {
                EditorSessionSnapshot session = this._desktopRuntime.Editor;
                if (!this.ValidateMutationLocked(token) || session == null || session.Id != id || revision < session.SavedRevision || revision > session.Revision)
                    return false;
                commit();
                this._desktopRuntime.Editor = session with { SavedContent = content, SavedRevision = revision };
                snapshot = this.CommitDesktopLocked(this._snapshot.FloatingWindows);
                subscribers = this.GetSubscribers();
            }
            this.NotifySubscribers(subscribers, snapshot);
            return true;
        }

        /// <summary>Aggiorna solo la finestra richiesta, senza sovrascrivere altre superfici</summary>
        /// <param name="token">Lease corrente</param>
        /// <param name="expectedRevision">Revisione globale attesa</param>
        /// <param name="id">Sessione proprietaria</param>
        /// <param name="editor">True per editor, false per renamer</param>
        /// <param name="expectedWindow">Finestra da cui deriva la modifica</param>
        /// <param name="window">Nuovo stato</param>
        /// <param name="snapshot">Snapshot autorevole</param>
        /// <returns>True se accettato</returns>
        internal bool TryUpdateDesktopWindow(WorkspaceClientToken token, long expectedRevision, Guid id, bool editor, FloatingWindowSnapshot expectedWindow, FloatingWindowSnapshot window, out BiviumWorkspaceSnapshot snapshot)
        {
            Action<BiviumWorkspaceSnapshot>[] subscribers = Array.Empty<Action<BiviumWorkspaceSnapshot>>();
            bool accepted;
            lock (this._lock)
            {
                FloatingWindowsSnapshot windows = this._snapshot.FloatingWindows;
                Guid currentId = editor ? this._snapshot.Desktop.EditorId : this._snapshot.Desktop.RenamerId;
                FloatingWindowSnapshot currentWindow = editor ? windows.Editor : windows.Renamer;
                accepted = this.ValidateLeaseLocked(token) && id != Guid.Empty && id == currentId && expectedRevision == this._snapshot.Revision && expectedWindow == currentWindow;
                if (accepted && window != currentWindow)
                {
                    this.CommitDesktopLocked(editor ? new FloatingWindowsSnapshot(windows.Terminal, window, windows.Renamer) : new FloatingWindowsSnapshot(windows.Terminal, windows.Editor, window));
                    subscribers = this.GetSubscribers();
                }
                snapshot = this._snapshot;
            }
            this.NotifySubscribers(subscribers, snapshot);
            return accepted;
        }

        /// <summary>Chiude soltanto la sessione esplicitamente richiesta, non durante dispose</summary>
        /// <param name="token">Lease corrente</param>
        /// <param name="id">Sessione da chiudere</param>
        /// <param name="expectedRevision">Ultima revisione del draft</param>
        /// <param name="editor">True per editor, false per renamer</param>
        /// <returns>True se chiusa</returns>
        internal bool TryCloseDesktopSession(WorkspaceClientToken token, Guid id, long expectedRevision, bool editor)
        {
            Action<BiviumWorkspaceSnapshot>[] subscribers;
            BiviumWorkspaceSnapshot snapshot;
            lock (this._lock)
            {
                Guid currentId = editor ? this._desktopRuntime.Editor?.Id ?? Guid.Empty : this._desktopRuntime.Renamer?.Id ?? Guid.Empty;
                long revision = editor ? this._desktopRuntime.Editor?.Revision ?? -1 : this._desktopRuntime.Renamer?.Revision ?? -1;
                if (!this.ValidateMutationLocked(token) || id == Guid.Empty || id != currentId || revision != expectedRevision)
                    return false;
                if (!editor && this._operationRuntime?.Plan.RenamerSessionId == id && this._operationRuntime.Snapshot.IsRunning)
                    return false;
                FloatingWindowsSnapshot windows = this._snapshot.FloatingWindows;
                if (editor)
                {
                    this._desktopRuntime.Editor = null;
                    this._desktopRuntime.EditorHistory = null;
                    windows = new FloatingWindowsSnapshot(windows.Terminal, windows.Editor with { Visible = false, Minimized = false }, windows.Renamer);
                }
                else
                {
                    this._desktopRuntime.Renamer = null;
                    this._desktopRuntime.RenamerView = null;
                    windows = new FloatingWindowsSnapshot(windows.Terminal, windows.Editor, windows.Renamer with { Visible = false, Minimized = false });
                }
                snapshot = this.CommitDesktopLocked(windows);
                subscribers = this.GetSubscribers();
            }
            this.NotifySubscribers(subscribers, snapshot);
            return true;
        }

        /// <summary>Aggiorna clipboard interna sulla revisione globale attesa</summary>
        /// <param name="token">Lease corrente</param>
        /// <param name="expectedRevision">Revisione globale attesa</param>
        /// <param name="paths">Sorgenti copy/cut</param>
        /// <param name="isCut">Modalità cut</param>
        /// <returns>True se accettato</returns>
        internal bool TryUpdateClipboard(WorkspaceClientToken token, long expectedRevision, System.Collections.Generic.IEnumerable<string> paths, bool isCut)
        {
            Action<BiviumWorkspaceSnapshot>[] subscribers;
            BiviumWorkspaceSnapshot snapshot;
            lock (this._lock)
            {
                if (!this.ValidateLeaseLocked(token) || this._snapshot.Revision != expectedRevision)
                    return false;
                DesktopSessionsSnapshot desktop = this._snapshot.Desktop;
                desktop = new DesktopSessionsSnapshot(desktop.EditorId, desktop.EditorTitle, desktop.EditorDirty, desktop.RenamerId, paths, isCut);
                snapshot = new BiviumWorkspaceSnapshot(this._snapshot.Revision + 1, this._snapshot.Panels, this._snapshot.FloatingWindows, this._snapshot.ActiveClientLease, desktop, this._snapshot.Handoff, this._snapshot.Workflow, this._snapshot.Operation, this._snapshot.Upload);
                this._snapshot = snapshot;
                subscribers = this.GetSubscribers();
            }
            this.NotifySubscribers(subscribers, snapshot);
            return true;
        }

        #endregion

        #region Metodi privati

        /// <summary>Domanda o sessione ancora proprietaria della superficie visuale</summary>
        /// <param name="draft">Identità catturata al mount</param>
        /// <returns>True soltanto per una superficie conosciuta e attiva</returns>
        private bool IsDialogVisualOwnerLocked(WorkspaceDialogVisualDraft draft)
        {
            if (draft == null || draft.OwnerId == Guid.Empty)
                return false;
            if (draft.Surface == "upload")
                return this._uploadRuntime?.Snapshot.Id == draft.OwnerId && this._uploadRuntime.Snapshot.Visible;
            WorkspaceWorkflowSnapshot workflow = this._workflowRuntime.Current;
            if (workflow?.Id != draft.OwnerId || workflow.QuestionId != draft.QuestionId || workflow.Phase != draft.Phase || !workflow.IsActive)
                return false;
            return draft.Surface switch
            {
                "input" => workflow.Kind is WorkspaceWorkflowKind.CreateFile or WorkspaceWorkflowKind.CreateDirectory or WorkspaceWorkflowKind.RenameEntry,
                "confirm" => workflow.Kind is WorkspaceWorkflowKind.DeleteEntries or WorkspaceWorkflowKind.EditorAlert or WorkspaceWorkflowKind.ResetWorkspace,
                "editor-close" => workflow.Kind == WorkspaceWorkflowKind.EditorClose,
                "overwrite" => workflow.Kind is WorkspaceWorkflowKind.CopyEntries or WorkspaceWorkflowKind.MoveEntries or WorkspaceWorkflowKind.TransferEntries,
                "properties" => workflow.Kind == WorkspaceWorkflowKind.Properties,
                "permissions" => workflow.Kind == WorkspaceWorkflowKind.Permissions,
                "about" => workflow.Kind == WorkspaceWorkflowKind.About,
                "compress" => workflow.Kind == WorkspaceWorkflowKind.Compress,
                "extensions" => workflow.Kind == WorkspaceWorkflowKind.EditorExtensions,
                "creation-permissions" => workflow.Kind == WorkspaceWorkflowKind.CreationPermissions,
                "auth" => workflow.Kind == WorkspaceWorkflowKind.Authentication,
                "terminal" => workflow.Kind is WorkspaceWorkflowKind.TerminalRename or WorkspaceWorkflowKind.TerminalClose or WorkspaceWorkflowKind.TerminalClipboard,
                "failure" => workflow.Phase is WorkspaceWorkflowPhase.Failed or WorkspaceWorkflowPhase.Cancelled,
                _ => false
            };
        }

        /// <summary>La stessa rappresentazione uniforme adottata dal modello testo Monaco</summary>
        /// <param name="content">Testo originale</param>
        /// <param name="eol">Separatore effettivo del modello</param>
        /// <returns>Testo con soli separatori EOL concordati</returns>
        private static string NormalizeEditorEol(string content, string eol) => content.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", eol);

        /// <summary>La directory di base impedisce pubblicazioni tardive dopo una navigazione</summary>
        /// <param name="panelId">Identità left/right</param>
        /// <returns>Draft corrente oppure default visuale della nuova directory</returns>
        private WorkspacePanelPathDraft GetPanelPathDraftLocked(string panelId)
        {
            WorkspacePanelSnapshot panel = panelId == "left" ? this._snapshot.Panels?.LeftPanel : panelId == "right" ? this._snapshot.Panels?.RightPanel : null;
            if (panel == null)
                return null;
            WorkspacePanelPathDraft draft = panelId == "left" ? this._desktopRuntime.LeftPathDraft : this._desktopRuntime.RightPathDraft;
            return draft?.BasePath == panel.CurrentPath ? draft : new WorkspacePanelPathDraft((draft?.Revision ?? -1) + 1, panel.CurrentPath, false, panel.CurrentPath, [], [], 0, "", "");
        }

        /// <summary>Rivalida apertura sotto il lock esistente</summary>
        /// <param name="token">Lease del chiamante</param>
        private void RequireDesktopLeaseLocked(WorkspaceClientToken token)
        {
            this.ThrowIfStopped();
            if (!this.ValidateMutationLocked(token))
                throw new UnauthorizedAccessException("The browser attachment no longer owns the workspace lease");
        }

        /// <summary>Costruisce metadati leggeri; chiamare solo sotto il lock</summary>
        /// <param name="windows">Finestre autorevoli</param>
        /// <returns>Snapshot committato</returns>
        private BiviumWorkspaceSnapshot CommitDesktopLocked(FloatingWindowsSnapshot windows)
        {
            EditorSessionSnapshot editor = this._desktopRuntime.Editor;
            DesktopSessionsSnapshot previous = this._snapshot.Desktop;
            string title = editor == null ? "" : "Edit: " + Path.GetFileName(editor.FilePath) + (editor.IsDirty ? " *" : "");
            DesktopSessionsSnapshot desktop = new DesktopSessionsSnapshot(editor?.Id ?? Guid.Empty, title, editor?.IsDirty ?? false, this._desktopRuntime.Renamer?.Id ?? Guid.Empty, previous.ClipboardPaths, previous.ClipboardIsCut);
            this._snapshot = new BiviumWorkspaceSnapshot(this._snapshot.Revision + 1, this._snapshot.Panels, windows, this._snapshot.ActiveClientLease, desktop, this._snapshot.Handoff, this._snapshot.Workflow, this._snapshot.Operation, this._snapshot.Upload);
            return this._snapshot;
        }

        #endregion
    }
}
