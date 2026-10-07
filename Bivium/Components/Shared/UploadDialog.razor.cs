using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Bivium.Models;
using Bivium.Services;

namespace Bivium.Components.Shared
{
    /// <summary>
    /// Dialog for hierarchical chunked file and directory uploads
    /// </summary>
    public partial class UploadDialog : ComponentBase, IAsyncDisposable
    {
        #region Parameters

        /// <summary>
        /// Attachment authorized for the mutating upload
        /// </summary>
        [Parameter]
        public string AttachmentId { get; set; } = "";

        /// <summary>
        /// Generation authorized for the mutating upload
        /// </summary>
        [Parameter]
        public long LeaseGeneration { get; set; }

        /// <summary>Projection autorizzata; mount e hydration non inviano byte</summary>
        [Parameter] public WorkspaceUploadSnapshot Upload { get; set; }
        [Inject] private BiviumWorkspaceService WorkspaceService { get; set; }

        #endregion

        #region Class Variables

        /// <summary>
        /// Whether the dialog is visible
        /// </summary>
        private bool _isVisible = false;

        /// <summary>
        /// Target directory for the upload
        /// </summary>
        private string _destinationDir = "";

        /// <summary>
        /// Number of selected files
        /// </summary>
        private int _fileCount = 0;

        /// <summary>
        /// Number of selected directories
        /// </summary>
        private int _directoryCount = 0;

        /// <summary>
        /// Total bytes across selected files
        /// </summary>
        private long _totalBytes = 0;

        /// <summary>
        /// Upload progress percentage (0-100)
        /// </summary>
        private int _progress = 0;

        /// <summary>
        /// Whether an upload is in progress
        /// </summary>
        private bool _isUploading = false;

        /// <summary>
        /// Status or error text
        /// </summary>
        private string _statusText = "";

        /// <summary>
        /// Current relative path being uploaded or prepared
        /// </summary>
        private string _currentItem = "";

        /// <summary>
        /// Number of completely uploaded files
        /// </summary>
        private int _processedFiles = 0;

        /// <summary>
        /// Total files in the active upload
        /// </summary>
        private int _uploadFileCount = 0;

        /// <summary>
        /// JS module reference for upload interop
        /// </summary>
        private IJSObjectReference _jsModule;

        /// <summary>
        /// .NET object reference for JS callbacks
        /// </summary>
        private DotNetObjectReference<UploadDialog> _dotNetRef;

        /// <summary>
        /// Whether component-owned callbacks have been released
        /// </summary>
        private bool _isDisposed;

        /// <summary>
        /// Reference to the Browse button for focus
        /// </summary>
        private Radzen.Blazor.RadzenButton _browseButton;

        /// <summary>Bridge pronto per la selezione nel mount corrente</summary>
        private bool _bridgeReady;

        /// <summary>Generazione locale dell'apertura per scartare inizializzazioni obsolete</summary>
        private long _showGeneration;

        /// <summary>Disponibilità delle sorgenti soltanto nel browser corrente</summary>
        private bool _hasLocalSelection;

        /// <summary>Identità del mount per scartare callback tardivi</summary>
        private Guid _sessionId;

        /// <summary>Lease con la quale è stato inizializzato il mount</summary>
        private long _generation;

        /// <summary>Ultima projection autorizzata, letta dal parametro o dai callback; il parametro non viene mai sovrascritto</summary>
        private WorkspaceUploadSnapshot _upload;

        /// <summary>Fase autorevole proiettata dal workspace</summary>
        private WorkspaceUploadPhase _phase = WorkspaceUploadPhase.Selecting;

        /// <summary>Annullamento in corso: Cancel è idempotente finché il servizio non conclude</summary>
        private bool _isCancelling;

        /// <summary>Pausa in corso: il trasporto browser e il writer server devono quietarsi</summary>
        private bool _isPausing;

        #endregion

        #region Lifecycle

        /// <summary>Ripristina destinazione, manifest e progresso dal workspace, non dai callback locali</summary>
        protected override void OnParametersSet()
        {
            this.ApplyUpload(this.Upload);
        }

        #endregion

        #region JS Invokable Methods

        /// <summary>
        /// Called from JS when files or directories are added to the upload queue
        /// </summary>
        /// <param name="sessionId">Sessione del mount browser</param>
        /// <param name="generation">Lease catturata</param>
        /// <param name="hasSelection">Disponibilità locale delle sorgenti, non stato di dominio</param>
        /// <returns>True quando l'adapter appartiene ancora alla sessione autorizzata</returns>
        [JSInvokable]
        public bool OnUploadSelectionChanged(string sessionId, long generation, bool hasSelection)
        {
            if (!this.IsCurrentCallback(sessionId, generation))
                return false;
            WorkspaceClientToken token = new WorkspaceClientToken(this.AttachmentId, generation);
            WorkspaceUploadSnapshot upload = this.WorkspaceService.GetUpload(token);
            if (upload?.Id != this._sessionId || !upload.Visible)
                return false;
            this._hasLocalSelection = hasSelection;
            this.ApplyUpload(upload);
            this.InvokeAsync(() => this.StateHasChanged());
            return true;
        }

        /// <summary>Errore preflight locale; nessuna mutazione o pubblicazione nel workspace</summary>
        /// <param name="sessionId">Sessione del mount browser</param>
        /// <param name="generation">Lease catturata</param>
        /// <param name="message">Errore della verifica delle sorgenti</param>
        /// <returns>Aggiornamento del solo dialog del chiamante</returns>
        [JSInvokable]
        public async System.Threading.Tasks.Task OnUploadVerificationFailed(string sessionId, long generation, string message)
        {
            if (!this.IsCurrentCallback(sessionId, generation))
                return;
            this._isUploading = false;
            this._statusText = "Error: " + message;
            await this.InvokeAsync(() => this.StateHasChanged());
        }

        /// <summary>
        /// Called from JS when an admitted upload completes or fails
        /// </summary>
        /// <param name="success">True if upload succeeded</param>
        /// <param name="message">Error message on failure</param>
        [JSInvokable]
        public async System.Threading.Tasks.Task OnUploadComplete(string sessionId, long generation, bool success, string message)
        {
            if (!this.IsCurrentCallback(sessionId, generation))
                return;

            this._isUploading = false;

            if (success)
            {
                this.ApplyUpload(this.WorkspaceService.GetUpload(new WorkspaceClientToken(this.AttachmentId, generation)));
            }
            else
            {
                try
                {
                    this.ApplyUpload(this.WorkspaceService.PauseUpload(new WorkspaceClientToken(this.AttachmentId, generation), this._sessionId, message));
                }
                catch (OperationCanceledException) { }
                catch (InvalidOperationException) { }
            }

            await this.InvokeAsync(() => this.StateHasChanged());
        }

        /// <summary>Callback del mount obsoleto o della lease revocata non modificano la sessione</summary>
        private bool IsCurrentCallback(string sessionId, long generation) => !this._isDisposed && Guid.TryParse(sessionId, out Guid id) && id == this._sessionId && generation == this.LeaseGeneration && this.WorkspaceService.ValidatePublication(new WorkspaceClientToken(this.AttachmentId, generation));

        /// <summary>Ferma solo il trasporto browser e aspetta il rollback del chunk corrente, non l'intero upload</summary>
        internal async System.Threading.Tasks.Task<bool> FlushForHandoffAsync(CancellationToken cancellationToken)
        {
            if (this._upload?.Visible != true)
                return true;
            if (this._jsModule == null || !this._bridgeReady)
                return false;
            try
            {
                await this._jsModule.InvokeVoidAsync("pauseUpload", cancellationToken, this._sessionId.ToString(), this.LeaseGeneration);
                this.ApplyUpload(await this.WorkspaceService.PauseUploadTransferAsync(new WorkspaceClientToken(this.AttachmentId, this.LeaseGeneration), this._sessionId, cancellationToken));
            }
            finally
            {
                this._isUploading = false;
                if (!this._isDisposed)
                    this.StateHasChanged();
            }
            return this.WorkspaceService.ValidatePublication(new WorkspaceClientToken(this.AttachmentId, this.LeaseGeneration));
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Initializes JS module and focuses the Browse button
        /// </summary>
        /// <param name="firstRender">Primo mount reale del contenuto nativo</param>
        private async System.Threading.Tasks.Task HandleContentRenderedAsync(bool firstRender)
        {
            if (!firstRender || this._isDisposed || !this._isVisible)
                return;

            long generation = this._showGeneration;
            await this.InitializeJsModule();
            if (this._isDisposed || !this._isVisible || generation != this._showGeneration || this._jsModule == null)
                return;
            await this._jsModule.InvokeVoidAsync("initUpload", this._dotNetRef, this._sessionId.ToString(), this.LeaseGeneration, this.AttachmentId);
            if (this._isDisposed || !this._isVisible || generation != this._showGeneration)
                return;
            this._bridgeReady = true;
            await this._browseButton.Element.FocusAsync();
        }

        /// <summary>
        /// Initializes the JS upload module and passes the .NET reference
        /// </summary>
        private async System.Threading.Tasks.Task InitializeJsModule()
        {
            if (this._isDisposed || !this._isVisible)
                return;

            if (this._jsModule == null)
            {
                IJSObjectReference module = await this.JSRuntime.InvokeAsync<IJSObjectReference>("import", "./js/upload.js");
                if (this._isDisposed || !this._isVisible)
                {
                    await module.DisposeAsync();
                    return;
                }
                this._jsModule = module;
            }
            if (this._isDisposed || !this._isVisible)
                return;

            // Create .NET reference for callbacks
            if (this._dotNetRef == null)
            {
                this._dotNetRef = DotNetObjectReference.Create(this);
            }

        }

        /// <summary>
        /// Clears the current upload queue
        /// </summary>
        private async System.Threading.Tasks.Task HandleClearSelection()
        {
            if (this._jsModule == null || this._isUploading || this._isCancelling || this._isPausing)
                return;

            await this._jsModule.InvokeVoidAsync("clearUploadSelection");
        }

        /// <summary>
        /// Handles the Upload button click - starts chunked upload via JS
        /// </summary>
        private async System.Threading.Tasks.Task HandleUpload()
        {
            if (this._jsModule == null || !this._bridgeReady || this._isUploading || this._isCancelling || this._isPausing || !this._hasLocalSelection)
                return;

            this._isUploading = true;
            this._statusText = "Uploading...";
            this._uploadFileCount = this._fileCount;
            this.StateHasChanged();

            if (!await this._jsModule.InvokeAsync<bool>("uploadSelection", this._sessionId.ToString(), this.AttachmentId, this.LeaseGeneration))
            {
                this._isUploading = false;
                this.StateHasChanged();
            }
        }

        /// <summary>
        /// Handles the Cancel button click
        /// </summary>
        private async System.Threading.Tasks.Task HandleCancel()
        {
            if (!this._isVisible || this._isCancelling)
                return;

            WorkspaceClientToken token = new WorkspaceClientToken(this.AttachmentId, this.LeaseGeneration);
            if (!this.WorkspaceService.ValidateMutation(token))
                return;
            this._isCancelling = true;
            try
            {
                await this.WorkspaceService.CancelUploadAsync(token, this._sessionId);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is OperationCanceledException || ex is UnauthorizedAccessException)
            {
                // La sessione è già stata annullata o revocata: lo stato autorevole arriva dal workspace
            }
            finally
            {
                this._isCancelling = false;
            }
            if (this._isDisposed)
                return;
            bool wasUploading = this._isUploading;
            this._isUploading = false;
            this._isVisible = false;
            this._bridgeReady = false;
            this._showGeneration++;
            this._progress = 0;
            this._statusText = "";
            this._currentItem = "";
            this._processedFiles = 0;
            this._uploadFileCount = 0;
            this._fileCount = 0;
            this._directoryCount = 0;
            this._totalBytes = 0;
            if (wasUploading && this._jsModule != null)
                await this._jsModule.InvokeVoidAsync("cancelUpload");
            else if (this._jsModule != null)
                await this._jsModule.InvokeVoidAsync("clearUploadSelection");
        }

        /// <summary>
        /// Mette in pausa il trasferimento conservando i chunk ricevuti e la selezione locale
        /// </summary>
        private async System.Threading.Tasks.Task HandlePause()
        {
            if (this._jsModule == null || !this._isUploading || this._isPausing || this._isCancelling)
                return;

            WorkspaceClientToken token = new WorkspaceClientToken(this.AttachmentId, this.LeaseGeneration);
            if (!this.WorkspaceService.ValidateMutation(token))
                return;
            this._isPausing = true;
            try
            {
                await this._jsModule.InvokeVoidAsync("pauseUpload", this._sessionId.ToString(), this.LeaseGeneration);
                this.ApplyUpload(await this.WorkspaceService.PauseUploadTransferAsync(token, this._sessionId, CancellationToken.None));
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is OperationCanceledException || ex is UnauthorizedAccessException || ex is JSException)
            {
                this._statusText = "Error: " + ex.Message;
            }
            finally
            {
                this._isPausing = false;
                this._isUploading = false;
            }
        }

        /// <summary>
        /// Applica la projection autorevole senza modificare il parametro ricevuto dal Commander
        /// </summary>
        /// <param name="upload">Projection del workspace</param>
        private void ApplyUpload(WorkspaceUploadSnapshot upload)
        {
            bool changed = this._sessionId != (upload?.Id ?? Guid.Empty) || this._generation != this.LeaseGeneration;
            if (changed)
            {
                this._sessionId = upload?.Id ?? Guid.Empty;
                this._generation = this.LeaseGeneration;
                this._hasLocalSelection = false;
                this._bridgeReady = false;
                this._isUploading = false;
                this._showGeneration++;
            }
            this._upload = upload;
            this._isVisible = upload?.Visible == true;
            if (upload == null)
                return;
            this._phase = upload.Phase;
            this._destinationDir = upload.Destination;
            this._fileCount = upload.Files.Length;
            this._directoryCount = upload.Directories.Length;
            this._totalBytes = upload.TotalBytes;
            this._processedFiles = upload.CompletedFiles;
            this._uploadFileCount = this._fileCount;
            this._progress = upload.Percent;
            this._currentItem = upload.CurrentPath;
            this._statusText = !string.IsNullOrEmpty(upload.Error) ? "Error: " + upload.Error : upload.Phase == WorkspaceUploadPhase.Paused ? "Upload paused. Received chunks are preserved." : "";
        }

        /// <summary>
        /// Etichetta dell'azione di avvio derivata dalla fase autorevole
        /// </summary>
        /// <returns>Upload oppure Resume per un trasferimento in pausa o fallito</returns>
        private string GetStartLabel()
        {
            return this._phase is WorkspaceUploadPhase.Paused or WorkspaceUploadPhase.Failed ? "Resume" : "Upload";
        }

        /// <summary>
        /// Indica se il suggerimento della drop zone deve restare visibile anche senza trascinamento
        /// </summary>
        /// <returns>True durante l'upload o quando le sorgenti vanno riselezionate</returns>
        private bool IsDropHintPersistent()
        {
            return this._isUploading || (this._fileCount > 0 && !this._hasLocalSelection);
        }

        /// <summary>
        /// Testo del suggerimento della drop zone coerente con lo stato corrente
        /// </summary>
        /// <returns>Suggerimento da mostrare</returns>
        private string GetDropHint()
        {
            if (this._isUploading)
                return "The selection is locked while the upload is running.";
            if (this._fileCount > 0 && !this._hasLocalSelection)
                return "Reselect the original files and folders. Browser file handles are not transferred.";
            return "Selections are added to the current queue";
        }

        /// <summary>
        /// Formats the current upload queue summary
        /// </summary>
        /// <returns>Selection summary</returns>
        private string GetSelectionSummary()
        {
            if (this._fileCount == 0 && this._directoryCount == 0)
                return "No files or folders selected";

            return this._fileCount + " file(s), " + this._directoryCount + " folder(s), " + ByteSizeFormatter.Format(this._totalBytes);
        }

        /// <summary>
        /// Formats the current file counter prefix
        /// </summary>
        /// <returns>Progress prefix</returns>
        private string GetProgressSummary()
        {
            if (this._uploadFileCount <= 0)
                return "";
            int currentFile = Math.Min(this._processedFiles + 1, this._uploadFileCount);
            return "[" + currentFile + "/" + this._uploadFileCount + "] ";
        }

        #endregion

        #region IAsyncDisposable

        /// <summary>
        /// Cleanup JS references
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (this._isDisposed)
                return;
            this._isDisposed = true;

            try
            {
                if (this._jsModule != null)
                {
                    try
                    {
                        await this._jsModule.InvokeVoidAsync("dispose", this._sessionId.ToString(), this._generation);
                    }
                    finally
                    {
                        await this._jsModule.DisposeAsync();
                    }
                }
            }
            catch (Exception ex) when (ex is JSDisconnectedException || ex is OperationCanceledException)
            {
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
