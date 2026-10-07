using System.Text.Json;
using Bivium.Models;
using Microsoft.AspNetCore.Components;

namespace Bivium.Components.Shared
{
    /// <summary>
    /// Dialog for configuring ownership and permissions of newly created entries
    /// </summary>
    public partial class CreationPermissionsDialog : ComponentBase
    {
        #region Parameters

        /// <summary>
        /// Attachment authorized to update settings
        /// </summary>
        [Parameter]
        public string AttachmentId { get; set; } = "";

        /// <summary>
        /// Lease generation authorized to update settings
        /// </summary>
        [Parameter]
        public long LeaseGeneration { get; set; }

        [Parameter] public WorkspaceWorkflowSnapshot Workflow { get; set; }
        [Inject] private Bivium.Services.BiviumWorkspaceService WorkspaceService { get; set; }
        private readonly WorkspaceFormBinding _binding = new WorkspaceFormBinding();

        /// <summary>Hydration di owner, group, bit e modalità senza reset dalle options</summary>
        protected override void OnParametersSet()
        {
            if (this.Workflow == null)
            {
                this._isVisible = false;
                return;
            }
            if (!this._binding.Adopt(this.Workflow, this.LeaseGeneration))
                return;
            this._model = JsonSerializer.Deserialize<DefaultCreationPermissionsSettings>(this.Workflow.Draft);
            this._useSystemDefaults = !this._model.Enabled;
            this._statusText = this.Workflow.ErrorMessage;
            this._isVisible = this.Workflow.Phase is WorkspaceWorkflowPhase.AwaitingInput or WorkspaceWorkflowPhase.Failed;
        }

        private string GetDraft()
        {
            this._model.Enabled = !this._useSystemDefaults;
            return JsonSerializer.Serialize(this._model);
        }

        private void PublishDraft() => this._binding.Publish(this.WorkspaceService, new WorkspaceClientToken(this.AttachmentId, this.LeaseGeneration), this.GetDraft());

        /// <summary>Barriera del draft, mai della scrittura impostazioni</summary>
        internal System.Threading.Tasks.Task<bool> FlushForHandoffAsync(CancellationToken cancellationToken) => this._binding.FlushAsync(this.WorkspaceService, new WorkspaceClientToken(this.AttachmentId, this.LeaseGeneration), this.GetDraft(), cancellationToken);

        #endregion

        #region Class Variables

        /// <summary>
        /// Whether the dialog is visible
        /// </summary>
        private bool _isVisible = false;

        /// <summary>
        /// Whether operating system defaults should remain untouched
        /// </summary>
        private bool _useSystemDefaults = true;

        /// <summary>
        /// Whether Unix permission controls should be displayed
        /// </summary>
        private readonly bool _isUnix = !OperatingSystem.IsWindows();

        /// <summary>
        /// Settings being edited
        /// </summary>
        private DefaultCreationPermissionsSettings _model = new DefaultCreationPermissionsSettings();

        /// <summary>
        /// Status or error text
        /// </summary>
        private string _statusText = "";

        /// <summary>
        /// Dialog element used for keyboard focus
        /// </summary>
        private Radzen.Blazor.RadzenButton _cancelButton;

        #endregion

        #region Private Methods

        /// <summary>
        /// Focuses the dialog after rendering
        /// </summary>
        /// <param name="firstRender">Primo mount reale del contenuto</param>
        private async System.Threading.Tasks.Task HandleContentRenderedAsync(bool firstRender)
        {
            if (firstRender && this._isVisible)
                await this._cancelButton.Element.FocusAsync();
        }

        /// <summary>Conferma il draft acknowledged; il salvataggio appartiene al workflow server</summary>
        private void HandleSave()
        {
            this._binding.Respond(this.WorkspaceService, new WorkspaceClientToken(this.AttachmentId, this.LeaseGeneration), false);
        }

        /// <summary>Chiude senza salvare</summary>
        private void HandleCancel()
        {
            this._binding.Respond(this.WorkspaceService, new WorkspaceClientToken(this.AttachmentId, this.LeaseGeneration), true);
        }

        /// <summary>
        /// Calculates the octal Unix permission string
        /// </summary>
        /// <param name="permissions">Permission values</param>
        /// <returns>Three-digit octal string</returns>
        private string GetOctalString(CreationPermissionSettings permissions)
        {
            int owner = (permissions.OwnerRead ? 4 : 0) + (permissions.OwnerWrite ? 2 : 0) + (permissions.OwnerExecute ? 1 : 0);
            int group = (permissions.GroupRead ? 4 : 0) + (permissions.GroupWrite ? 2 : 0) + (permissions.GroupExecute ? 1 : 0);
            int others = (permissions.OthersRead ? 4 : 0) + (permissions.OthersWrite ? 2 : 0) + (permissions.OthersExecute ? 1 : 0);
            return owner.ToString() + group.ToString() + others.ToString();
        }

        #endregion
    }
}
