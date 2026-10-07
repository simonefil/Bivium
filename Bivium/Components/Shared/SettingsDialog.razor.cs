using Microsoft.AspNetCore.Components;

namespace Bivium.Components.Shared
{
    /// <summary>
    /// Dialog for editing the list of editable file extensions
    /// </summary>
    public partial class SettingsDialog : ComponentBase
    {
        #region Parameters

        [Parameter]
        public string AttachmentId { get; set; } = "";

        [Parameter]
        public long LeaseGeneration { get; set; }

        [Parameter] public Bivium.Models.WorkspaceWorkflowSnapshot Workflow { get; set; }
        [Inject] private Bivium.Services.BiviumWorkspaceService WorkspaceService { get; set; }
        private readonly WorkspaceFormBinding _binding = new WorkspaceFormBinding();

        /// <summary>Hydration without loading or saving settings</summary>
        protected override void OnParametersSet()
        {
            if (this.Workflow == null)
            {
                this._isVisible = false;
                return;
            }
            if (!this._binding.Adopt(this.Workflow, this.LeaseGeneration))
                return;
            this._extensionsText = this.Workflow.Draft;
            this._statusText = this.Workflow.ErrorMessage;
            this._isVisible = this.Workflow.Phase is Bivium.Models.WorkspaceWorkflowPhase.AwaitingInput or Bivium.Models.WorkspaceWorkflowPhase.Failed;
        }

        private void PublishDraft() => this._binding.Publish(this.WorkspaceService, new Bivium.Models.WorkspaceClientToken(this.AttachmentId, this.LeaseGeneration), this._extensionsText);

        /// <summary>The barrier waits only for the text checkpoint</summary>
        internal System.Threading.Tasks.Task<bool> FlushForHandoffAsync(CancellationToken cancellationToken) => this._binding.FlushAsync(this.WorkspaceService, new Bivium.Models.WorkspaceClientToken(this.AttachmentId, this.LeaseGeneration), this._extensionsText, cancellationToken);

        #endregion

        #region Class Variables

        /// <summary>
        /// Whether the dialog is visible
        /// </summary>
        private bool _isVisible = false;

        /// <summary>
        /// Extensions text, one for line
        /// </summary>
        private string _extensionsText = "";

        /// <summary>
        /// Status or error text
        /// </summary>
        private string _statusText = "";

        /// <summary>
        /// Reference to the textarea for focus
        /// </summary>
        private Radzen.Blazor.RadzenTextArea _textareaElement;

        #endregion

        #region Private Methods

        /// <summary>
        /// Focuses the textarea after render
        /// </summary>
        /// <param name="firstRender">First real mount of the content</param>
        private async System.Threading.Tasks.Task HandleContentRenderedAsync(bool firstRender)
        {
            if (firstRender && this._isVisible)
                await this._textareaElement.Element.FocusAsync();
        }

        /// <summary>Confirms the draft; saving the extensions belongs to the server workflow</summary>
        private void HandleSave()
        {
            this._binding.Respond(this.WorkspaceService, new Bivium.Models.WorkspaceClientToken(this.AttachmentId, this.LeaseGeneration), false);
        }

        /// <summary>Closes without saving</summary>
        private void HandleCancel()
        {
            this._binding.Respond(this.WorkspaceService, new Bivium.Models.WorkspaceClientToken(this.AttachmentId, this.LeaseGeneration), true);
        }

        #endregion
    }
}
