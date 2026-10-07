using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Bivium.Models;
using Bivium.Services;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Text.Json;

namespace Bivium.Components.Shared
{
    /// <summary>
    /// Floating editor window with Monaco Editor for editing text files
    /// </summary>
    public partial class EditorDialog : ComponentBase, IAsyncDisposable
    {
        #region Injected Services

        /// <summary>
        /// File operation service for saving editor content
        /// </summary>
        [Inject]
        private IFileOperationService _fileOperationService { get; set; }

        /// <summary>
        /// Workspace lease authority
        /// </summary>
        [Inject]
        private BiviumWorkspaceService _workspaceService { get; set; }

        /// <summary>Tema Radzen corrente dal quale deriva il tema Monaco</summary>
        [Inject]
        private Radzen.ThemeService _themeService { get; set; }

        /// <summary>Notifiche per errori di salvataggio</summary>
        [Inject]
        private Radzen.NotificationService _notificationService { get; set; }

        #endregion

        #region Parameters

        /// <summary>
        /// Callback when the editor is closed (true if the file was saved at least once in the session)
        /// </summary>
        [Parameter]
        public EventCallback<bool> OnClose { get; set; }

        /// <summary>Stato visuale ricevuto dallo stacking JS</summary>
        [Parameter]
        public bool IsActive { get; set; }

        /// <summary>Notifica lifecycle e titolo della finestra</summary>
        [Parameter]
        public EventCallback OnStateChanged { get; set; }

        [Parameter]
        public string AttachmentId { get; set; } = "";

        [Parameter]
        public long LeaseGeneration { get; set; }

        #endregion

        #region Class Variables

        /// <summary>
        /// Whether the editor window is visible
        /// </summary>
        private bool _isVisible = false;

        /// <summary>Finestra aperta ma temporaneamente nascosta</summary>
        private bool _isMinimized;

        /// <summary>Contenuto iniziale consumato una sola volta dopo il render</summary>
        private string _pendingContent;

        /// <summary>Richiesta di attivazione invalidabile dal manager JS</summary>
        private long _pendingActivation;

        /// <summary>Revisione proprietaria del ticket post-render</summary>
        private long _pendingActivationRevision;

        /// <summary>Ripristino visibile provvisorio ancora in attesa dell'esito JS</summary>
        private bool _restorePending;

        /// <summary>Origine conservata anche fra restore ripetuti prima dell'esito</summary>
        private bool _restoreWasMinimized;

        /// <summary>Generazione dell'apertura per scartare inizializzazioni obsolete</summary>
        private long _openGeneration;

        /// <summary>Ordine locale delle richieste lifecycle, distinto dallo stacking</summary>
        private long _lifecycleRevision;

        /// <summary>Evita inizializzazioni concorrenti durante il caricamento AMD</summary>
        private bool _renderInteropPending;

        /// <summary>
        /// Full path of the file being edited
        /// </summary>
        private string _filePath = "";

        /// <summary>
        /// Display name of the file being edited
        /// </summary>
        private string _fileName = "";

        /// <summary>
        /// Whether the editor content has unsaved changes
        /// </summary>
        private bool _isDirty = false;

        /// <summary>
        /// JS module reference for editor interop
        /// </summary>
        private IJSObjectReference _jsModule = null;

        /// <summary>
        /// JS module reference for drag/resize interop
        /// </summary>
        private IJSObjectReference _interopModule = null;

        /// <summary>
        /// .NET reference for JS save callback
        /// </summary>
        private DotNetObjectReference<EditorDialog> _dotNetRef = null;

        /// <summary>
        /// Whether the JS editor has been initialized
        /// </summary>
        private bool _jsInitialized = false;

        /// <summary>
        /// Whether component-owned callbacks have been released
        /// </summary>
        private bool _isDisposed;

        /// <summary>Save in corso: impedisce scritture concorrenti e blocca il drain dell'handoff</summary>
        private bool _isSaving;

        /// <summary>Guardia transitoria: un save in corso non viene cancellato dal tentativo</summary>
        internal bool HasNonTransferableWork => this._isSaving;

        /// <summary>
        /// Status bar text (file size, encoding info)
        /// </summary>
        private string _statusText = "";

        /// <summary>Sessione server, separata dagli oggetti Monaco posseduti dall'adapter</summary>
        private EditorSessionSnapshot _session;

        /// <summary>Ultima finestra autorevole dalla quale derivano gli aggiornamenti</summary>
        private FloatingWindowSnapshot _window = new FloatingWindowsSnapshot().Editor;

        /// <summary>Viewstate da applicare una sola volta al nuovo modello Monaco</summary>
        private string _pendingViewState;
        /// <summary>History della revisione hydrated, consumata soltanto al mount Monaco</summary>
        private EditorHistorySnapshot _pendingHistory;

        /// <summary>Ultima notifica geometrica accettata per questa sessione</summary>
        private long _geometrySequence;

        /// <summary>Lease che possiede il publisher Monaco di questo mount</summary>
        private long _adapterLeaseGeneration;

        /// <summary>Rilascio del modello locale quando il server chiude o resetta la sessione</summary>
        private bool _releaseEditorPending;

        /// <summary>Stile della geometria; vuoto conserva il layout CSS iniziale</summary>
        private string WindowStyle => this._window.Width > 0 ? FormattableString.Invariant($"left:{this._window.Left}px;top:{this._window.Top}px;width:{this._window.Width}px;height:{this._window.Height}px") : "";

        #endregion

        #region Public Methods

        /// <summary>
        /// Shows the editor window with the specified file content
        /// </summary>
        /// <param name="filePath">Full path to the file</param>
        /// <param name="content">Text content to load into the editor</param>
        public void Show(string filePath, string content)
        {
            if (this._isDisposed || this.IsOpen())
                return;
            try
            {
                EditorSessionSnapshot session = this._workspaceService.OpenEditorSession(this.GetClientToken(), filePath, content);
                this.ApplySession(session);
                this.ApplyWindow(this._workspaceService.GetSnapshot().FloatingWindows.Editor);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is ObjectDisposedException)
            {
                return;
            }
            this.StateHasChanged();
            _ = this.RestoreAsync();
        }

        /// <summary>
        /// Hides the editor window and disposes the Monaco instance
        /// </summary>
        private void Hide()
        {
            if (this._session != null && !this._workspaceService.TryCloseDesktopSession(this.GetClientToken(), this._session.Id, this._session.Revision, true))
                return;
            this._session = null;
            this._isVisible = false;
            this._isMinimized = false;
            this._pendingContent = null;
            this._pendingActivation = 0;
            this._restorePending = false;
            this._openGeneration++;
            this._lifecycleRevision++;
            this._isDirty = false;
            if (this._interopModule != null)
                _ = this._interopModule.InvokeVoidAsync("cancelFloatingWindowActivation", "editor-window");

            // Dispose the Monaco editor instance
            if (this._jsModule != null)
            {
                _ = this._jsModule.InvokeVoidAsync("disposeEditor");
                this._jsInitialized = false;
            }

            this.StateHasChanged();
            _ = this.OnStateChanged.InvokeAsync();
        }

        /// <summary>Indica se la stessa sessione editor è ancora aperta</summary>
        public bool IsOpen() => this._isVisible || this._isMinimized;

        /// <summary>Titolo corrente con indicatore di modifiche non salvate</summary>
        public string GetTitle() => "Edit: " + this._fileName + (this._isDirty ? " *" : "");

        /// <summary>Nasconde la finestra conservando DOM, modello Monaco e geometria</summary>
        public async Task MinimizeAsync()
        {
            if (this._isDisposed || !this._isVisible)
                return;
            this._isVisible = false;
            this._isMinimized = true;
            this._lifecycleRevision++;
            this._pendingActivation = 0;
            this._restorePending = false;
            this.PersistVisibility();
            if (this._interopModule != null)
                await this._interopModule.InvokeVoidAsync("cancelFloatingWindowActivation", "editor-window");
            this.StateHasChanged();
            await this.OnStateChanged.InvokeAsync();
        }

        /// <summary>Ripristina e attiva la stessa istanza dopo il render visibile</summary>
        public async Task RestoreAsync()
        {
            if (this._isDisposed || !this.IsOpen())
                return;
            long revision = ++this._lifecycleRevision;
            if (this._interopModule == null)
                this._interopModule = await this.JSRuntime.InvokeAsync<IJSObjectReference>("import", "./js/interop.js");
            if (this._isDisposed || !this.IsOpen() || revision != this._lifecycleRevision)
                return;
            long activation = await this._interopModule.InvokeAsync<long>("requestFloatingWindowActivation", "editor-window");
            if (this._isDisposed || !this.IsOpen() || revision != this._lifecycleRevision)
            {
                await this._interopModule.InvokeVoidAsync("cancelFloatingWindowActivation", "editor-window", activation);
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
            this.PersistVisibility();
            this.StateHasChanged();
            await this.OnStateChanged.InvokeAsync();
        }

        #endregion

        #region Private Methods

        /// <summary>Segue i cambi di tema Radzen per aggiornare Monaco</summary>
        protected override void OnInitialized()
        {
            this._themeService.ThemeChanged += this.HandleThemeChanged;
        }

        /// <summary>Hydrate dal runtime senza rileggere il file o azzerare dirty</summary>
        protected override void OnParametersSet()
        {
            if (this._isDisposed)
                return;
            EditorSessionSnapshot session = this._workspaceService.GetEditorSession(this.GetClientToken());
            if (session == null)
            {
                if (this._session != null)
                    this._releaseEditorPending = true;
                this._session = null;
                this._isVisible = false;
                this._isMinimized = false;
                this._pendingContent = null;
                this._pendingViewState = null;
                this._openGeneration++;
                return;
            }
            this.ApplySession(session);
            this.ApplyWindow(this._workspaceService.GetSnapshot().FloatingWindows.Editor);
        }

        /// <summary>Ricostruisce l'adapter soltanto quando cambia identità documento</summary>
        /// <param name="session">Documento autorevole</param>
        private void ApplySession(EditorSessionSnapshot session)
        {
            if (this._session?.Id != session.Id || this._adapterLeaseGeneration != this.LeaseGeneration)
            {
                this._adapterLeaseGeneration = this.LeaseGeneration;
                this._geometrySequence = 0;
                this._releaseEditorPending = false;
                this._openGeneration++;
                this._lifecycleRevision++;
                this._pendingContent = session.Content;
                this._pendingViewState = session.ViewState;
                this._pendingHistory = this._workspaceService.GetEditorHistory(this.GetClientToken(), session.Id, session.Revision);
                this._jsInitialized = false;
                this._filePath = session.FilePath;
                this._fileName = System.IO.Path.GetFileName(session.FilePath);
            }
            this._session = session;
            this._isDirty = session.IsDirty;
            this._statusText = ByteSizeFormatter.Format(session.Content.Length) + (session.IsDirty ? " - Modified" : " - Saved");
        }

        /// <summary>Proietta la finestra del workspace senza attivarla come nuova apertura</summary>
        /// <param name="window">Stato autorevole</param>
        private void ApplyWindow(FloatingWindowSnapshot window)
        {
            this._window = window;
            this._isVisible = window.Visible;
            this._isMinimized = window.Minimized;
        }

        /// <summary>Token del mount corrente</summary>
        /// <returns>Lease da rivalidare sotto il lock workspace</returns>
        private WorkspaceClientToken GetClientToken() => new WorkspaceClientToken(this.AttachmentId, this.LeaseGeneration);

        /// <summary>Pubblica il lifecycle prima degli await degli adapter JS</summary>
        private void PersistVisibility()
        {
            this.PersistWindow(this._window with { Visible = this._isVisible, Minimized = this._isMinimized });
        }

        /// <summary>Confronta finestra e revisione; un retry richiede prova che la stessa finestra sia invariata</summary>
        /// <param name="window">Modifica derivata dalla finestra locale</param>
        private void PersistWindow(FloatingWindowSnapshot window)
        {
            if (this._session == null || this._isDisposed)
                return;
            Guid id = this._session.Id;
            FloatingWindowSnapshot expectedWindow = this._window;
            BiviumWorkspaceSnapshot observed = this._workspaceService.GetSnapshot();
            bool updated = this._workspaceService.TryUpdateDesktopWindow(this.GetClientToken(), observed.Revision, id, true, expectedWindow, window, out BiviumWorkspaceSnapshot snapshot);
            if (!updated && snapshot.Revision != observed.Revision && snapshot.Desktop.EditorId == id && snapshot.FloatingWindows.Editor == expectedWindow)
                this._workspaceService.TryUpdateDesktopWindow(this.GetClientToken(), snapshot.Revision, id, true, expectedWindow, window, out snapshot);
            this.ApplyWindow(snapshot.FloatingWindows.Editor);
        }

        /// <summary>Riceve geometria, focus semantico e ordine MRU dal manager locale</summary>
        /// <param name="update">Misure della finestra montata</param>
        /// <returns>Finestra autorevole per riconciliare anche un CAS rifiutato</returns>
        [JSInvokable]
        public FloatingWindowSnapshot OnWindowGeometryChanged(FloatingWindowGeometryUpdate update)
        {
            if (this._isDisposed || update == null || !update.IsValid || !this.IsOpen() || this._session == null || update.SessionId != this._session.Id.ToString() || update.LeaseGeneration != this.LeaseGeneration || update.Sequence <= this._geometrySequence)
                return null;
            this._geometrySequence = update.Sequence;
            this.PersistWindow(this._window with { Left = update.Left, Top = update.Top, Width = update.Width, Height = update.Height, ViewportWidth = update.ViewportWidth, ViewportHeight = update.ViewportHeight, MruOrder = update.MruOrder, FocusTarget = update.FocusTarget });
            return this._window;
        }

        /// <summary>Accetta il checkpoint JS solo sulla precedente revisione confermata</summary>
        /// <param name="sessionId">Identità catturata dall'istanza Monaco</param>
        /// <param name="revision">Revisione del checkpoint precedente</param>
        /// <param name="leaseGeneration">Generazione catturata dal publisher JS</param>
        /// <param name="checkpointStream">Checkpoint trasferito fuori dal messaggio SignalR ordinario</param>
        /// <returns>Nuova revisione oppure -1 per fermare il publisher revocato o obsoleto</returns>
        [JSInvokable("OnEditorCheckpoint")]
        public async Task<long> OnEditorCheckpointAsync(string sessionId, long revision, long leaseGeneration, IJSStreamReference checkpointStream)
        {
            await using (checkpointStream)
            {
                WorkspaceClientToken token = new WorkspaceClientToken(this.AttachmentId, leaseGeneration);
                if (this._isDisposed || leaseGeneration != this.LeaseGeneration || this._session == null || !Guid.TryParse(sessionId, out Guid id) || id != this._session.Id || !this._workspaceService.ValidatePublication(token))
                    return -1;
                CancellationToken cancellationToken = this._workspaceService.GetRevocationToken(token);
                await using Stream stream = await checkpointStream.OpenReadStreamAsync(checkpointStream.Length, cancellationToken);
                EditorCheckpoint checkpoint = await JsonSerializer.DeserializeAsync<EditorCheckpoint>(stream, cancellationToken: cancellationToken);
                if (this._isDisposed || checkpoint == null || !this._workspaceService.TryUpdateEditorDraft(token, id, revision, checkpoint, out EditorSessionSnapshot session))
                    return -1;
                this.ApplySession(session);
                this.StateHasChanged();
                return session.Revision;
            }
        }

        /// <summary>Handshake streaming della base, prima di abilitare input e publisher del journal</summary>
        /// <param name="sessionId">Documento catturato</param>
        /// <param name="revision">Revisione hydrated</param>
        /// <param name="generation">Lease catturata</param>
        /// <param name="eol">EOL pubblico del modello</param>
        /// <param name="contentStream">getValue senza limiti del messaggio SignalR ordinario</param>
        /// <returns>Revisione concordata oppure -1</returns>
        [JSInvokable("OnEditorModelInitialized")]
        public async Task<long> OnEditorModelInitializedAsync(string sessionId, long revision, long generation, string eol, IJSStreamReference contentStream)
        {
            await using (contentStream)
            {
                WorkspaceClientToken token = new WorkspaceClientToken(this.AttachmentId, generation);
                if (this._isDisposed || generation != this.LeaseGeneration || !Guid.TryParse(sessionId, out Guid id) || this._session?.Id != id || !this._workspaceService.ValidatePublication(token))
                    return -1;
                CancellationToken cancellationToken = this._workspaceService.GetRevocationToken(token);
                await using Stream stream = await contentStream.OpenReadStreamAsync(contentStream.Length, cancellationToken);
                string content = await JsonSerializer.DeserializeAsync<string>(stream, cancellationToken: cancellationToken);
                if (this._isDisposed || generation != this.LeaseGeneration || this._session?.Id != id)
                    return -1;
                EditorSessionSnapshot session = this._workspaceService.InitializeEditorModel(token, id, revision, content, eol);
                if (session == null)
                    return -1;
                this.ApplySession(session);
                return session.Revision;
            }
        }

        /// <summary>Conferma il restore o annulla soltanto quello rifiutato dal modal</summary>
        /// <param name="revision">Revisione proprietaria della richiesta</param>
        /// <param name="result">Esito JS distinto da un ticket obsoleto</param>
        /// <returns>Notifica asincrona dell'eventuale rollback</returns>
        private async Task CompleteRestoreAsync(long revision, string result)
        {
            if (this._isDisposed || revision != this._lifecycleRevision || !this._restorePending)
                return;
            this._restorePending = false;
            this._pendingActivation = 0;
            if (result == "blocked" && this._restoreWasMinimized)
            {
                this._isVisible = false;
                this._isMinimized = true;
                this._lifecycleRevision++;
                this.PersistVisibility();
                this.StateHasChanged();
                await this.OnStateChanged.InvokeAsync();
            }
        }

        /// <summary>
        /// Inizializza Monaco una volta e applica l'attivazione dopo il render
        /// </summary>
        /// <param name="firstRender">Indica il primo render del componente</param>
        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (this._isDisposed || this._renderInteropPending)
                return;
            long generation = this._openGeneration;
            this._renderInteropPending = true;
            try
            {
                if (this._releaseEditorPending && this._jsModule != null)
                {
                    this._releaseEditorPending = false;
                    this._jsInitialized = false;
                    await this._jsModule.InvokeVoidAsync("disposeEditor");
                }
                if (this._interopModule == null)
                    this._interopModule = await this.JSRuntime.InvokeAsync<IJSObjectReference>("import", "./js/interop.js");
                if (this._isDisposed)
                    return;
                if (this._dotNetRef == null)
                    this._dotNetRef = DotNetObjectReference.Create(this);
                await this._interopModule.InvokeVoidAsync("initWindowDrag", "editor-window", "editor-titlebar", "editor-resize-handle", this._dotNetRef, this._session?.Id.ToString() ?? "", this.LeaseGeneration);
                if (this._isDisposed || !this.IsOpen() || generation != this._openGeneration)
                    return;
                if (this._jsModule == null)
                    this._jsModule = await this.JSRuntime.InvokeAsync<IJSObjectReference>("import", "./js/editor.js");
                if (this._isDisposed || !this.IsOpen() || generation != this._openGeneration)
                    return;

                if (this._pendingContent != null)
                {
                    string content = this._pendingContent;
                    this._pendingContent = null;
                    string extension = System.IO.Path.GetExtension(this._filePath).ToLowerInvariant();
                    string language = await this._jsModule.InvokeAsync<string>("getLanguageFromExtension", extension);
                    if (this._dotNetRef == null)
                        this._dotNetRef = DotNetObjectReference.Create(this);
                    await this._jsModule.InvokeVoidAsync("setSaveCallback", this._dotNetRef);
                    await this._jsModule.InvokeVoidAsync("setEditorTheme", this.GetMonacoTheme());
                    if (this._isDisposed || generation != this._openGeneration)
                        return;
                    if (this._pendingHistory == null)
                        return;
                    bool initialized = await this._jsModule.InvokeAsync<bool>("initEditor", "monaco-container", content, language, this._session.Id.ToString(), this._session.Revision, this._pendingViewState, this.LeaseGeneration, this._pendingHistory, this._session.ModelEol);
                    if (this._isDisposed || generation != this._openGeneration)
                        return;
                    this._jsInitialized = initialized;
                    this._pendingViewState = null;
                    this._pendingHistory = null;
                    if (initialized && this._isVisible)
                        await this._jsModule.InvokeVoidAsync("layoutEditor");
                }

                if (this._isVisible && this._pendingActivation > 0)
                {
                    long activation = this._pendingActivation;
                    long revision = this._pendingActivationRevision;
                    this._pendingActivation = 0;
                    string result = await this._interopModule.InvokeAsync<string>("activateFloatingWindowWithResult", "editor-window", activation);
                    await this.CompleteRestoreAsync(revision, result);
                    if (result == "activated" && revision == this._lifecycleRevision)
                        await this._jsModule.InvokeVoidAsync("layoutEditor", activation);
                }
            }
            catch (Exception ex) when (ex is JSDisconnectedException || ex is OperationCanceledException || ex is ObjectDisposedException)
            {
            }
            finally
            {
                this._renderInteropPending = false;
                if (!this._isDisposed && this.IsOpen() && (this._pendingContent != null || this._pendingActivation > 0))
                    this.StateHasChanged();
            }
        }

        /// <summary>
        /// Receives the Ctrl+S save request from JavaScript
        /// </summary>
        [JSInvokable("OnEditorSave")]
        public async System.Threading.Tasks.Task OnEditorSaveAsync()
        {
            await this.InvokeAsync(this.HandleSaveAsync);
        }

        /// <summary>
        /// Retrieves Monaco content and saves it with lease-authorized commit; a save in progress ignores new requests
        /// </summary>
        private async System.Threading.Tasks.Task HandleSaveAsync()
        {
            await this.SaveAsync();
        }

        /// <summary>Salva una sola volta alla volta e riporta l'esito</summary>
        /// <returns>True solo quando il file è stato scritto e committato</returns>
        private async Task<bool> SaveAsync()
        {
            if (this._isSaving)
                return false;
            this._isSaving = true;
            this.StateHasChanged();
            try
            {
                return await this.HandleSaveCoreAsync();
            }
            finally
            {
                this._isSaving = false;
                if (!this._isDisposed)
                    this.StateHasChanged();
            }
        }

        /// <summary>Salva solo il documento catturato prima del flush, preservando le guardie di generazione</summary>
        /// <returns>True solo per il save committato</returns>
        private async Task<bool> HandleSaveCoreAsync()
        {
            if (this._isDisposed || this._session == null || this._jsModule == null || !this._jsInitialized)
                return false;

            long generation = this._openGeneration;
            Guid sessionId = this._session.Id;

            WorkspaceClientToken token = new WorkspaceClientToken(this.AttachmentId, this.LeaseGeneration);
            if (!this._workspaceService.ValidateMutation(token))
            {
                this._notificationService.Notify(Radzen.NotificationSeverity.Warning, "Save rejected", "This browser no longer controls the workspace.", 8000);
                return false;
            }

            // Il flush può sospendere l'adapter mentre il documento viene chiuso e riaperto
            if (!await this._jsModule.InvokeAsync<bool>("flushEditorCheckpoint"))
                return false;
            if (this._isDisposed || generation != this._openGeneration || this._session?.Id != sessionId)
                return false;
            EditorSessionSnapshot savingSession = this._workspaceService.GetEditorSession(token);
            if (savingSession == null || savingSession.Id != sessionId)
                return false;
            string content = savingSession.Content;

            CancellationToken revocationToken = this._workspaceService.GetRevocationToken(token);
            if (revocationToken.IsCancellationRequested || this._isDisposed || generation != this._openGeneration || this._session?.Id != sessionId)
                return false;

            // Keep the editor dirty when the write or final commit fails
            FileOperationResult result = await this._fileOperationService.WriteFileTextAsync(savingSession.FilePath, content, mutation => this._workspaceService.TryCommitEditorSave(token, sessionId, savingSession.Revision, content, mutation), revocationToken);
            if (this._isDisposed || !this.IsOpen() || generation != this._openGeneration || this._session?.Id != sessionId)
                return false;
            if (!result.Success)
            {
                this._notificationService.Notify(Radzen.NotificationSeverity.Error, "Save failed", result.ErrorMessage, 10000);
                this._isDirty = this._workspaceService.GetEditorSession(token)?.IsDirty ?? this._isDirty;
                await this.OnStateChanged.InvokeAsync();
                return false;
            }

            // Update state only after the final file commit
            EditorSessionSnapshot savedSession = this._workspaceService.GetEditorSession(token);
            if (savedSession != null && savedSession.Id == savingSession.Id)
                this.ApplySession(savedSession);
            await this.OnStateChanged.InvokeAsync();
            return true;
        }

        /// <summary>
        /// Handles the close button click: with unsaved changes asks Save, Discard or Cancel through a workspace workflow
        /// </summary>
        private async System.Threading.Tasks.Task HandleClose()
        {
            if (this._isSaving || this._session == null)
                return;
            if (this._jsInitialized && this._jsModule != null && !await this._jsModule.InvokeAsync<bool>("flushEditorCheckpoint"))
                return;
            WorkspaceClientToken token = this.GetClientToken();
            EditorSessionSnapshot session = this._workspaceService.GetEditorSession(token);
            if (session == null || session.Id != this._session?.Id)
                return;
            if (!session.IsDirty)
            {
                await this.CloseSessionAsync();
                return;
            }
            try
            {
                this._workspaceService.BeginConfirmationWorkflow(token, WorkspaceWorkflowKind.EditorClose, "Unsaved changes", "Save changes to " + this._fileName + " before closing?");
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is UnauthorizedAccessException || ex is ObjectDisposedException)
            {
                this._notificationService.Notify(Radzen.NotificationSeverity.Warning, "Editor not closed", "Finish the active workflow, then close the editor again.", 8000);
            }
        }

        /// <summary>Esegue la scelta consumata dalla domanda di chiusura: salva e chiude oppure scarta e chiude</summary>
        /// <param name="save">True per salvare prima di chiudere</param>
        /// <returns>Completamento della chiusura; un save fallito lascia l'editor aperto</returns>
        internal async Task CompleteCloseAsync(bool save)
        {
            if (this._isDisposed || this._session == null)
                return;
            if (save && !await this.SaveAsync())
                return;
            await this.CloseSessionAsync();
        }

        /// <summary>Chiude la sessione e notifica se nella sessione è stato eseguito almeno un save</summary>
        private async Task CloseSessionAsync()
        {
            bool saved = this._session?.SavedRevision > 0;
            this.Hide();
            if (this._session != null)
                return;
            await this.OnClose.InvokeAsync(saved);
        }

        /// <summary>Applica a Monaco il tema derivato dal tema Radzen corrente</summary>
        private void HandleThemeChanged()
        {
            _ = this.InvokeAsync(async () =>
            {
                if (this._isDisposed || this._jsModule == null)
                    return;
                try
                {
                    await this._jsModule.InvokeVoidAsync("setEditorTheme", this.GetMonacoTheme());
                }
                catch (Exception ex) when (ex is JSDisconnectedException || ex is OperationCanceledException || ex is ObjectDisposedException)
                {
                }
            });
        }

        /// <summary>Tema Monaco coerente con il tema Radzen: chiaro, scuro o ad alto contrasto con WCAG</summary>
        /// <returns>Identificatore del tema Monaco</returns>
        private string GetMonacoTheme()
        {
            string theme = this._themeService.Theme ?? RadzenThemeCatalog.DEFAULT_THEME;
            bool dark = theme == "dark" || theme.EndsWith("-dark", StringComparison.Ordinal);
            if (this._themeService.Wcag == true)
                return dark ? "hc-black" : "hc-light";
            return dark ? "vs-dark" : "vs";
        }

        #endregion

        /// <summary>Drena checkpoint e geometria senza salvare il file né ricostruire Monaco</summary>
        /// <param name="cancellationToken">Limite e lifecycle del tentativo</param>
        /// <returns>True solo per publisher confermati e sessione invariata</returns>
        internal async Task<bool> FlushForHandoffAsync(CancellationToken cancellationToken)
        {
            if (this._isDisposed || this._isSaving || this._restorePending || this._renderInteropPending)
                return false;
            if (this._session == null)
                return !this.IsOpen();
            Guid id = this._session.Id;
            long generation = this._openGeneration;
            if (this._jsModule == null || !this._jsInitialized || this._interopModule == null)
                return false;
            if (!await this._jsModule.InvokeAsync<bool>("flushEditorCheckpoint", cancellationToken))
                return false;
            if (!await this._interopModule.InvokeAsync<bool>("flushWindowGeometry", cancellationToken, "editor-window"))
                return false;
            return !this._isDisposed && !this._isSaving && generation == this._openGeneration && this._session?.Id == id && this._workspaceService.ValidatePublication(this.GetClientToken());
        }

        #region IAsyncDisposable

        /// <summary>
        /// Cleanup Monaco editor and JS references
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (this._isDisposed)
                return;
            this._isDisposed = true;
            this._themeService.ThemeChanged -= this.HandleThemeChanged;

            try
            {
                try
                {
                    try
                    {
                        if (this._jsModule != null)
                            await this._jsModule.InvokeVoidAsync("disposeEditor");
                    }
                    catch (Exception ex) when (ex is JSDisconnectedException || ex is OperationCanceledException)
                    {
                    }
                    finally
                    {
                        try
                        {
                            if (this._jsModule != null)
                                await this._jsModule.DisposeAsync();
                        }
                        catch (Exception ex) when (ex is JSDisconnectedException || ex is OperationCanceledException)
                        {
                        }
                    }
                }
                finally
                {
                    if (this._interopModule != null)
                    {
                        try
                        {
                            try
                            {
                                await this._interopModule.InvokeVoidAsync("disposeWindowDrag", "editor-window");
                            }
                            finally
                            {
                                await this._interopModule.DisposeAsync();
                            }
                        }
                        catch (Exception ex) when (ex is JSDisconnectedException || ex is OperationCanceledException)
                        {
                        }
                    }
                }
            }
            finally
            {
                if (this._dotNetRef != null)
                {
                    this._dotNetRef.Dispose();
                    this._dotNetRef = null;
                }
            }
        }

        #endregion
    }
}
