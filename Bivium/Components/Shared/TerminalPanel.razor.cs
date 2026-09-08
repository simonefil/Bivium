using Bivium.Models;
using Bivium.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using System;
using System.Collections.Generic;
using System.Threading;

namespace Bivium.Components.Shared
{
    /// <summary>
    /// Temporary UI adapter for the persistent terminal runtime
    /// </summary>
    public partial class TerminalPanel : ComponentBase, IDisposable
    {
        #region Injected Services

        /// <summary>
        /// Global terminal runtime
        /// </summary>
        [Inject]
        private TerminalRuntimeService _terminalRuntime { get; set; }

        /// <summary>
        /// Global workspace
        /// </summary>
        [Inject]
        private BiviumWorkspaceService _workspaceService { get; set; }

        #endregion

        #region Parameters

        /// <summary>
        /// Initial directory for new tabs
        /// </summary>
        [Parameter]
        public string WorkingDirectory { get; set; } = "";

        /// <summary>
        /// Authorized circuit attachment
        /// </summary>
        [Parameter]
        public string AttachmentId { get; set; } = "";

        /// <summary>
        /// Authorized lease generation
        /// </summary>
        [Parameter]
        public long LeaseGeneration { get; set; }

        /// <summary>
        /// Callback after explicitly closing the terminal window
        /// </summary>
        [Parameter]
        public EventCallback OnClose { get; set; }

        /// <summary>
        /// Callback after a visible terminal state change
        /// </summary>
        [Parameter]
        public EventCallback OnStateChanged { get; set; }

        #endregion

        #region Class Variables

        /// <summary>
        /// Local snapshots of tabs rendered by the circuit
        /// </summary>
        private readonly List<TerminalSessionSnapshot> _sessions = new List<TerminalSessionSnapshot>();

        /// <summary>
        /// Last revision applied to the renderer for each session
        /// </summary>
        private readonly Dictionary<int, long> _appliedRevisions = new Dictionary<int, long>();

        /// <summary>
        /// Protects terminal runtime notifications received outside the circuit dispatcher
        /// </summary>
        private readonly object _runtimeEventSyncRoot = new object();

        /// <summary>
        /// Latest pending runtime notification for each session
        /// </summary>
        private readonly Dictionary<int, TerminalRuntimeEvent> _pendingRuntimeEvents = new Dictionary<int, TerminalRuntimeEvent>();

        /// <summary>
        /// Cancels every request started by the circuit
        /// </summary>
        private readonly CancellationTokenSource _requestCancellation = new CancellationTokenSource();

        /// <summary>
        /// Connects the runtime subscription to the circuit lifecycle and lease
        /// </summary>
        private CancellationTokenSource _subscriptionCancellation;

        /// <summary>
        /// Active tab identifier
        /// </summary>
        private int _activeSessionId = 0;

        /// <summary>
        /// Whether the terminal window is visible
        /// </summary>
        private bool _isVisible = false;

        /// <summary>
        /// Whether the terminal window is minimized
        /// </summary>
        private bool _isMinimized = false;

        /// <summary>
        /// Whether the circuit was detached
        /// </summary>
        private bool _isDisposed = false;

        /// <summary>
        /// Whether the browser page can consume terminal renderer updates
        /// </summary>
        private bool _terminalPageVisible = true;

        /// <summary>
        /// Whether one circuit-dispatcher drain is already scheduled
        /// </summary>
        private bool _runtimeEventDrainScheduled = false;

        /// <summary>
        /// Whether the window manager was initialized
        /// </summary>
        private bool _windowDragInitialized = false;

        /// <summary>
        /// Persistent horizontal window coordinate
        /// </summary>
        private double _windowLeft = 100;

        /// <summary>
        /// Persistent vertical window coordinate
        /// </summary>
        private double _windowTop = 80;

        /// <summary>
        /// Persistent window width
        /// </summary>
        private double _windowWidth = 800;

        /// <summary>
        /// Persistent window height
        /// </summary>
        private double _windowHeight = 400;

        /// <summary>
        /// Viewport width used during the last save
        /// </summary>
        private double _viewportWidth = 0;

        /// <summary>
        /// Viewport height used during the last save
        /// </summary>
        private double _viewportHeight = 0;

        /// <summary>
        /// Logical MRU window order
        /// </summary>
        private long _mruOrder = 0;

        /// <summary>
        /// Semantic focus target to restore
        /// </summary>
        private string _focusTarget = "terminal-input";

        /// <summary>
        /// Workspace revision on which local state is based
        /// </summary>
        private long _workspaceRevision = 0;

        /// <summary>
        /// JavaScript terminal renderer module
        /// </summary>
        private IJSObjectReference _jsModule;

        /// <summary>
        /// JavaScript window manager module
        /// </summary>
        private IJSObjectReference _interopModule;

        /// <summary>
        /// Component reference exposed to JavaScript callbacks
        /// </summary>
        private DotNetObjectReference<TerminalPanel> _dotNetRef;

        /// <summary>
        /// Temporary terminal event subscription
        /// </summary>
        private IDisposable _runtimeSubscription;

        /// <summary>
        /// Temporary window-state subscription
        /// </summary>
        private IDisposable _workspaceSubscription;

        /// <summary>
        /// Confirmation dialog for destructive close operations
        /// </summary>
        private ConfirmDialog _confirmDialog;

        /// <summary>
        /// Close operation awaiting confirmation
        /// </summary>
        private ConfirmAction _pendingConfirmAction = ConfirmAction.None;

        /// <summary>
        /// Session awaiting close confirmation
        /// </summary>
        private int _pendingCloseSessionId = 0;

        /// <summary>
        /// Session currently being renamed
        /// </summary>
        private int _renamingSessionId = 0;

        /// <summary>
        /// Temporary rename text
        /// </summary>
        private string _renameText = "";

        /// <summary>
        /// Input used by inline rename
        /// </summary>
        private ElementReference _renameInputElement;

        #endregion

        #region Overrides

        /// <summary>
        /// Connects the current circuit to the singleton without acquiring PTY ownership
        /// </summary>
        protected override void OnInitialized()
        {
            BiviumWorkspaceSnapshot workspace;
            this._workspaceSubscription = this._workspaceService.SubscribeAttachment(this.AttachmentId, this.HandleWorkspaceChanged, out workspace);
            this.ApplyWindowSnapshot(workspace);
            CancellationToken revocationToken = this._workspaceService.GetRevocationToken(this.GetClientToken());
            this._subscriptionCancellation = CancellationTokenSource.CreateLinkedTokenSource(this._requestCancellation.Token, revocationToken);
            this._runtimeSubscription = this._terminalRuntime.Subscribe(this.HandleRuntimeChanged, this._subscriptionCancellation.Token);
            this.RefreshSessions();
        }

        /// <summary>
        /// Initializes renderer and window manager after the render
        /// </summary>
        /// <param name="firstRender">Whether this is the first render</param>
        protected override async System.Threading.Tasks.Task OnAfterRenderAsync(bool firstRender)
        {
            if (this._isDisposed || (!this._isVisible && this._sessions.Count == 0))
                return;

            try
            {
                await this.EnsureJsModules();

                // Recreate only missing renderers and apply an atomic handoff on first display
                for (int i = 0; i < this._sessions.Count; i++)
                {
                    TerminalSessionSnapshot session = this._sessions[i];
                    int[] size = await this._jsModule.InvokeAsync<int[]>("initTerminal", session.Id, "terminal-container-" + session.Id, this._dotNetRef);
                    if (size != null && size.Length >= 2 && session.Id == this._activeSessionId)
                        this._terminalRuntime.ResizeSession(this.GetClientToken(), session.Id, size[0], size[1]);
                    if (!this._appliedRevisions.ContainsKey(session.Id))
                    {
                        TerminalAttachSnapshot attach = this._terminalRuntime.GetAttachSnapshot(session.Id, this._requestCancellation.Token);
                        if (attach != null)
                        {
                            this._appliedRevisions[session.Id] = attach.Session.Revision;
                            await this._jsModule.InvokeVoidAsync("applyTerminalAttach", session.Id, attach);
                        }
                    }
                }

                // Restore focus after all visible renderers are synchronized
                if (this._isVisible && this._activeSessionId > 0)
                    await this._jsModule.InvokeVoidAsync("focusTerminal", this._activeSessionId);
            }
            catch (JSDisconnectedException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Shows the terminal and creates a tab when necessary
        /// </summary>
        public void Show()
        {
            if (this._isDisposed)
                return;

            if (this._sessions.Count == 0)
                this.CreateTab();

            this._isVisible = true;
            this._isMinimized = false;
            this.PersistWindowState();
            this.StateHasChanged();
            _ = this.OnStateChanged.InvokeAsync();
        }

        /// <summary>
        /// Minimizes the terminal without terminating any PTY
        /// </summary>
        public void Minimize()
        {
            if (this._isDisposed)
                return;

            this.FinishRename(true);
            this._isVisible = false;
            this._isMinimized = this._sessions.Count > 0;
            this.PersistWindowState();
            this.StateHasChanged();
            _ = this.OnStateChanged.InvokeAsync();
        }

        /// <summary>
        /// Restores a minimized window
        /// </summary>
        public void Restore()
        {
            if (this._isDisposed)
                return;

            this._isVisible = true;
            this._isMinimized = false;
            this.PersistWindowState();
            this.StateHasChanged();
            _ = this.FocusActiveSessionAsync();
            _ = this.OnStateChanged.InvokeAsync();
        }

        /// <summary>
        /// Whether the window is visible
        /// </summary>
        /// <returns>True if visible</returns>
        public bool IsVisible()
        {
            return this._isVisible;
        }

        /// <summary>
        /// Whether the window is minimized
        /// </summary>
        /// <returns>True if minimized</returns>
        public bool IsMinimized()
        {
            return this._isMinimized;
        }

        /// <summary>
        /// Toggles visibility and minimized state
        /// </summary>
        public void Toggle()
        {
            if (this._isVisible)
                this.Minimize();
            else if (this._isMinimized)
                this.Restore();
            else
                this.Show();
        }

        #endregion

        #region JavaScript Callbacks

        /// <summary>
        /// Forwards input to the authoritative runtime
        /// </summary>
        /// <param name="sessionId">Session identifier</param>
        /// <param name="data">Data to send to the PTY</param>
        [JSInvokable]
        public void OnTerminalInput(int sessionId, string data)
        {
            if (!this._isDisposed)
                this._terminalRuntime.SendInput(this.GetClientToken(), sessionId, data);
        }

        /// <summary>
        /// Forwards a key to the authoritative XTerm.NET generator
        /// </summary>
        /// <param name="sessionId">Session identifier</param>
        /// <param name="keyName">DOM key name</param>
        /// <param name="character">Printable character</param>
        /// <param name="shift">Shift modifier</param>
        /// <param name="control">Control modifier</param>
        /// <param name="alt">Alt modifier</param>
        [JSInvokable]
        public void OnTerminalKey(int sessionId, string keyName, string character, bool shift, bool control, bool alt)
        {
            if (!this._isDisposed)
                this._terminalRuntime.SendKey(this.GetClientToken(), sessionId, keyName, character, shift, control, alt);
        }

        /// <summary>
        /// Forwards pasted text for server-side bracketed-paste handling
        /// </summary>
        /// <param name="sessionId">Session identifier</param>
        /// <param name="text">Text to paste</param>
        [JSInvokable]
        public void OnTerminalPaste(int sessionId, string text)
        {
            if (!this._isDisposed)
                this._terminalRuntime.SendPaste(this.GetClientToken(), sessionId, text);
        }

        /// <summary>
        /// Forwards focus-in and focus-out according to current VT mode
        /// </summary>
        /// <param name="sessionId">Session identifier</param>
        /// <param name="focused">True for focus-in</param>
        [JSInvokable]
        public void OnTerminalFocus(int sessionId, bool focused)
        {
            if (!this._isDisposed)
                this._terminalRuntime.SendFocus(this.GetClientToken(), sessionId, focused);
        }

        /// <summary>
        /// Forwards the resize to the PTY and headless emulator
        /// </summary>
        /// <param name="sessionId">Session identifier</param>
        /// <param name="cols">Column count</param>
        /// <param name="rows">Row count</param>
        [JSInvokable]
        public void OnTerminalResize(int sessionId, int cols, int rows)
        {
            if (!this._isDisposed)
                this._terminalRuntime.ResizeSession(this.GetClientToken(), sessionId, cols, rows);
        }

        /// <summary>
        /// Suspends renderer delivery while the browser page is hidden
        /// </summary>
        /// <param name="visible">Whether the document is visible</param>
        [JSInvokable]
        public void OnTerminalVisibilityChanged(bool visible)
        {
            lock (this._runtimeEventSyncRoot)
            {
                if (this._isDisposed || this._terminalPageVisible == visible)
                    return;

                this._terminalPageVisible = visible;
                if (!visible)
                    this._pendingRuntimeEvents.Clear();
            }

            if (visible)
            {
                // Rebuild tab chrome while JavaScript requests one authoritative renderer handoff
                this.RefreshSessions();
                this.StateHasChanged();
            }
        }

        /// <summary>
        /// Forwards mouse events to the authoritative VT tracker
        /// </summary>
        /// <param name="sessionId">Session identifier</param>
        /// <param name="button">Normalized DOM button</param>
        /// <param name="x">Terminal column</param>
        /// <param name="y">Terminal row</param>
        /// <param name="eventType">Mouse event type</param>
        /// <param name="shift">Shift modifier</param>
        /// <param name="control">Control modifier</param>
        /// <param name="alt">Alt modifier</param>
        [JSInvokable]
        public void OnTerminalMouse(int sessionId, int button, int x, int y, string eventType, bool shift, bool control, bool alt)
        {
            if (!this._isDisposed)
                this._terminalRuntime.SendMouse(this.GetClientToken(), sessionId, button, x, y, eventType, shift, control, alt);
        }

        /// <summary>
        /// Returns a remote history page
        /// </summary>
        /// <param name="sessionId">Session identifier</param>
        /// <param name="start">First requested logical row</param>
        /// <param name="count">Maximum row count</param>
        /// <returns>History page</returns>
        [JSInvokable]
        public TerminalHistoryPage GetTerminalHistoryPage(int sessionId, long start, int count)
        {
            return this._terminalRuntime.GetHistoryPage(sessionId, start, count, this._requestCancellation.Token);
        }

        /// <summary>
        /// Returns a new atomic handoff for complete resynchronization
        /// </summary>
        /// <param name="sessionId">Session identifier</param>
        /// <returns>Coherent session and history-tail handoff</returns>
        [JSInvokable]
        public TerminalAttachSnapshot GetTerminalAttach(int sessionId)
        {
            TerminalAttachSnapshot attach = this._terminalRuntime.GetAttachSnapshot(sessionId, this._requestCancellation.Token);
            if (attach?.Session != null)
                this._appliedRevisions[sessionId] = attach.Session.Revision;
            return attach;
        }

        /// <summary>
        /// Receives geometry and viewport at the end of drag or resize
        /// </summary>
        /// <param name="update">New normalized geometry from the browser</param>
        [JSInvokable]
        public void OnWindowGeometryChanged(WindowGeometryUpdate update)
        {
            if (this._isDisposed || update == null)
                return;

            this._windowLeft = update.Left;
            this._windowTop = update.Top;
            this._windowWidth = update.Width;
            this._windowHeight = update.Height;
            this._viewportWidth = update.ViewportWidth;
            this._viewportHeight = update.ViewportHeight;
            this._mruOrder = update.MruOrder;
            this._focusTarget = update.FocusTarget ?? "terminal-input";
            this.PersistWindowState();
        }

        #endregion

        #region Private UI Methods

        /// <summary>
        /// Creates a tab in the runtime singleton
        /// </summary>
        private void CreateTab()
        {
            this.FinishRename(true);
            TerminalSessionSnapshot session = this._terminalRuntime.CreateSession(this.GetClientToken(), this.WorkingDirectory);
            this._activeSessionId = session.Id;
            this._isVisible = true;
            this._isMinimized = false;
            this.RefreshSessions();
            this.PersistWindowState();
            this.StateHasChanged();
            _ = this.OnStateChanged.InvokeAsync();
        }

        /// <summary>
        /// Selects the active tab
        /// </summary>
        /// <param name="sessionId">Tab identifier</param>
        private void SelectTab(int sessionId)
        {
            this.FinishRename(true);
            this._terminalRuntime.SetActiveSession(this.GetClientToken(), sessionId);
            this._activeSessionId = sessionId;
            this.RefreshSessions();
            this.StateHasChanged();
            _ = this.FocusActiveSessionAsync();
        }

        /// <summary>
        /// Starts inline renaming
        /// </summary>
        /// <param name="session">Session to rename</param>
        private void BeginRename(TerminalSessionSnapshot session)
        {
            this._renamingSessionId = session.Id;
            this._renameText = session.Label;
            this.StateHasChanged();
            _ = this.FocusRenameInputAsync();
        }

        /// <summary>
        /// Updates the rename text
        /// </summary>
        /// <param name="args">New input value</param>
        private void HandleRenameInput(ChangeEventArgs args)
        {
            this._renameText = args.Value == null ? "" : args.Value.ToString();
        }

        /// <summary>
        /// Handles confirmation or cancellation from the keyboard
        /// </summary>
        /// <param name="args">Keyboard event</param>
        private void HandleRenameKeyDown(KeyboardEventArgs args)
        {
            if (args.Key == "Enter")
                this.FinishRename(true);
            else if (args.Key == "Escape")
                this.FinishRename(false);
        }

        /// <summary>
        /// Completes tab renaming
        /// </summary>
        /// <param name="save">Whether to save the text</param>
        private void FinishRename(bool save)
        {
            if (this._renamingSessionId == 0)
                return;

            if (save && !string.IsNullOrWhiteSpace(this._renameText))
                this._terminalRuntime.RenameSession(this.GetClientToken(), this._renamingSessionId, this._renameText);

            this._renamingSessionId = 0;
            this._renameText = "";
            this.RefreshSessions();
            this.StateHasChanged();
        }

        /// <summary>
        /// Downloads the complete retained history of the active terminal tab
        /// </summary>
        private void ExportActiveHistory()
        {
            if (this._isDisposed || this._activeSessionId <= 0)
                return;

            string url = "/api/terminal/history?sessionId=" + this._activeSessionId + "&attachmentId=" + Uri.EscapeDataString(this.AttachmentId) + "&generation=" + this.LeaseGeneration;
            _ = this.JSRuntime.InvokeVoidAsync("open", url, "_blank");
        }

        /// <summary>
        /// Requests confirmation before destructively closing the window
        /// </summary>
        private void RequestCloseContainer()
        {
            this._pendingConfirmAction = ConfirmAction.CloseAll;
            this._pendingCloseSessionId = 0;
            this._confirmDialog.Show("Close terminal sessions?", "Closing the terminal window will stop all running shell sessions.", "Close all", "Cancel");
        }

        /// <summary>
        /// Requests confirmation before destructively closing an active tab
        /// </summary>
        /// <param name="sessionId">Tab identifier</param>
        private void RequestCloseTab(int sessionId)
        {
            TerminalSessionSnapshot session = this._sessions.Find(item => item.Id == sessionId);
            if (session == null)
                return;

            if (session.Running)
            {
                this._pendingConfirmAction = ConfirmAction.CloseTab;
                this._pendingCloseSessionId = sessionId;
                this._confirmDialog.Show("Close " + session.Label + "?", "Closing " + session.Label + " will stop its shell session.", "Close tab", "Cancel");
            }
            else
            {
                this.CloseTab(sessionId);
            }
        }

        /// <summary>
        /// Executes an explicitly confirmed close operation
        /// </summary>
        /// <param name="confirmed">Whether the user confirmed</param>
        private async System.Threading.Tasks.Task HandleConfirmClose(bool confirmed)
        {
            ConfirmAction action = this._pendingConfirmAction;
            int sessionId = this._pendingCloseSessionId;
            this._pendingConfirmAction = ConfirmAction.None;
            this._pendingCloseSessionId = 0;
            if (!confirmed)
                return;

            if (action == ConfirmAction.CloseAll)
            {
                this._terminalRuntime.CloseAllSessions(this.GetClientToken());
                this._sessions.Clear();
                this._activeSessionId = 0;
                this._isVisible = false;
                this._isMinimized = false;
                this.PersistWindowState();
                await this.OnClose.InvokeAsync();
            }
            else if (action == ConfirmAction.CloseTab)
            {
                this.CloseTab(sessionId);
            }

            this.StateHasChanged();
            await this.OnStateChanged.InvokeAsync();
        }

        /// <summary>
        /// Explicitly closes one tab
        /// </summary>
        /// <param name="sessionId">Tab identifier</param>
        private void CloseTab(int sessionId)
        {
            this._terminalRuntime.CloseSession(this.GetClientToken(), sessionId);
            this._appliedRevisions.Remove(sessionId);
            if (this._jsModule != null)
                _ = this._jsModule.InvokeVoidAsync("disposeTerminal", sessionId);
            this.RefreshSessions();
            if (this._sessions.Count == 0)
            {
                this._isVisible = false;
                this._isMinimized = false;
                this.PersistWindowState();
                _ = this.OnClose.InvokeAsync();
            }

            this.StateHasChanged();
            _ = this.OnStateChanged.InvokeAsync();
        }

        /// <summary>
        /// Returns the visual classes of the tab
        /// </summary>
        /// <param name="session">Session to represent</param>
        /// <returns>Tab CSS classes</returns>
        private string GetTabCssClass(TerminalSessionSnapshot session)
        {
            string result = "terminal-tab";
            if (session.Id == this._activeSessionId)
                result += " active";
            if (session.Exited)
                result += " exited";
            if (session.HasUnreadOutput && session.Id != this._activeSessionId)
                result += " unread";
            return result;
        }

        #endregion

        #region Runtime And Workspace Synchronization

        /// <summary>
        /// Reloads the bounded session list
        /// </summary>
        private void RefreshSessions()
        {
            TerminalRuntimeSnapshot snapshot = this._terminalRuntime.GetSnapshot(this._requestCancellation.Token);
            this._sessions.Clear();
            this._sessions.AddRange(snapshot.Sessions);
            this._activeSessionId = snapshot.ActiveSessionId;

            // Reconcile local renderers with sessions still owned by the runtime singleton
            HashSet<int> currentIds = new HashSet<int>();
            for (int i = 0; i < snapshot.Sessions.Count; i++)
                currentIds.Add(snapshot.Sessions[i].Id);
            List<int> staleIds = new List<int>();
            foreach (int sessionId in this._appliedRevisions.Keys)
            {
                if (!currentIds.Contains(sessionId))
                    staleIds.Add(sessionId);
            }
            for (int i = 0; i < staleIds.Count; i++)
            {
                this._appliedRevisions.Remove(staleIds[i]);
                if (this._jsModule != null)
                    _ = this._jsModule.InvokeVoidAsync("disposeTerminal", staleIds[i]);
            }
        }

        /// <summary>
        /// Handles runtime notifications without retaining JavaScript references in the singleton
        /// </summary>
        /// <param name="runtimeEvent">Bounded event produced by the runtime</param>
        private void HandleRuntimeChanged(TerminalRuntimeEvent runtimeEvent)
        {
            if (this._isDisposed)
                return;

            lock (this._runtimeEventSyncRoot)
            {
                if (this._isDisposed || !this._terminalPageVisible)
                    return;

                this._pendingRuntimeEvents.TryGetValue(runtimeEvent.SessionId, out TerminalRuntimeEvent pending);
                this._pendingRuntimeEvents[runtimeEvent.SessionId] = MergeRuntimeEvents(pending, runtimeEvent);
                if (this._runtimeEventDrainScheduled)
                    return;
                this._runtimeEventDrainScheduled = true;
            }

            _ = this.InvokeAsync(this.DrainRuntimeChangesAsync);
        }

        /// <summary>
        /// Drains coalesced terminal changes serially on the circuit dispatcher
        /// </summary>
        private async System.Threading.Tasks.Task DrainRuntimeChangesAsync()
        {
            while (true)
            {
                List<TerminalRuntimeEvent> pendingEvents;
                lock (this._runtimeEventSyncRoot)
                {
                    if (this._isDisposed || !this._terminalPageVisible)
                    {
                        this._pendingRuntimeEvents.Clear();
                        this._runtimeEventDrainScheduled = false;
                        return;
                    }

                    if (this._pendingRuntimeEvents.Count == 0)
                    {
                        this._runtimeEventDrainScheduled = false;
                        return;
                    }

                    pendingEvents = new List<TerminalRuntimeEvent>(this._pendingRuntimeEvents.Values);
                    this._pendingRuntimeEvents.Clear();
                }

                for (int i = 0; i < pendingEvents.Count; i++)
                    await this.ApplyRuntimeChangeAsync(pendingEvents[i]);
            }
        }

        /// <summary>
        /// Applies a runtime notification on the circuit dispatcher without propagating detach to the PTY
        /// </summary>
        /// <param name="runtimeEvent">Bounded event produced by the runtime</param>
        private async System.Threading.Tasks.Task ApplyRuntimeChangeAsync(TerminalRuntimeEvent runtimeEvent)
        {
            try
            {
                lock (this._runtimeEventSyncRoot)
                {
                    if (this._isDisposed || !this._terminalPageVisible)
                        return;
                }

                bool renderRequired = runtimeEvent.SessionsChanged;

                // Update the tab list first because the patch may reference a newly created session
                if (runtimeEvent.SessionsChanged)
                    this.RefreshSessions();

                // Attempt the incremental patch from the revision actually applied to the renderer
                long fromRevision = 0;
                if (runtimeEvent.SessionId > 0)
                    this._appliedRevisions.TryGetValue(runtimeEvent.SessionId, out fromRevision);
                TerminalSessionPatch patch = null;
                if (runtimeEvent.SessionId > 0)
                    patch = this._terminalRuntime.GetSessionPatch(runtimeEvent.SessionId, fromRevision, this._requestCancellation.Token);
                if (patch != null && patch.RequiresResync)
                {
                    // A bounded-journal gap requires a new coherent history and screen handoff
                    TerminalAttachSnapshot attach = this._terminalRuntime.GetAttachSnapshot(runtimeEvent.SessionId, this._requestCancellation.Token);
                    if (attach != null)
                    {
                        TerminalSessionSnapshot previous = this._sessions.Find(item => item.Id == attach.Session.Id);
                        renderRequired |= HasSessionChromeChanged(previous, attach.Session);
                        this.ReplaceSession(attach.Session);
                        if (this._jsModule != null)
                        {
                            await this._jsModule.InvokeVoidAsync("applyTerminalAttach", attach.Session.Id, attach);
                            this._appliedRevisions[attach.Session.Id] = attach.Session.Revision;
                        }
                    }
                }
                else if (patch?.Session != null)
                {
                    // The patch updates only the affected session and leaves other renderers intact
                    TerminalSessionSnapshot previous = this._sessions.Find(item => item.Id == patch.Session.Id);
                    renderRequired |= HasSessionChromeChanged(previous, patch.Session);
                    this.ReplaceSession(patch.Session);
                    if (this._jsModule != null)
                    {
                        await this._jsModule.InvokeVoidAsync("applyTerminalPatch", patch.Session.Id, patch);
                        this._appliedRevisions[patch.Session.Id] = patch.ToRevision;
                    }
                }

                if (renderRequired)
                {
                    this.StateHasChanged();
                    await this.OnStateChanged.InvokeAsync();
                }
            }
            catch (OperationCanceledException)
            {
                // Circuit detach cancels snapshots and pages without affecting the runtime
            }
            catch (JSDisconnectedException)
            {
                // Renderer loss must not propagate to persistent PTYs
            }
            catch (ObjectDisposedException)
            {
                // Concurrent disposal completes only pending UI work
            }
        }

        /// <summary>
        /// Determines whether a session update changes Razor-rendered terminal chrome
        /// </summary>
        /// <param name="previous">Previously rendered session</param>
        /// <param name="current">Current authoritative session</param>
        /// <returns>True when the tab representation must be rendered again</returns>
        internal static bool HasSessionChromeChanged(TerminalSessionSnapshot previous, TerminalSessionSnapshot current)
        {
            if (previous == null || current == null)
                return true;

            return previous.Id != current.Id ||
                previous.Label != current.Label ||
                previous.WorkingDirectory != current.WorkingDirectory ||
                previous.Running != current.Running ||
                previous.Exited != current.Exited ||
                previous.HasUnreadOutput != current.HasUnreadOutput;
        }

        /// <summary>
        /// Keeps the newest revision and every pending tab-list change for one session
        /// </summary>
        /// <param name="pending">Previously pending event</param>
        /// <param name="current">New runtime event</param>
        /// <returns>Coalesced immutable notification copy</returns>
        internal static TerminalRuntimeEvent MergeRuntimeEvents(TerminalRuntimeEvent pending, TerminalRuntimeEvent current)
        {
            if (current == null)
                return pending;
            if (pending == null)
            {
                return new TerminalRuntimeEvent
                {
                    SessionId = current.SessionId,
                    Revision = current.Revision,
                    SessionsChanged = current.SessionsChanged
                };
            }

            return new TerminalRuntimeEvent
            {
                SessionId = current.SessionId,
                Revision = Math.Max(pending.Revision, current.Revision),
                SessionsChanged = pending.SessionsChanged || current.SessionsChanged
            };
        }

        /// <summary>
        /// Handles workspace changes originating from another attachment
        /// </summary>
        /// <param name="workspace">Notified workspace snapshot</param>
        private void HandleWorkspaceChanged(BiviumWorkspaceSnapshot workspace)
        {
            if (this._isDisposed || workspace == null || workspace.Revision <= this._workspaceRevision)
                return;

            _ = this.InvokeAsync(() =>
            {
                this.ApplyWindowSnapshot(workspace);
                this.StateHasChanged();
            });
        }

        /// <summary>
        /// Applies window geometry and lifecycle
        /// </summary>
        /// <param name="workspace">Authoritative workspace snapshot</param>
        private void ApplyWindowSnapshot(BiviumWorkspaceSnapshot workspace)
        {
            FloatingWindowSnapshot window = workspace.FloatingWindows.Terminal;
            this._workspaceRevision = workspace.Revision;
            this._isVisible = window.Visible;
            this._isMinimized = window.Minimized;
            this._windowLeft = window.Left;
            this._windowTop = window.Top;
            this._windowWidth = window.Width;
            this._windowHeight = window.Height;
            this._viewportWidth = window.ViewportWidth;
            this._viewportHeight = window.ViewportHeight;
            this._mruOrder = window.MruOrder;
            this._focusTarget = window.FocusTarget;
        }

        /// <summary>
        /// Saves window state with one retry on a concurrent panel revision
        /// </summary>
        private void PersistWindowState()
        {
            FloatingWindowSnapshot window = new FloatingWindowSnapshot(this._isVisible, this._isMinimized, this._windowLeft, this._windowTop, this._windowWidth, this._windowHeight, this._viewportWidth, this._viewportHeight, this._mruOrder, this._focusTarget);
            try
            {
                BiviumWorkspaceSnapshot workspace;

                // One retry moves the update onto the revision produced by a concurrent panel mutation
                if (!this._workspaceService.TryUpdateTerminalWindow(this.GetClientToken(), this._workspaceRevision, window, out workspace))
                    this._workspaceService.TryUpdateTerminalWindow(this.GetClientToken(), workspace.Revision, window, out workspace);
                this._workspaceRevision = workspace.Revision;
            }
            catch (Exception ex) when (ex is ObjectDisposedException || ex is UnauthorizedAccessException)
            {
                // A concurrent detach or takeover simply makes this UI update obsolete
            }
        }

        /// <summary>
        /// Replaces the local session snapshot
        /// </summary>
        /// <param name="session">Updated snapshot</param>
        private void ReplaceSession(TerminalSessionSnapshot session)
        {
            int index = this._sessions.FindIndex(item => item.Id == session.Id);
            if (index >= 0)
                this._sessions[index] = session;
            else
                this._sessions.Add(session);
        }

        /// <summary>
        /// Returns the current circuit mutation token
        /// </summary>
        private WorkspaceClientToken GetClientToken()
        {
            return new WorkspaceClientToken(this.AttachmentId, this.LeaseGeneration);
        }

        #endregion

        #region JavaScript interop

        /// <summary>
        /// Loads the modules and registers the circuit window manager and renderer
        /// </summary>
        private async System.Threading.Tasks.Task EnsureJsModules()
        {
            if (this._dotNetRef == null)
                this._dotNetRef = DotNetObjectReference.Create(this);
            if (this._jsModule == null)
                this._jsModule = await this.JSRuntime.InvokeAsync<IJSObjectReference>("import", "./js/terminal.js?v=20260730-terminal-clipboard-v8");
            if (this._interopModule == null)
                this._interopModule = await this.JSRuntime.InvokeAsync<IJSObjectReference>("import", "./js/interop.js?v=20260725-window-geometry-v3");
            if (!this._windowDragInitialized)
            {
                await this._interopModule.InvokeVoidAsync("initWindowDrag", "terminal-window", "terminal-titlebar", "terminal-resize-handle", this._dotNetRef);
                this._windowDragInitialized = true;
            }
        }

        /// <summary>
        /// Moves focus to the active renderer
        /// </summary>
        private async System.Threading.Tasks.Task FocusActiveSessionAsync()
        {
            try
            {
                await this.EnsureJsModules();
                if (this._activeSessionId > 0)
                    await this._jsModule.InvokeVoidAsync("focusTerminal", this._activeSessionId);
            }
            catch (JSDisconnectedException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        /// <summary>
        /// Moves focus to the rename input
        /// </summary>
        private async System.Threading.Tasks.Task FocusRenameInputAsync()
        {
            await System.Threading.Tasks.Task.Delay(50);
            if (this._renamingSessionId == 0 || this._isDisposed)
                return;
            try
            {
                await this._renameInputElement.FocusAsync();
            }
            catch (Exception ex) when (ex is JSDisconnectedException || ex is ObjectDisposedException || ex is InvalidOperationException)
            {
            }
        }

        /// <summary>
        /// Releases a JavaScript module while ignoring circuit disconnection
        /// </summary>
        /// <param name="module">Renderer module to release</param>
        /// <param name="disposeTerminals">Whether to destroy terminal renderers first</param>
        private async System.Threading.Tasks.Task DisposeJsModuleAsync(IJSObjectReference module, bool disposeTerminals)
        {
            if (module == null)
                return;
            try
            {
                if (disposeTerminals)
                    await module.InvokeVoidAsync("disposeAllTerminals");
                await module.DisposeAsync();
            }
            catch (Exception ex) when (ex is JSDisconnectedException || ex is ObjectDisposedException)
            {
            }
        }

        /// <summary>
        /// Removes global window manager listeners before releasing the module
        /// </summary>
        /// <param name="module">Window manager module to release</param>
        private async System.Threading.Tasks.Task DisposeWindowModuleAsync(IJSObjectReference module)
        {
            if (module == null)
                return;
            try
            {
                await module.InvokeVoidAsync("disposeWindowDrag", "terminal-window");
                await module.DisposeAsync();
            }
            catch (Exception ex) when (ex is JSDisconnectedException || ex is ObjectDisposedException)
            {
            }
        }

        #endregion

        #region IDisposable

        /// <summary>
        /// Detaches only the circuit without stopping PTYs or processes
        /// </summary>
        public void Dispose()
        {
            if (this._isDisposed)
                return;

            this._isDisposed = true;
            lock (this._runtimeEventSyncRoot)
            {
                this._pendingRuntimeEvents.Clear();
                this._runtimeEventDrainScheduled = false;
            }

            // Cancel callbacks and requests first to prevent new work on the closing circuit
            this._requestCancellation.Cancel();
            this._subscriptionCancellation?.Cancel();
            this._runtimeSubscription?.Dispose();
            this._runtimeSubscription = null;
            this._workspaceSubscription?.Dispose();
            this._workspaceSubscription = null;

            IJSObjectReference jsModule = this._jsModule;
            IJSObjectReference interopModule = this._interopModule;
            this._jsModule = null;
            this._interopModule = null;

            // Release only browser resources; the singleton still owns PTYs, emulators and history
            if (jsModule != null)
                _ = this.DisposeJsModuleAsync(jsModule, true);
            if (interopModule != null)
                _ = this.DisposeWindowModuleAsync(interopModule);
            this._dotNetRef?.Dispose();
            this._dotNetRef = null;
            this._subscriptionCancellation?.Dispose();
            this._subscriptionCancellation = null;
            this._requestCancellation.Dispose();
            GC.SuppressFinalize(this);
        }

        #endregion

        #region Nested Types

        /// <summary>
        /// Destructive action awaiting confirmation
        /// </summary>
        private enum ConfirmAction
        {
            None,
            CloseAll,
            CloseTab
        }

        /// <summary>
        /// Geometry received from the window manager
        /// </summary>
        public sealed class WindowGeometryUpdate
        {
            /// <summary>
            /// Horizontal window coordinate
            /// </summary>
            public double Left { get; set; }

            /// <summary>
            /// Vertical window coordinate
            /// </summary>
            public double Top { get; set; }

            /// <summary>
            /// Window width
            /// </summary>
            public double Width { get; set; }

            /// <summary>
            /// Window height
            /// </summary>
            public double Height { get; set; }

            /// <summary>
            /// Source viewport width
            /// </summary>
            public double ViewportWidth { get; set; }

            /// <summary>
            /// Source viewport height
            /// </summary>
            public double ViewportHeight { get; set; }

            /// <summary>
            /// Logical MRU order
            /// </summary>
            public long MruOrder { get; set; }

            /// <summary>
            /// Semantic focus target
            /// </summary>
            public string FocusTarget { get; set; } = "";
        }

        #endregion
    }
}
