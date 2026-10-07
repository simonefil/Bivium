using Microsoft.AspNetCore.Components;
using Bivium.Models;
using Bivium.Services;

namespace Bivium.Components.Shared
{
    /// <summary>
    /// Dialog for editing file and directory permissions
    /// </summary>
    public partial class PermissionsDialog : ComponentBase
    {
        #region Injected Services

        /// <summary>
        /// Workspace lease authority
        /// </summary>
        [Inject]
        private BiviumWorkspaceService _workspaceService { get; set; }

        #endregion

        #region Parameters

        [Parameter]
        public string AttachmentId { get; set; } = "";

        [Parameter]
        public long LeaseGeneration { get; set; }

        [Parameter] public WorkspaceWorkflowSnapshot Workflow { get; set; }
        private readonly WorkspaceFormBinding _binding = new WorkspaceFormBinding();

        /// <summary>Ripristina il draft e il contesto originale senza rileggere permessi</summary>
        protected override void OnParametersSet()
        {
            if (this.Workflow == null)
            {
                this._isVisible = false;
                return;
            }
            if (!this._binding.Adopt(this.Workflow, this.LeaseGeneration))
                return;
            WorkspacePermissionsDraft draft = System.Text.Json.JsonSerializer.Deserialize<WorkspacePermissionsDraft>(this.Workflow.Draft);
            WorkspacePermissionsContext context = System.Text.Json.JsonSerializer.Deserialize<WorkspacePermissionsContext>(this.Workflow.InvocationParameters.FormContext);
            this._model = draft.Model;
            this._recursive = draft.Recursive;
            this._entryName = context.EntryName;
            this._isDirectory = context.IsDirectory;
            this._canSave = context.CanSave;
            this._errorMessage = this.Workflow.ErrorMessage;
            this._isVisible = this.Workflow.Phase is WorkspaceWorkflowPhase.AwaitingInput or WorkspaceWorkflowPhase.Failed;
        }

        private string GetDraft() => System.Text.Json.JsonSerializer.Serialize(new WorkspacePermissionsDraft(this._model, this._recursive));
        private void PublishDraft() => this._binding.Publish(this._workspaceService, new WorkspaceClientToken(this.AttachmentId, this.LeaseGeneration), this.GetDraft());

        /// <summary>Confronta il checkpoint senza aspettare la ricorsione</summary>
        internal System.Threading.Tasks.Task<bool> FlushForHandoffAsync(CancellationToken cancellationToken) => this._binding.FlushAsync(this._workspaceService, new WorkspaceClientToken(this.AttachmentId, this.LeaseGeneration), this.GetDraft(), cancellationToken);

        #endregion

        #region Class Variables

        /// <summary>
        /// Whether the dialog is visible
        /// </summary>
        private bool _isVisible = false;

        /// <summary>
        /// Name of the entry being edited
        /// </summary>
        private string _entryName = "";

        /// <summary>
        /// Whether the entry is a directory
        /// </summary>
        private bool _isDirectory = false;

        /// <summary>
        /// Permission model being edited
        /// </summary>
        private PermissionModel _model = new PermissionModel();

        /// <summary>
        /// Whether to apply permissions recursively
        /// </summary>
        private bool _recursive = false;

        /// <summary>
        /// Error message to display
        /// </summary>
        private string _errorMessage = "";

        /// <summary>
        /// Whether permissions were loaded successfully and can be saved
        /// </summary>
        private bool _canSave = false;

        /// <summary>
        /// Reference to the dialog element for focus
        /// </summary>
        private Radzen.Blazor.RadzenButton _cancelButton;

        #endregion

        #region Private Methods

        /// <summary>
        /// Focuses the dialog element after render
        /// </summary>
        /// <param name="firstRender">Primo mount reale del contenuto</param>
        private async System.Threading.Tasks.Task HandleContentRenderedAsync(bool firstRender)
        {
            if (firstRender && this._isVisible)
                await this._cancelButton.Element.FocusAsync();
        }

        /// <summary>Conferma il draft; permessi e ownership vengono applicati dal workflow server</summary>
        private void HandleSave()
        {
            this._binding.Respond(this._workspaceService, new WorkspaceClientToken(this.AttachmentId, this.LeaseGeneration), false);
        }

        /// <summary>Chiude senza applicare modifiche</summary>
        private void HandleCancel()
        {
            this._binding.Respond(this._workspaceService, new WorkspaceClientToken(this.AttachmentId, this.LeaseGeneration), true);
        }

        /// <summary>
        /// Calculates the octal permission string for Unix permissions
        /// </summary>
        /// <returns>Octal string (and.g. 755)</returns>
        private string GetOctalString()
        {
            int owner = 0;
            int group = 0;
            int others = 0;

            // Owner bits
            if (this._model.OwnerRead) owner += 4;
            if (this._model.OwnerWrite) owner += 2;
            if (this._model.OwnerExecute) owner += 1;

            // Group bits
            if (this._model.GroupRead) group += 4;
            if (this._model.GroupWrite) group += 2;
            if (this._model.GroupExecute) group += 1;

            // Others bits
            if (this._model.OthersRead) others += 4;
            if (this._model.OthersWrite) others += 2;
            if (this._model.OthersExecute) others += 1;

            string result = owner.ToString() + group.ToString() + others.ToString();
            return result;
        }

        #endregion
    }
}
