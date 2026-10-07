using Bivium.Models;
using Bivium.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Bivium.Components.Shared
{
    /// <summary>
    /// Temporary UI adapter for the persistent terminal runtime
    /// </summary>
    public partial class TerminalPanel : ComponentBase, IAsyncDisposable
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

        /// <summary>
        /// Notifiche accessibili (aria-live) per le richieste di notifica del terminale
        /// </summary>
        [Inject]
        private Radzen.NotificationService _notificationService { get; set; }

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

        /// <summary>Stato visuale ricevuto dallo stacking JS</summary>
        [Parameter]
        public bool IsActive { get; set; }

        /// <summary>
        /// Verifica la lease tramite il proprietario Commander prima della rinomina
        /// </summary>
        [Parameter]
        public Func<bool> CanInvoke { get; set; }

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
        /// Generazione della lease a cui appartiene la sottoscrizione corrente
        /// </summary>
        private long _subscribedLeaseGeneration;

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

        /// <summary>Richiesta di attivazione invalidabile dal manager JS</summary>
        private long _pendingActivation;

        /// <summary>Revisione proprietaria del ticket post-render</summary>
        private long _pendingActivationRevision;

        /// <summary>Restore provvisorio non ancora confermato dal manager JS</summary>
        private bool _restorePending;

        /// <summary>Origine conservata fra restore ripetuti prima dell'esito</summary>
        private bool _restoreWasMinimized;

        /// <summary>Ordine locale lifecycle per scartare continuazioni obsolete</summary>
        private long _lifecycleRevision;

        /// <summary>Pubblica lo snapshot iniziale dopo l'assegnazione del riferimento parent</summary>
        private bool _initialStatePublished;

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
        /// Modulo geometrico dei pulsanti di chiusura esterni ai tab nativi
        /// </summary>
        private IJSObjectReference _tabStripModule;

        /// <summary>
        /// Wrapper stabile della strip, senza alterare l'ID interno di RadzenTabs
        /// </summary>
        private ElementReference _tabStripElement;

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

        #endregion

        #region Overrides

        /// <summary>
        /// Connects the current circuit to the singleton without acquiring PTY ownership
        /// </summary>
        protected override async System.Threading.Tasks.Task OnInitializedAsync()
        {
            BiviumWorkspaceSnapshot workspace;
            this._workspaceSubscription = this._workspaceService.SubscribeAttachment(this.AttachmentId, this.HandleWorkspaceChanged, out workspace);
            this.ApplyWindowSnapshot(workspace);
            CancellationToken revocationToken = this._workspaceService.GetRevocationToken(this.GetClientToken());
            this._subscriptionCancellation = CancellationTokenSource.CreateLinkedTokenSource(this._requestCancellation.Token, revocationToken);
            this._runtimeSubscription = this._terminalRuntime.Subscribe(this.HandleRuntimeChanged, this._subscriptionCancellation.Token);
            this._subscribedLeaseGeneration = this.LeaseGeneration;
            await this.RefreshSessionsAsync();
        }

        /// <summary>
        /// Ricollega gli eventi quando il recovery riacquisisce una nuova generazione della lease
        /// </summary>
        protected override async System.Threading.Tasks.Task OnParametersSetAsync()
        {
            if (this._isDisposed || this._subscribedLeaseGeneration == this.LeaseGeneration)
                return;

            this._windowDragInitialized = false;
            this._handoffGeometrySequence = 0;

            this._subscriptionCancellation?.Cancel();
            this._runtimeSubscription?.Dispose();
            this._subscriptionCancellation?.Dispose();
            CancellationToken revocationToken = this._workspaceService.GetRevocationToken(this.GetClientToken());
            this._subscriptionCancellation = CancellationTokenSource.CreateLinkedTokenSource(this._requestCancellation.Token, revocationToken);
            this._runtimeSubscription = this._terminalRuntime.Subscribe(this.HandleRuntimeChanged, this._subscriptionCancellation.Token);
            this._subscribedLeaseGeneration = this.LeaseGeneration;
            this._appliedRevisions.Clear();
            await this.RefreshSessionsAsync();
        }

        /// <summary>
        /// Initializes renderer and window manager after the render
        /// </summary>
        /// <param name="firstRender">Whether this is the first render</param>
        protected override async System.Threading.Tasks.Task OnAfterRenderAsync(bool firstRender)
        {
            if (this._isDisposed || (!firstRender && !this._isVisible && this._sessions.Count == 0))
                return;
            // Il drain usa lo stato già persistito, senza generare nuovi resize o dialog dai render
            if (this._workspaceService.GetSnapshot().Handoff != null)
                return;

            bool publishInitialState = !this._initialStatePublished;
            if (publishInitialState)
            {
                // Il parent deve conoscere lo snapshot montato anche se l'interop non è disponibile
                this._initialStatePublished = true;
                await this.OnStateChanged.InvokeAsync();
                if (this._isDisposed)
                    return;
            }

            try
            {
                WorkspaceClientToken token = this.GetClientToken();
                await this.EnsureJsModules();
                if (this._isDisposed || token != this.GetClientToken() || this._workspaceService.GetSnapshot().Handoff != null)
                    return;
                WorkspaceTerminalStripViewState strip = this._workspaceService.GetTerminalStripViewState(token);
                if (strip == null)
                    return;
                await this._tabStripModule.InvokeVoidAsync("refresh", this._tabStripElement, this._dotNetRef, token.Generation, strip);

                // Recreate only missing renderers and apply an atomic handoff on first display
                foreach (TerminalSessionSnapshot session in this._sessions.ToArray())
                {
                    if (this._isDisposed || token != this.GetClientToken() || this._workspaceService.GetSnapshot().Handoff != null)
                        return;
                    WorkspaceTerminalViewState view = this._workspaceService.GetTerminalViewState(token, session.Id);
                    if (view == null)
                        return;
                    int[] size = await this._jsModule.InvokeAsync<int[]>("initTerminal", session.Id, "terminal-container-" + session.Id, this._publicationReference, token.Generation, view);
                    if (this._isDisposed || token != this.GetClientToken() || this._workspaceService.GetSnapshot().Handoff != null)
                        return;
                    if (size != null && size.Length >= 2 && session.Id == this._activeSessionId)
                        this._terminalRuntime.ResizeSession(token, session.Id, size[0], size[1]);
                    if (!this._appliedRevisions.ContainsKey(session.Id) || (size?.Length >= 3 && size[2] == 0))
                    {
                        TerminalAttachSnapshot attach = this._terminalRuntime.GetAttachSnapshot(session.Id, this._requestCancellation.Token);
                        if (attach != null)
                        {
                            this._appliedRevisions[session.Id] = attach.Session.Revision;
                            await this._jsModule.InvokeVoidAsync("applyTerminalAttach", session.Id, attach);
                        }
                    }
                }

                if (this._isDisposed || token != this.GetClientToken() || this._workspaceService.GetSnapshot().Handoff != null)
                    return;

                // Restore focus after all visible renderers are synchronized
                if (this._isVisible && this._pendingActivation > 0)
                {
                    long activation = this._pendingActivation;
                    long revision = this._pendingActivationRevision;
                    this._pendingActivation = 0;
                    string result = await this._interopModule.InvokeAsync<string>("activateFloatingWindowWithResult", "terminal-window", activation);
                    await this.CompleteRestoreAsync(revision, result);
                    if (result == "activated" && revision == this._lifecycleRevision)
                        await this.FocusActiveSessionAsync(activation);
                }
                else if (publishInitialState && this._isVisible)
                {
                    await this.FocusActiveSessionAsync();
                }
            }
            catch (JSDisconnectedException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (UnauthorizedAccessException)
            {
                // Un freeze o cambio lease concorrente non deve interrompere il circuito
            }
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Shows the terminal and creates a tab when necessary
        /// </summary>
        public async System.Threading.Tasks.Task ShowAsync()
        {
            if (this._isDisposed)
                return;

            if (this._sessions.Count == 0)
                await this.CreateTabAsync();
            else
            {
                this._isVisible = true;
                await this.RestoreAsync();
            }
        }

        /// <summary>
        /// Minimizes the terminal without terminating any PTY
        /// </summary>
        public async System.Threading.Tasks.Task MinimizeAsync()
        {
            if (this._isDisposed || !this._isVisible)
                return;

            this._isVisible = false;
            this._isMinimized = true;
            this._lifecycleRevision++;
            long activation = this._pendingActivation;
            this._pendingActivation = 0;
            this._restorePending = false;
            this.PersistWindowState();
            this.StateHasChanged();
            await this.OnStateChanged.InvokeAsync();
            if (this._interopModule != null && !this._isDisposed && activation > 0)
                await this._interopModule.InvokeVoidAsync("cancelFloatingWindowActivation", "terminal-window", activation);
        }

        /// <summary>
        /// Restores a minimized window
        /// </summary>
        public void Restore()
        {
            _ = this.RestoreAsync();
        }

        /// <summary>Ripristina e attiva la stessa finestra dopo il render visibile</summary>
        public async Task RestoreAsync()
        {
            if (this._isDisposed || !this.IsOpen())
                return;
            long revision = ++this._lifecycleRevision;
            await this.EnsureJsModules();
            if (this._isDisposed || !this.IsOpen() || revision != this._lifecycleRevision)
                return;
            long activation = await this._interopModule.InvokeAsync<long>("requestFloatingWindowActivation", "terminal-window");
            if (this._isDisposed || !this.IsOpen() || revision != this._lifecycleRevision)
            {
                await this._interopModule.InvokeVoidAsync("cancelFloatingWindowActivation", "terminal-window", activation);
                return;
            }
            if (activation == 0)
            {
                await this.CompleteRestoreAsync(revision, "blocked");
                return;
            }
            if (!this._restorePending)
                this._restoreWasMinimized = this._isMinimized;
            this._restorePending = true;
            this._pendingActivationRevision = revision;
            this._pendingActivation = activation;
            this._isVisible = true;
            this._isMinimized = false;
            this.StateHasChanged();
            await this.OnStateChanged.InvokeAsync();
        }

        /// <summary>Indica apertura UI, senza dedurla dalla sola esistenza di PTY</summary>
        public bool IsOpen() => this._isVisible || this._isMinimized;

        /// <summary>Titolo della finestra terminale</summary>
        public string GetTitle() => "Terminal";

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
        /// Whether any terminal tab has an unacknowledged attention request
        /// </summary>
        public bool NeedsAttention()
        {
            return this._sessions.Exists(session => session.AttentionRequested);
        }

        /// <summary>
        /// Toggles visibility and minimized state
        /// </summary>
        public async System.Threading.Tasks.Task ToggleAsync()
        {
            if (this._isVisible)
                await this.MinimizeAsync();
            else if (this._isMinimized)
                await this.RestoreAsync();
            else
                await this.ShowAsync();
        }

        #endregion

        #region JavaScript Callbacks

        /// <summary>Checkpoint visuale della sessione, senza inviare screen/history al workspace</summary>
        /// <param name="generation">Lease catturata dal renderer</param>
        /// <param name="view">Vista della singola sessione</param>
        /// <returns>Revisione acknowledged oppure -1</returns>
        [JSInvokable]
        public long OnTerminalViewChanged(long generation, WorkspaceTerminalViewState view)
        {
            if (this._isDisposed || generation != this.LeaseGeneration || view == null || !this._workspaceService.ValidatePublication(this.GetClientToken()))
                return -1;
            TerminalSessionSnapshot owner = this._terminalRuntime.GetSessionSnapshot(view.SessionId, this._requestCancellation.Token);
            return this._workspaceService.PublishTerminalViewState(this.GetClientToken(), view, owner);
        }

        /// <summary>Checkpoint della strip, indipendente dalla selezione del tab attivo</summary>
        /// <param name="generation">Lease catturata</param>
        /// <param name="view">Scroll e anchor header</param>
        /// <returns>Revisione acknowledged oppure -1</returns>
        [JSInvokable]
        public long OnTerminalStripViewChanged(long generation, WorkspaceTerminalStripViewState view)
        {
            return !this._isDisposed && generation == this.LeaseGeneration ? this._workspaceService.PublishTerminalStripViewState(this.GetClientToken(), view) : -1;
        }

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
        /// <param name="meta">Modificatore Meta/Super</param>
        /// <param name="code">Identità fisica DOM del tasto</param>
        /// <param name="repeat">Ripetizione di una pressione mantenuta</param>
        /// <param name="release">Rilascio del tasto</param>
        [JSInvokable]
        public void OnTerminalKey(int sessionId, string keyName, string character, bool shift, bool control, bool alt, bool meta, string code, bool repeat, bool release)
        {
            if (!this._isDisposed)
                this._terminalRuntime.SendKey(this.GetClientToken(), sessionId, keyName, character, shift, control, alt, meta, code, repeat, release);
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
        public async System.Threading.Tasks.Task OnTerminalVisibilityChanged(bool visible)
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
                await this.RefreshSessionsAsync();
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
        /// Returns the adjacent OSC 133 prompt without changing terminal input
        /// </summary>
        /// <param name="sessionId">Session identifier</param>
        /// <param name="fromRow">Current logical history/screen row</param>
        /// <param name="previous">True to search backward</param>
        /// <returns>Logical prompt row, or -1 when unavailable</returns>
        [JSInvokable]
        public long FindTerminalPrompt(int sessionId, long fromRow, bool previous)
        {
            return this._terminalRuntime.FindPrompt(sessionId, fromRow, previous);
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
        /// <returns>Snapshot autorevole per l'ack della geometria, oppure null per callback obsolete</returns>
        [JSInvokable]
        public FloatingWindowSnapshot OnWindowGeometryChanged(FloatingWindowGeometryUpdate update)
        {
            if (this._isDisposed || update == null || !update.IsValid || update.LeaseGeneration != this.LeaseGeneration || update.Sequence <= this._handoffGeometrySequence || !this._workspaceService.ValidatePublication(this.GetClientToken()))
                return null;
            this._handoffGeometrySequence = update.Sequence;

            this._windowLeft = update.Left;
            this._windowTop = update.Top;
            this._windowWidth = update.Width;
            this._windowHeight = update.Height;
            this._viewportWidth = update.ViewportWidth;
            this._viewportHeight = update.ViewportHeight;
            this._mruOrder = update.MruOrder;
            this._focusTarget = update.FocusTarget ?? "terminal-input";
            this.PersistWindowState();
            return this._workspaceService.GetSnapshot().FloatingWindows.Terminal;
        }

        /// <summary>Sequenza della registrazione geometrica corrente</summary>
        private long _handoffGeometrySequence;

        /// <summary>I dialog terminali sono ora nel runtime workflow; restano solo transizioni renderer</summary>
        internal bool HasNonTransferableWork => this._restorePending || this._pendingActivation != 0;

        /// <summary>Proxy JS che include i callback del renderer nella barriera dei publisher</summary>
        private IJSObjectReference _publicationReference;

        /// <summary>Drena soltanto lo stato terminale già persistito; non trasferisce dialog o input non inviati</summary>
        /// <param name="cancellationToken">Limite del tentativo</param>
        /// <returns>True quando geometria e lifecycle sono acknowledged</returns>
        internal async Task<bool> FlushForHandoffAsync(CancellationToken cancellationToken)
        {
            if (this._isDisposed || this._restorePending || this._pendingActivation != 0)
                return false;
            if (!this._isVisible && !this._isMinimized)
                return true;
            if (this._interopModule == null || !await this._interopModule.InvokeAsync<bool>("flushWindowGeometry", cancellationToken, "terminal-window"))
                return false;
            if (this._jsModule == null || !await this._jsModule.InvokeAsync<bool>("flushTerminalViews", cancellationToken))
                return false;
            cancellationToken.ThrowIfCancellationRequested();
            this.PersistWindowState();
            FloatingWindowSnapshot window = this._workspaceService.GetSnapshot().FloatingWindows.Terminal;
            return this._workspaceService.ValidatePublication(this.GetClientToken()) && window.Visible == this._isVisible && window.Minimized == this._isMinimized && window.Left == this._windowLeft && window.Top == this._windowTop && window.Width == this._windowWidth && window.Height == this._windowHeight && window.MruOrder == this._mruOrder && window.FocusTarget == this._focusTarget;
        }

        #endregion

        #region Private UI Methods

        /// <summary>Persiste solo il lifecycle conclusivo, senza annullare intenti più recenti</summary>
        /// <param name="revision">Revisione proprietaria della richiesta</param>
        /// <param name="result">Esito JS distinto da un ticket obsoleto</param>
        /// <returns>Notifica asincrona dell'eventuale rollback</returns>
        private async Task CompleteRestoreAsync(long revision, string result)
        {
            if (this._isDisposed || revision != this._lifecycleRevision || !this._restorePending)
                return;
            this._restorePending = false;
            this._pendingActivation = 0;
            bool rollback = result == "blocked" && this._restoreWasMinimized;
            if (rollback)
            {
                this._isVisible = false;
                this._isMinimized = true;
                this._lifecycleRevision++;
            }
            if (result == "activated")
                this.AcknowledgeActiveAttention();
            this.PersistWindowState();
            if (rollback)
            {
                this.StateHasChanged();
                await this.OnStateChanged.InvokeAsync();
            }
        }

        /// <summary>
        /// Creates a tab in the runtime singleton
        /// </summary>
        private async System.Threading.Tasks.Task CreateTabAsync()
        {
            if (this._workspaceService.GetWorkflow(this.GetClientToken())?.IsActive == true)
                return;
            TerminalSessionSnapshot session = this._terminalRuntime.CreateSession(this.GetClientToken(), this.WorkingDirectory);
            this._activeSessionId = session.Id;
            this._isVisible = true;
            this._isMinimized = false;
            await this.RefreshSessionsAsync();
            this.PersistWindowState();
            await this.RestoreAsync();
        }

        /// <summary>
        /// Selects the active tab
        /// </summary>
        /// <param name="sessionId">Tab identifier</param>
        private async System.Threading.Tasks.Task SelectTabAsync(int sessionId)
        {
            this._terminalRuntime.SetActiveSession(this.GetClientToken(), sessionId);
            this._activeSessionId = sessionId;
            this._terminalRuntime.AcknowledgeAttention(this.GetClientToken(), sessionId);
            await this.RefreshSessionsAsync();
            this.StateHasChanged();
            _ = this.FocusActiveSessionAsync();
        }

        /// <summary>
        /// Calcola l'indice visuale Radzen dall'identità autoritativa della sessione attiva
        /// </summary>
        /// <returns>Indice corrente oppure -1 quando non esistono sessioni</returns>
        private int GetSelectedTabIndex()
        {
            return this._sessions.FindIndex(session => session.Id == this._activeSessionId);
        }

        /// <summary>
        /// Traduce l'indice visuale Radzen nell'identità stabile della sessione
        /// </summary>
        /// <param name="index">Indice selezionato dal controllo</param>
        private async System.Threading.Tasks.Task HandleSelectedTabChangedAsync(int index)
        {
            if (index >= 0 && index < this._sessions.Count)
                await this.SelectTabAsync(this._sessions[index].Id);
        }

        /// <summary>
        /// Proietta la rinomina F2 sull'header Radzen della singola sessione
        /// </summary>
        /// <param name="session">Sessione rappresentata dall'header</param>
        /// <returns>Attributi evento dell'header</returns>
        private Dictionary<string, object> GetTabHeaderAttributes(TerminalSessionSnapshot session)
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            result["aria-label"] = session.Label + " terminal tab";
            result["data-terminal-tab-id"] = session.Id;
            result["onkeydown"] = EventCallback.Factory.Create<KeyboardEventArgs>(this, args => this.HandleTabHeaderKeyDown(session, args));
            return result;
        }

        /// <summary>
        /// Avvia la rinomina soltanto quando F2 proviene dall'header del tab
        /// </summary>
        /// <param name="session">Sessione rappresentata dall'header</param>
        /// <param name="args">Evento tastiera</param>
        private void HandleTabHeaderKeyDown(TerminalSessionSnapshot session, KeyboardEventArgs args)
        {
            if (args.Key == "F2" && !args.CtrlKey && !args.ShiftKey && !args.AltKey)
                this.BeginRename(session);
        }

        /// <summary>
        /// Apre il dialog nativo di rinomina senza input nel button del tab
        /// </summary>
        /// <param name="session">Session to rename</param>
        private void BeginRename(TerminalSessionSnapshot session)
        {
            if (session == null || this._isDisposed || this.CanInvoke?.Invoke() == false || this._workspaceService.GetWorkflow(this.GetClientToken())?.IsActive == true)
                return;
            WorkspaceTerminalContext context = new WorkspaceTerminalContext(System.Collections.Immutable.ImmutableArray.Create(session.Id));
            WorkspaceWorkflowInvocation invocation = new WorkspaceWorkflowInvocation("", "", -1, "Rename terminal tab", "Tab label", session.Label.Length, FormContext: System.Text.Json.JsonSerializer.Serialize(context));
            this._workspaceService.BeginFormWorkflow(this.GetClientToken(), WorkspaceWorkflowKind.TerminalRename, invocation, session.Label);
            this.StateHasChanged();
        }

        /// <summary>
        /// Apre la rinomina della sessione attiva derivata dallo snapshot
        /// </summary>
        private void BeginRenameActiveTab()
        {
            this.BeginRename(this._sessions.Find(session => session.Id == this._activeSessionId));
        }

        /// <summary>
        /// Downloads the complete retained history of the active terminal tab
        /// </summary>
        private void ExportActiveHistory()
        {
            if (this._isDisposed || this._activeSessionId <= 0)
                return;

            string url = "/api/terminal/history?sessionId=" + this._activeSessionId + "&attachmentId=" + Uri.EscapeDataString(this.AttachmentId) + "&generation=" + this.LeaseGeneration;
            // Download tramite anchor: non dipende dal permesso popup perso dopo il round-trip SignalR
            if (this._jsModule != null)
                _ = this._jsModule.InvokeVoidAsync("downloadTerminalHistory", url);
        }

        /// <summary>
        /// Requests confirmation before destructively closing the window
        /// </summary>
        private void RequestCloseContainer()
        {
            this.BeginTerminalClose(this._sessions.Select(session => session.Id).ToArray(), true, "Close terminal sessions?", "Closing the terminal window will stop all running shell sessions.", "Close all");
        }

        /// <summary>
        /// Requests confirmation before destructively closing the requested tab
        /// </summary>
        /// <param name="sessionId">Tab identifier</param>
        private async System.Threading.Tasks.Task RequestCloseTabAsync(int sessionId)
        {
            TerminalSessionSnapshot session = this._sessions.Find(item => item.Id == sessionId);
            if (session == null)
                return;

            if (session.Running)
            {
                this.BeginTerminalClose(new[] { sessionId }, this._sessions.Count == 1, "Close " + session.Label + "?", "Closing " + session.Label + " will stop its shell session.", "Close tab");
            }
            else
            {
                await this.CloseTabAsync(sessionId);
            }
        }

        /// <summary>Cattura i soli tab della domanda; nessun callback circuito viene trattenuto</summary>
        private void BeginTerminalClose(int[] sessionIds, bool closeWindow, string title, string message, string confirmationText)
        {
            if (this._isDisposed || this.CanInvoke?.Invoke() == false || this._workspaceService.GetWorkflow(this.GetClientToken())?.IsActive == true)
                return;
            WorkspaceTerminalContext context = new WorkspaceTerminalContext(System.Collections.Immutable.ImmutableArray.CreateRange(sessionIds), closeWindow, ConfirmationText: confirmationText);
            WorkspaceWorkflowInvocation invocation = new WorkspaceWorkflowInvocation("", "", -1, title, message, -1, FormContext: System.Text.Json.JsonSerializer.Serialize(context));
            this._workspaceService.BeginFormWorkflow(this.GetClientToken(), WorkspaceWorkflowKind.TerminalClose, invocation, "");
        }

        /// <summary>
        /// Explicitly closes one tab
        /// </summary>
        /// <param name="sessionId">Tab identifier</param>
        private async System.Threading.Tasks.Task CloseTabAsync(int sessionId)
        {
            this._terminalRuntime.CloseSession(this.GetClientToken(), sessionId);
            this._appliedRevisions.Remove(sessionId);
            await this.DisposeTerminalRendererAsync(sessionId);
            await this.RefreshSessionsAsync();
            if (this._sessions.Count == 0)
            {
                this._isVisible = false;
                this._isMinimized = false;
                this._lifecycleRevision++;
                this._pendingActivation = 0;
                this._restorePending = false;
                if (this._interopModule != null)
                    await this._interopModule.InvokeVoidAsync("cancelFloatingWindowActivation", "terminal-window");
                this.PersistWindowState();
                await this.OnClose.InvokeAsync();
            }

            this.StateHasChanged();
            await this.OnStateChanged.InvokeAsync();
        }

        /// <summary>
        /// Proietta lo stato di progresso nella severità nativa del badge
        /// </summary>
        /// <param name="session">Snapshot della sessione</param>
        /// <returns>Severità Radzen senza palette applicativa</returns>
        private Radzen.BadgeStyle GetProgressBadgeStyle(TerminalSessionSnapshot session)
        {
            return session.ProgressState switch
            {
                TerminalProgressState.Error => Radzen.BadgeStyle.Danger,
                TerminalProgressState.Warning => Radzen.BadgeStyle.Warning,
                _ => Radzen.BadgeStyle.Info
            };
        }

        /// <summary>
        /// Returns an accessible description of tab progress
        /// </summary>
        private string GetProgressTitle(TerminalSessionSnapshot session)
        {
            return session.ProgressState == TerminalProgressState.Indeterminate
                ? "Operation in progress"
                : session.ProgressState + " progress: " + session.ProgressValue + "%";
        }

        #endregion

        #region Runtime And Workspace Synchronization

        /// <summary>
        /// Reloads the bounded session list
        /// </summary>
        private async System.Threading.Tasks.Task RefreshSessionsAsync()
        {
            TerminalRuntimeSnapshot snapshot = this._terminalRuntime.GetSnapshot(this._requestCancellation.Token);
            this._sessions.Clear();
            this._sessions.AddRange(snapshot.Sessions);
            this._activeSessionId = snapshot.ActiveSessionId;
            this._workspaceService.ReconcileTerminalViews(this.GetClientToken(), snapshot.Sessions.Select(session => session.Id).ToArray());

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
                await this.DisposeTerminalRendererAsync(staleIds[i]);
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
                if (runtimeEvent.ClientEvents.Count > 0)
                {
                    this.HandleClientEvents(runtimeEvent.ClientEvents);
                    renderRequired = true;
                }

                // Update the tab list first because the patch may reference a newly created session
                if (runtimeEvent.SessionsChanged)
                    await this.RefreshSessionsAsync();

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
                previous.HasUnreadOutput != current.HasUnreadOutput ||
                previous.ProgressState != current.ProgressState ||
                previous.ProgressValue != current.ProgressValue ||
                previous.AttentionRequested != current.AttentionRequested;
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
                    SessionsChanged = current.SessionsChanged,
                    ClientEvents = current.ClientEvents
                };
            }

            return new TerminalRuntimeEvent
            {
                SessionId = current.SessionId,
                Revision = Math.Max(pending.Revision, current.Revision),
                SessionsChanged = pending.SessionsChanged || current.SessionsChanged,
                ClientEvents = MergeClientEvents(pending.ClientEvents, current.ClientEvents)
            };
        }

        /// <summary>
        /// Combines transient requests without allowing an unbounded browser queue
        /// </summary>
        private static IReadOnlyList<TerminalClientEvent> MergeClientEvents(IReadOnlyList<TerminalClientEvent> pending, IReadOnlyList<TerminalClientEvent> current)
        {
            List<TerminalClientEvent> result = new List<TerminalClientEvent>();
            if (pending != null)
                result.AddRange(pending);
            if (current != null)
                result.AddRange(current);
            if (result.Count > 16)
                result.RemoveRange(0, result.Count - 16);
            return result.AsReadOnly();
        }

        /// <summary>
        /// Routes transient terminal requests to the confirmed UI surfaces
        /// </summary>
        private void HandleClientEvents(IReadOnlyList<TerminalClientEvent> clientEvents)
        {
            for (int i = 0; i < clientEvents.Count; i++)
            {
                TerminalClientEvent clientEvent = clientEvents[i];
                if (clientEvent.Type == TerminalClientEventType.Notification)
                {
                    this.NotifyTerminalEvent(clientEvent);
                }
                else if (clientEvent.Type == TerminalClientEventType.Attention && this._isVisible && !this._restorePending && clientEvent.SessionId == this._activeSessionId)
                {
                    this._terminalRuntime.AcknowledgeAttention(this.GetClientToken(), clientEvent.SessionId);
                }
            }
        }

        /// <summary>
        /// Mostra la notifica del terminale; il click apre e seleziona la sessione che l'ha prodotta
        /// </summary>
        /// <param name="clientEvent">Notifica testuale transitoria, non persistita nel workspace</param>
        private void NotifyTerminalEvent(TerminalClientEvent clientEvent)
        {
            int sessionId = clientEvent.SessionId;
            this._notificationService.Notify(new Radzen.NotificationMessage
            {
                Severity = Radzen.NotificationSeverity.Info,
                Summary = clientEvent.Title,
                Detail = clientEvent.Text,
                Duration = 5000,
                CloseOnClick = true,
                Click = message => _ = this.InvokeAsync(() => this.OpenNotificationSessionAsync(sessionId))
            });
        }

        /// <summary>
        /// Opens and selects the session that produced a notification
        /// </summary>
        /// <param name="sessionId">Sessione da mostrare</param>
        private async System.Threading.Tasks.Task OpenNotificationSessionAsync(int sessionId)
        {
            if (this._isDisposed || !this._sessions.Exists(session => session.Id == sessionId))
                return;
            if (!this._isVisible)
                this.Restore();
            await this.SelectTabAsync(sessionId);
            this.StateHasChanged();
        }

        /// <summary>
        /// Clears attention for the tab made visible through F12
        /// </summary>
        private void AcknowledgeActiveAttention()
        {
            if (this._activeSessionId > 0)
                this._terminalRuntime.AcknowledgeAttention(this.GetClientToken(), this._activeSessionId);
        }

        /// <summary>
        /// Handles workspace changes originating from another attachment
        /// </summary>
        /// <param name="workspace">Notified workspace snapshot</param>
        private void HandleWorkspaceChanged(BiviumWorkspaceSnapshot workspace)
        {
            if (this._isDisposed || workspace == null || workspace.Revision <= this._workspaceRevision)
                return;

            _ = this.InvokeAsync(async () =>
            {
                // La revisione può essere avanzata mentre la notifica attendeva il dispatcher
                if (this._isDisposed || workspace.Revision <= this._workspaceRevision)
                    return;

                bool wasVisible = this._isVisible;
                bool wasMinimized = this._isMinimized;
                this.ApplyWindowSnapshot(workspace);
                this.StateHasChanged();
                if (this._isVisible != wasVisible || this._isMinimized != wasMinimized)
                    await this.OnStateChanged.InvokeAsync();
            });
        }

        /// <summary>
        /// Applies window geometry and lifecycle
        /// </summary>
        /// <param name="workspace">Authoritative workspace snapshot</param>
        private void ApplyWindowSnapshot(BiviumWorkspaceSnapshot workspace)
        {
            FloatingWindowSnapshot window = workspace.FloatingWindows.Terminal;
            // Gli aggiornamenti dei pannelli conservano il lifecycle di origine finché JS non decide il restore
            bool pendingOrigin = this._restorePending && window.Visible == !this._restoreWasMinimized && window.Minimized == this._restoreWasMinimized;
            if (!pendingOrigin)
            {
                if (this._isVisible != window.Visible || this._isMinimized != window.Minimized)
                {
                    this._lifecycleRevision++;
                    this._restorePending = false;
                }
                if (!window.Visible)
                    this._pendingActivation = 0;
                this._isVisible = window.Visible;
                this._isMinimized = window.Minimized;
            }
            this._workspaceRevision = workspace.Revision;
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
            bool visible = this._restorePending ? !this._restoreWasMinimized : this._isVisible;
            bool minimized = this._restorePending ? this._restoreWasMinimized : this._isMinimized;
            FloatingWindowSnapshot window = new FloatingWindowSnapshot(visible, minimized, this._windowLeft, this._windowTop, this._windowWidth, this._windowHeight, this._viewportWidth, this._viewportHeight, this._mruOrder, this._focusTarget);
            try
            {
                BiviumWorkspaceSnapshot workspace;

                // Un solo retry conserva la mutazione locale sullo snapshot concorrente
                bool updated = this._workspaceService.TryUpdateTerminalWindow(this.GetClientToken(), this._workspaceRevision, window, out workspace);
                if (!updated)
                    updated = this._workspaceService.TryUpdateTerminalWindow(this.GetClientToken(), workspace.Revision, window, out workspace);
                if (!updated)
                {
                    // Non dichiarare persistito uno stato rifiutato anche al secondo tentativo
                    this.HandleWorkspaceChanged(workspace);
                    return;
                }
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
                this._jsModule = await this.JSRuntime.InvokeAsync<IJSObjectReference>("import", "./js/terminal.js?v=20261007-render-v14");
            if (this._interopModule == null)
                this._interopModule = await this.JSRuntime.InvokeAsync<IJSObjectReference>("import", "./js/interop.js");
            if (this._publicationReference == null)
                this._publicationReference = await this._interopModule.InvokeAsync<IJSObjectReference>("createDesktopPublicationReference", this._dotNetRef);
            if (this._tabStripModule == null)
                this._tabStripModule = await this.JSRuntime.InvokeAsync<IJSObjectReference>("import", "./js/terminal-tabs.js?v=20261001-tab-close-v2");
            if (!this._windowDragInitialized)
            {
                await this._interopModule.InvokeVoidAsync("initWindowDrag", "terminal-window", "terminal-titlebar", "terminal-resize-handle", this._dotNetRef, "", this.LeaseGeneration);
                this._windowDragInitialized = true;
            }
        }

        /// <summary>
        /// Moves focus to the active renderer
        /// </summary>
        /// <param name="activation">Richiesta JS da rivalidare anche nel frame differito</param>
        private async System.Threading.Tasks.Task FocusActiveSessionAsync(long? activation = null)
        {
            try
            {
                await this.EnsureJsModules();
                if (this._workspaceService.GetWorkflow(this.GetClientToken())?.IsActive != true && this._activeSessionId > 0)
                    await this._jsModule.InvokeVoidAsync("focusTerminal", this._activeSessionId, activation);
            }
            catch (JSDisconnectedException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        /// <summary>
        /// Releases one browser renderer without changing the server-side terminal session
        /// </summary>
        /// <param name="sessionId">Session renderer to release</param>
        private async System.Threading.Tasks.Task DisposeTerminalRendererAsync(int sessionId)
        {
            if (this._jsModule == null)
                return;

            try
            {
                await this._jsModule.InvokeVoidAsync("disposeTerminal", sessionId);
            }
            catch (Exception ex) when (ex is JSDisconnectedException || ex is OperationCanceledException || ex is ObjectDisposedException)
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
                try
                {
                    if (disposeTerminals)
                        await module.InvokeVoidAsync("disposeAllTerminals");
                }
                finally
                {
                    await module.DisposeAsync();
                }
            }
            catch (Exception ex) when (ex is JSDisconnectedException || ex is OperationCanceledException || ex is ObjectDisposedException)
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
                try
                {
                    await module.InvokeVoidAsync("disposeWindowDrag", "terminal-window");
                }
                finally
                {
                    await module.DisposeAsync();
                }
            }
            catch (Exception ex) when (ex is JSDisconnectedException || ex is OperationCanceledException || ex is ObjectDisposedException)
            {
            }
        }

        #endregion

        #region IAsyncDisposable

        /// <summary>
        /// Rilascia observer e frame della strip prima del modulo geometrico
        /// </summary>
        /// <param name="module">Modulo geometrico da rilasciare</param>
        private async System.Threading.Tasks.Task DisposeTabStripModuleAsync(IJSObjectReference module)
        {
            if (module == null)
                return;
            try
            {
                try
                {
                    await module.InvokeVoidAsync("dispose", this._tabStripElement);
                }
                finally
                {
                    await module.DisposeAsync();
                }
            }
            catch (Exception ex) when (ex is JSDisconnectedException || ex is OperationCanceledException || ex is ObjectDisposedException)
            {
            }
        }

        /// <summary>
        /// Detaches only the circuit without stopping PTYs or processes
        /// </summary>
        public async ValueTask DisposeAsync()
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
            IJSObjectReference tabStripModule = this._tabStripModule;
            IJSObjectReference publicationReference = this._publicationReference;
            this._jsModule = null;
            this._interopModule = null;
            this._tabStripModule = null;
            this._publicationReference = null;

            // Release only browser resources; the singleton still owns PTYs, emulators and history
            try
            {
                try
                {
                    await this.DisposeJsModuleAsync(jsModule, true);
                }
                finally
                {
                    try
                    {
                        await this.DisposeTabStripModuleAsync(tabStripModule);
                    }
                    finally
                    {
                        try
                        {
                            if (publicationReference != null)
                                await publicationReference.DisposeAsync();
                        }
                        catch (Exception ex) when (ex is JSDisconnectedException || ex is OperationCanceledException || ex is ObjectDisposedException)
                        {
                        }
                        finally
                        {
                            await this.DisposeWindowModuleAsync(interopModule);
                        }
                    }
                }
            }
            finally
            {
                this._dotNetRef?.Dispose();
                this._dotNetRef = null;
                this._subscriptionCancellation?.Dispose();
                this._subscriptionCancellation = null;
                this._requestCancellation.Dispose();
            }

            GC.SuppressFinalize(this);
        }

        #endregion
    }
}
