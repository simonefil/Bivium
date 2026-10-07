using Microsoft.AspNetCore.Components;
using Bivium.Models;
using System.Collections.Generic;

namespace Bivium.Components.Shared
{
    /// <summary>
    /// Dialog for selecting archive format and output file name
    /// </summary>
    public partial class CompressDialog : ComponentBase
    {
        #region Parameters

        /// <summary>
        /// Rivalida l'autorità del browser prima di confermare la compressione
        /// </summary>
        [Parameter]
        public Func<bool> CanInvoke { get; set; }

        /// <summary>Proiezione del workflow, senza reinvocare Show</summary>
        [Parameter] public WorkspaceWorkflowSnapshot Workflow { get; set; }
        [Parameter] public string AttachmentId { get; set; } = "";
        [Parameter] public long LeaseGeneration { get; set; }
        [Inject] private Bivium.Services.BiviumWorkspaceService WorkspaceService { get; set; }
        private readonly WorkspaceFormBinding _binding = new WorkspaceFormBinding();

        /// <summary>Hydration della sola form catturata</summary>
        protected override void OnParametersSet()
        {
            if (this.Workflow == null)
            {
                this._isVisible = false;
                return;
            }
            if (!this._binding.Adopt(this.Workflow, this.LeaseGeneration))
                return;
            WorkspaceCompressDraft draft = System.Text.Json.JsonSerializer.Deserialize<WorkspaceCompressDraft>(this.Workflow.Draft);
            this._selectedFormat = draft.Format;
            this._outputName = draft.OutputName;
            this._baseName = draft.BaseName;
            this._isVisible = this.Workflow.Phase is WorkspaceWorkflowPhase.AwaitingInput or WorkspaceWorkflowPhase.Failed;
        }

        /// <summary>Checkpoint Immediate di formato e nome</summary>
        private void PublishDraft()
        {
            this._binding.Publish(this.WorkspaceService, new WorkspaceClientToken(this.AttachmentId, this.LeaseGeneration), this.GetDraft());
        }

        private string GetDraft() => System.Text.Json.JsonSerializer.Serialize(new WorkspaceCompressDraft(this._selectedFormat, this._outputName, this._baseName));

        /// <summary>Barriera semantica, senza avviare compressione</summary>
        internal System.Threading.Tasks.Task<bool> FlushForHandoffAsync(CancellationToken cancellationToken) => this._binding.FlushAsync(this.WorkspaceService, new WorkspaceClientToken(this.AttachmentId, this.LeaseGeneration), this.GetDraft(), cancellationToken);

        #endregion

        #region Class Variables

        /// <summary>
        /// Whether the dialog is visible
        /// </summary>
        private bool _isVisible = false;

        /// <summary>
        /// Selected archive format
        /// </summary>
        private ArchiveFormat _selectedFormat = ArchiveFormat.Zip;

        /// <summary>
        /// Output file name entered by the user
        /// </summary>
        private string _outputName = "";

        /// <summary>
        /// Base name derived from the source selection
        /// </summary>
        private string _baseName = "";

        /// <summary>
        /// Reference to the output name input for focus
        /// </summary>
        private Radzen.Blazor.RadzenTextBox _outputNameElement;

        /// <summary>Formati esistenti con etichette leggibili per il dropdown</summary>
        private readonly Dictionary<ArchiveFormat, string> _formats = new Dictionary<ArchiveFormat, string>
        {
            { ArchiveFormat.Zip, "ZIP (.zip)" },
            { ArchiveFormat.TarGz, "TAR.GZ (.tar.gz)" },
            { ArchiveFormat.TarBz2, "TAR.BZ2 (.tar.bz2)" },
            { ArchiveFormat.TarXz, "TAR.XZ (.tar.xz)" },
            { ArchiveFormat.TarZst, "TAR.ZST (.tar.zst)" },
            { ArchiveFormat.Tar, "TAR (.tar)" }
        };

        /// <summary>Estensione di ciascun formato; i suffissi composti precedono .tar nel riconoscimento</summary>
        private static readonly (ArchiveFormat Format, string Extension)[] _extensions =
        {
            (ArchiveFormat.TarGz, ".tar.gz"),
            (ArchiveFormat.TarBz2, ".tar.bz2"),
            (ArchiveFormat.TarXz, ".tar.xz"),
            (ArchiveFormat.TarZst, ".tar.zst"),
            (ArchiveFormat.Zip, ".zip"),
            (ArchiveFormat.Tar, ".tar")
        };

        #endregion

        #region Private Methods

        /// <summary>
        /// Focuses the output name input after render
        /// </summary>
        /// <param name="firstRender">Primo mount reale del contenuto</param>
        private async System.Threading.Tasks.Task HandleContentRenderedAsync(bool firstRender)
        {
            if (firstRender && this._isVisible)
                await this._outputNameElement.Element.FocusAsync();
        }

        /// <summary>
        /// Sostituisce soltanto l'estensione del nome digitato con quella del formato già aggiornato dal binding
        /// </summary>
        /// <param name="args">Valore selezionato, già applicato da @bind-Value</param>
        private void HandleFormatChange(object args)
        {
            string name = (this._outputName ?? "").Trim();
            foreach ((ArchiveFormat _, string extension) in _extensions)
            {
                if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                {
                    name = name.Substring(0, name.Length - extension.Length);
                    break;
                }
            }
            this._outputName = (string.IsNullOrEmpty(name) ? this._baseName : name) + Array.Find(_extensions, item => item.Format == this._selectedFormat).Extension;
            this.PublishDraft();
        }

        /// <summary>
        /// Handles confirm button
        /// </summary>
        private void HandleConfirm()
        {
            if (this.CanInvoke != null && !this.CanInvoke())
                return;

            this._binding.Respond(this.WorkspaceService, new WorkspaceClientToken(this.AttachmentId, this.LeaseGeneration), false);
        }

        /// <summary>
        /// Handles cancel button
        /// </summary>
        private void HandleCancel()
        {
            this._binding.Respond(this.WorkspaceService, new WorkspaceClientToken(this.AttachmentId, this.LeaseGeneration), true);
        }

        #endregion
    }
}
