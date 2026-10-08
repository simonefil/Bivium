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

        /// <summary>Draft runtime kept separate from the lightweight global projection</summary>
        private readonly DesktopSessionRuntime _desktopRuntime = new DesktopSessionRuntime();

        #endregion

        #region Metodi pubblici

        /// <summary>Reads the view of the terminal session already verified by its runtime adapter</summary>
        /// <param name="token">Mount lease</param>
        /// <param name="sessionId">Stable terminal identity</param>
        /// <returns>View or default, never terminal data</returns>
        internal WorkspaceTerminalViewState GetTerminalViewState(WorkspaceClientToken token, int sessionId)
        {
            lock (this._lock)
                return this.ValidateLeaseLocked(token) && !this.IsStopped && sessionId > 0 ? this._desktopRuntime.TerminalViews.GetValueOrDefault(sessionId) ?? new WorkspaceTerminalViewState(0, sessionId, 0, false, 0, 0, 0, true) : null;
        }

        /// <summary>Browser CAS separate from the terminal runtime journal, allowed during the drain</summary>
        /// <param name="token">Publisher lease</param>
        /// <param name="draft">Captured view</param>
        /// <param name="owner">Internal snapshot verified by the runtime, not received from the browser</param>
        /// <returns>Acknowledged revision or -1</returns>
        internal long PublishTerminalViewState(WorkspaceClientToken token, WorkspaceTerminalViewState draft, TerminalSessionSnapshot owner)
        {
            lock (this._lock)
            {
                if (!this.ValidateLeaseLocked(token) || this.IsStopped || draft == null || owner?.Id != draft.SessionId || draft.ObservedRevision < 0 || draft.ObservedRevision > owner.Revision || draft.TopRow < 0 || !double.IsFinite(draft.RowFraction) || draft.RowFraction < 0 || draft.RowFraction >= 1 || !double.IsFinite(draft.HorizontalCells) || draft.HorizontalCells < 0 || draft.PromptRow < -1 || (draft.SelectionAnchor == null) != (draft.SelectionFocus == null) || (draft.SelectionAnchor != null && (draft.SelectionAnchor.Row < 0 || draft.SelectionAnchor.Column < 0 || draft.SelectionFocus.Row < 0 || draft.SelectionFocus.Column < 0)))
                    return -1;
                if ((this._desktopRuntime.TerminalViews.GetValueOrDefault(draft.SessionId)?.Revision ?? 0) != draft.Revision)
                    return -1;
                // The runtime may advance or apply retention while the checkpoint is in transit.
                // A buffer change stays explicit: hydration will discard the incompatible anchors.
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

        /// <summary>Releases views of sessions the terminal runtime no longer represents</summary>
        /// <param name="token">Adapter lease</param>
        /// <param name="sessionIds">Identities read from the runtime, never from the browser checkpoint</param>
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

        /// <summary>Reads the strip scroll without changing the active tab</summary>
        /// <param name="token">Mount lease</param>
        /// <returns>Strip view or null</returns>
        internal WorkspaceTerminalStripViewState GetTerminalStripViewState(WorkspaceClientToken token)
        {
            lock (this._lock)
                return this.ValidateLeaseLocked(token) && !this.IsStopped ? this._desktopRuntime.TerminalStrip : null;
        }

        /// <summary>CAS of the strip only; removed headers will be clamped in the real DOM</summary>
        /// <param name="token">Publisher lease</param>
        /// <param name="draft">Semantic anchor and scroll</param>
        /// <returns>Acknowledged revision or -1</returns>
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

        /// <summary>Popup hydration only for the same Renamer session</summary>
        /// <param name="token">Mount lease</param>
        /// <param name="sessionId">Renamer owner</param>
        /// <returns>Immutable view or null</returns>
        internal WorkspaceRenamerViewState GetRenamerViewState(WorkspaceClientToken token, Guid sessionId)
        {
            lock (this._lock)
                return this.ValidateLeaseLocked(token) && !this.IsStopped && this._desktopRuntime.Renamer?.Id == sessionId ? this._desktopRuntime.RenamerView?.SessionId == sessionId ? this._desktopRuntime.RenamerView : new WorkspaceRenamerViewState(0, sessionId, [], Scrolls: [], Selections: []) : null;
        }

        /// <summary>CAS of the popups, never a change to the form or the preview</summary>
        /// <param name="token">Publisher lease</param>
        /// <param name="draft">Popups mounted by the session</param>
        /// <returns>Acknowledged revision or -1</returns>
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

        /// <summary>Reads the opening without rereading entries or flags from the filesystem</summary>
        /// <param name="token">Mount lease</param>
        /// <returns>Captured opening or null</returns>
        internal WorkspaceContextMenuDraft GetContextMenuDraft(WorkspaceClientToken token)
        {
            lock (this._lock)
                return this.ValidateLeaseLocked(token) && !this.IsStopped ? this._desktopRuntime.ContextMenu : null;
        }

        /// <summary>CAS of the opening, closing or position of the context menu</summary>
        /// <param name="token">Publisher lease</param>
        /// <param name="draft">State captured by the Commander owner</param>
        /// <returns>Acknowledged draft or null</returns>
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

        /// <summary>Visual hydration tied to the question/session, never to the last generic dialog</summary>
        /// <param name="token">Mount lease</param>
        /// <param name="draft">Identity of the surface to mount</param>
        /// <returns>Visual state or default for the same identity</returns>
        internal WorkspaceDialogVisualDraft GetDialogVisualDraft(WorkspaceClientToken token, WorkspaceDialogVisualDraft draft)
        {
            lock (this._lock)
            {
                if (!this.ValidateLeaseLocked(token) || this.IsStopped || !this.IsDialogVisualOwnerLocked(draft))
                    return null;
                return this._desktopRuntime.Dialogs.TryGetValue(draft.Surface, out WorkspaceDialogVisualDraft current) && current.OwnerId == draft.OwnerId && current.QuestionId == draft.QuestionId && current.Phase == draft.Phase ? current with { TextareaGeometries = current.TextareaGeometries?.ToArray() } : draft with { Revision = 0 };
            }
        }

        /// <summary>CAS of the visual state only; does not touch the form draft or the runner</summary>
        /// <param name="token">Publisher lease</param>
        /// <param name="draft">Focus and scroll of the owning surface</param>
        /// <returns>New revision or -1</returns>
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
                // Removes terminated identities, without accumulating dialog history or sensitive data
                foreach (string key in this._desktopRuntime.Dialogs.Where(item => !this.IsDialogVisualOwnerLocked(item.Value)).Select(item => item.Key).ToArray())
                    this._desktopRuntime.Dialogs.Remove(key);
                this._desktopRuntime.Dialogs[draft.Surface] = draft with { Revision = draft.Revision + 1, TextareaGeometries = draft.TextareaGeometries?.ToArray() };
                this.CommitDesktopLocked(this._snapshot.FloatingWindows);
                return draft.Revision + 1;
            }
        }

        /// <summary>Reads the draft path of the current directory without performing navigation</summary>
        /// <param name="token">Mount lease</param>
        /// <param name="panelId">Existing left/right identity</param>
        /// <returns>Typed draft or null when there is no authority</returns>
        internal WorkspacePanelPathDraft GetPanelPathDraft(WorkspaceClientToken token, string panelId)
        {
            lock (this._lock)
                return this.ValidateLeaseLocked(token) && !this.IsStopped ? this.GetPanelPathDraftLocked(panelId) : null;
        }

        /// <summary>Visual CAS checkpoint; updates the global watermark without rereading the panels</summary>
        /// <param name="token">Publisher lease</param>
        /// <param name="panelId">Left/right identity</param>
        /// <param name="expectedRevision">Revision of the observed draft</param>
        /// <param name="draft">Text, cycle and focus of the path editor</param>
        /// <returns>Acknowledged draft or null for a stale publisher</returns>
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
                // The drain sees this revision; no notification triggers a listing per keypress
                this.CommitDesktopLocked(this._snapshot.FloatingWindows);
                return acknowledged;
            }
        }

        /// <summary>Reads the document only for the active lease</summary>
        /// <param name="token">Reader lease</param>
        /// <returns>Immutable document or null</returns>
        internal EditorSessionSnapshot GetEditorSession(WorkspaceClientToken token)
        {
            lock (this._lock)
                return this.ValidateLeaseLocked(token) ? this._desktopRuntime.Editor : null;
        }

        /// <summary>History hydration only for the same revision of the authorized document</summary>
        /// <param name="token">Mount lease</param>
        /// <param name="id">Captured document</param>
        /// <param name="revision">Revision captured together with the content</param>
        /// <returns>Consistent history or null for a stale mount</returns>
        internal EditorHistorySnapshot GetEditorHistory(WorkspaceClientToken token, Guid id, long revision)
        {
            lock (this._lock)
                return this.ValidateLeaseLocked(token) && this._desktopRuntime.Editor?.Id == id && this._desktopRuntime.Editor.Revision == revision ? this._desktopRuntime.EditorHistory?.Capture() : null;
        }

        /// <summary>Agrees the Monaco base before edits, without creating undo or dirtying the baseline</summary>
        /// <param name="token">Mount lease</param>
        /// <param name="id">Document of the newly created model</param>
        /// <param name="revision">Hydration revision</param>
        /// <param name="content">Public getValue of the model</param>
        /// <param name="eol">Effective public getEOL</param>
        /// <returns>Initialized session or null for an inconsistent base</returns>
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
                // This is an agreement on the initial representation, not a change by the user or the file
                session = session with { Revision = session.Revision + 1, Content = content, SavedContent = content, ModelEol = eol };
                this._desktopRuntime.Editor = session;
                return session;
            }
        }

        /// <summary>Reads the draft only for the active lease</summary>
        /// <param name="token">Reader lease</param>
        /// <returns>Immutable draft or null</returns>
        internal RenamerSessionSnapshot GetRenamerSession(WorkspaceClientToken token)
        {
            lock (this._lock)
                return this.ValidateLeaseLocked(token) ? this._desktopRuntime.Renamer : null;
        }

        /// <summary>Opens a document without replacing an already open session</summary>
        /// <param name="token">Caller lease</param>
        /// <param name="filePath">File already read by the authorized workflow</param>
        /// <param name="content">Initial content</param>
        /// <returns>Authoritative session</returns>
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

        /// <summary>Opens an already materialized renamer draft without regenerating its preview</summary>
        /// <param name="token">Caller lease</param>
        /// <param name="draft">Serialized form payload</param>
        /// <returns>Authoritative session</returns>
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

        /// <summary>Accepts an ordered checkpoint of the document and the viewstate</summary>
        /// <param name="token">Caller lease</param>
        /// <param name="id">Document identity</param>
        /// <param name="expectedRevision">Revision of the previous confirmed checkpoint</param>
        /// <param name="checkpoint">Content, history delta, cursor and viewstate, atomically consistent</param>
        /// <param name="session">Session after the attempt</param>
        /// <returns>True if accepted; no implicit retry on the draft</returns>
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
                // Publishes only dirty transitions, never text checkpoints in global notifications
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

        /// <summary>Accepts a renamer draft only on the revision it derives from</summary>
        /// <param name="token">Caller lease</param>
        /// <param name="id">Session identity</param>
        /// <param name="expectedRevision">Expected revision</param>
        /// <param name="draft">Form and materialized preview</param>
        /// <param name="session">Authoritative session</param>
        /// <returns>True if accepted</returns>
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

        /// <summary>Commits file and baseline in the same critical section as the lease</summary>
        /// <param name="token">Lease that requested the save</param>
        /// <param name="id">Document to save</param>
        /// <param name="revision">Revision of the saved text, to prevent an older save from overtaking a newer one</param>
        /// <param name="content">Text actually written to the temporary file</param>
        /// <param name="commit">Short filesystem commit provided by the existing service</param>
        /// <returns>True if the commit was authorized</returns>
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

        /// <summary>Updates only the requested window, without overwriting other surfaces</summary>
        /// <param name="token">Current lease</param>
        /// <param name="expectedRevision">Expected global revision</param>
        /// <param name="id">Owning session</param>
        /// <param name="editor">True for editor, false for renamer</param>
        /// <param name="expectedWindow">Window the change derives from</param>
        /// <param name="window">New state</param>
        /// <param name="snapshot">Authoritative snapshot</param>
        /// <returns>True if accepted</returns>
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

        /// <summary>Closes only the explicitly requested session, not during dispose</summary>
        /// <param name="token">Current lease</param>
        /// <param name="id">Session to close</param>
        /// <param name="expectedRevision">Last draft revision</param>
        /// <param name="editor">True for editor, false for renamer</param>
        /// <returns>True if closed</returns>
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

        /// <summary>Updates the internal clipboard on the expected global revision</summary>
        /// <param name="token">Current lease</param>
        /// <param name="expectedRevision">Expected global revision</param>
        /// <param name="paths">Copy/cut sources</param>
        /// <param name="isCut">Cut mode</param>
        /// <returns>True if accepted</returns>
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

        /// <summary>Question or session that still owns the visual surface</summary>
        /// <param name="draft">Identity captured at mount</param>
        /// <returns>True only for a known and active surface</returns>
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

        /// <summary>The same uniform representation adopted by the Monaco text model</summary>
        /// <param name="content">Original text</param>
        /// <param name="eol">Effective separator of the model</param>
        /// <returns>Text with only the agreed EOL separators</returns>
        private static string NormalizeEditorEol(string content, string eol) => content.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", eol);

        /// <summary>The base directory prevents late publications after a navigation</summary>
        /// <param name="panelId">Left/right identity</param>
        /// <returns>Current draft or visual default of the new directory</returns>
        private WorkspacePanelPathDraft GetPanelPathDraftLocked(string panelId)
        {
            WorkspacePanelSnapshot panel = panelId == "left" ? this._snapshot.Panels?.LeftPanel : panelId == "right" ? this._snapshot.Panels?.RightPanel : null;
            if (panel == null)
                return null;
            WorkspacePanelPathDraft draft = panelId == "left" ? this._desktopRuntime.LeftPathDraft : this._desktopRuntime.RightPathDraft;
            return draft?.BasePath == panel.CurrentPath ? draft : new WorkspacePanelPathDraft((draft?.Revision ?? -1) + 1, panel.CurrentPath, false, panel.CurrentPath, [], [], 0, "", "");
        }

        /// <summary>Revalidates the opening under the existing lock</summary>
        /// <param name="token">Caller lease</param>
        private void RequireDesktopLeaseLocked(WorkspaceClientToken token)
        {
            this.ThrowIfStopped();
            if (!this.ValidateMutationLocked(token))
                throw new UnauthorizedAccessException("The browser attachment no longer owns the workspace lease");
        }

        /// <summary>Builds lightweight metadata; call only under the lock</summary>
        /// <param name="windows">Authoritative windows</param>
        /// <returns>Committed snapshot</returns>
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
