using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Radzen.Blazor;
using Bivium.Models;
using Bivium.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Bivium.Components.Shared
{
    /// <summary>Original Radzen rendering; exposes only visual hydration through APIs supported by the derivation</summary>
    public class WorkspaceVisualDropDown<TValue> : RadzenDropDown<TValue>, IAsyncDisposable
    {
        #region Class Variables

        /// <summary>Popup descriptor module, without a separate CAS publisher</summary>
        private IJSObjectReference _surfaceModule;
        /// <summary>Restore callback of the official control</summary>
        private DotNetObjectReference<WorkspaceVisualDropDown<TValue>> _surfaceReference;
        /// <summary>Mounted identity</summary>
        private (Guid Id, Guid Question, WorkspaceWorkflowPhase Phase, long Lease, string Control) _installed;
        /// <summary>Continuation barrier after dispose</summary>
        private bool _disposed;
        /// <summary>Previous bound value, distinct from the unconfirmed visual cursor</summary>
        private TValue _boundValue;
        /// <summary>Indicates that a baseline of the bound value exists</summary>
        private bool _boundInitialized;

        #endregion

        #region Public Methods

        /// <summary>Restores opening and protected cursor without SelectItem, ValueChanged, Change or the Compress command</summary>
        /// <param name="generation">Lease captured by the descriptor</param>
        /// <param name="ownerId">Captured session or workflow</param>
        /// <param name="questionId">Compress question, empty for Renamer</param>
        /// <param name="phase">Captured phase</param>
        /// <param name="controlId">App identity of the control</param>
        /// <param name="visual">Popup of the acknowledged dialog draft</param>
        /// <returns>True only for a still authorized owner</returns>
        [JSInvokable]
        public async Task<bool> RestorePopupAsync(long generation, Guid ownerId, Guid questionId, WorkspaceWorkflowPhase phase, string controlId, WorkspaceFormatPopupVisual visual)
        {
            if (!this.CanRestore(generation, ownerId, questionId, phase, controlId) || visual == null || visual.HighlightIndex < -1 || visual.HighlightIndex >= (this.Data?.Cast<object>().Count() ?? 0))
                return false;
            this.selectedIndex = visual.HighlightIndex;
            bool opened = await this.JSRuntime.InvokeAsync<bool>("Radzen.popupOpened", this.PopupID);
            if (!this.CanRestore(generation, ownerId, questionId, phase, controlId))
                return false;
            if (visual.Open != opened)
            {
                if (visual.Open)
                {
                    if (visual.Focused)
                        await this.OpenPopup("ArrowDown", false, false);
                    else
                    {
                        // The same public API used by Radzen, without OpenPopup's forced focus
                        await this.JSRuntime.InvokeVoidAsync("Radzen.togglePopup", this.Element, this.PopupID, true);
                        this.isPopupOpen = true;
                    }
                }
                else
                {
                    await this.JSRuntime.InvokeVoidAsync("Radzen.closePopup", this.PopupID, null, null, null, true);
                    this.isPopupOpen = false;
                }
            }
            if (!this.CanRestore(generation, ownerId, questionId, phase, controlId))
                return false;
            await this.JSRuntime.InvokeVoidAsync("Radzen.selectListItem", this.search, this.list, visual.HighlightIndex);
            await this.InvokeAsync(this.StateHasChanged);
            return this.CanRestore(generation, ownerId, questionId, phase, controlId);
        }

        /// <summary>Releases the descriptor and the Radzen control, never the workflow</summary>
        /// <returns>Mount cleanup</returns>
        public async ValueTask DisposeAsync()
        {
            if (this._disposed)
                return;
            this._disposed = true;
            try
            {
                if (this._surfaceModule != null)
                {
                    try
                    {
                        try { await this._surfaceModule.InvokeVoidAsync("disposeDropDownControl", this.Element); }
                        finally { await this._surfaceModule.DisposeAsync(); }
                    }
                    catch (Exception ex) when (ex is JSDisconnectedException || ex is OperationCanceledException) { }
                }
            }
            finally
            {
                this._surfaceReference?.Dispose();
                base.Dispose();
            }
        }

        #endregion

        #region Protected Methods

        /// <summary>A render with unchanged parameters neither confirms nor resets the highlight of the open popup</summary>
        /// <returns>Original Radzen lifecycle</returns>
        protected override async Task OnParametersSetAsync()
        {
            int cursor = this.selectedIndex;
            bool preserve = this.isPopupOpen && this._installed == this.ViewIdentity && this._boundInitialized && EqualityComparer<TValue>.Default.Equals(this._boundValue, this.Value);
            await base.OnParametersSetAsync();
            if (preserve)
                this.selectedIndex = cursor;
            this._boundValue = this.Value;
            this._boundInitialized = true;
            (Guid Id, Guid Question, WorkspaceWorkflowPhase Phase, long Lease, string Control) identity = this.ViewIdentity;
            Dictionary<string, object> attributes = this.Attributes?.ToDictionary(item => item.Key, item => item.Value) ?? new Dictionary<string, object>();
            attributes["data-workspace-popup-identity"] = $"{identity.Id}:{identity.Question}:{(int)identity.Phase}:{identity.Lease}:{identity.Control}";
            this.Attributes = attributes;
        }

        /// <summary>Installs the descriptor after the mount of the Radzen control and listbox</summary>
        /// <param name="firstRender">First render</param>
        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            await base.OnAfterRenderAsync(firstRender);
            if (this._disposed)
                return;
            if (this.Workflow == null && this.RenamerSessionId == Guid.Empty)
            {
                if (this._surfaceModule != null && this._installed.Id != Guid.Empty)
                    await this._surfaceModule.InvokeVoidAsync("disposeDropDownControl", this.Element);
                this._installed = default;
                return;
            }
            (Guid Id, Guid Question, WorkspaceWorkflowPhase Phase, long Lease, string Control) identity = this.ViewIdentity;
            if (this._installed == identity)
                return;
            IJSObjectReference module = this._surfaceModule ?? await this.JSRuntime.InvokeAsync<IJSObjectReference>("import", "./js/surface-adapters.js");
            if (this._disposed || identity != this.ViewIdentity)
            {
                if (!ReferenceEquals(this._surfaceModule, module))
                    await module.DisposeAsync();
                return;
            }
            if (this._surfaceModule != null && !ReferenceEquals(this._surfaceModule, module))
            {
                await module.DisposeAsync();
                module = this._surfaceModule;
            }
            this._surfaceModule = module;
            this._surfaceReference ??= DotNetObjectReference.Create(this);
            if (await module.InvokeAsync<bool>("installDropDownControl", this.Element, this.PopupID, this.ListId, this._surfaceReference, identity.Lease, identity.Id, identity.Question, identity.Phase, identity.Control) && !this._disposed && identity == this.ViewIdentity)
                this._installed = identity;
        }

        #endregion

        #region Private Methods

        /// <summary>Revalidates the captured owner after every await as well, without confirming values</summary>
        /// <param name="generation">Descriptor lease</param>
        /// <param name="ownerId">Captured session or workflow</param>
        /// <param name="questionId">Compress question, empty for Renamer</param>
        /// <param name="phase">Captured phase</param>
        /// <param name="controlId">App identity of the control</param>
        /// <returns>True for the same authorized mount</returns>
        private bool CanRestore(long generation, Guid ownerId, Guid questionId, WorkspaceWorkflowPhase phase, string controlId)
        {
            WorkspaceClientToken token = new WorkspaceClientToken(this.AttachmentId, generation);
            if (this._disposed || generation != this.LeaseGeneration || this.Name != controlId || !this.WorkspaceService.ValidatePublication(token))
                return false;
            if (this.RenamerSessionId != Guid.Empty)
                return this.RenamerSessionId == ownerId && questionId == Guid.Empty && this.WorkspaceService.GetRenamerSession(token)?.Id == ownerId;
            WorkspaceWorkflowSnapshot current = this.WorkspaceService.GetWorkflow(token);
            return this.Workflow?.Id == ownerId && this.Workflow.QuestionId == questionId && this.Workflow.Phase == phase && current?.Id == ownerId && current.QuestionId == questionId && current.Phase == phase && current.Kind == WorkspaceWorkflowKind.Compress;
        }

        #endregion

        #region Properties

        /// <summary>Full identity of the visual mount, independent of the selected value</summary>
        private (Guid Id, Guid Question, WorkspaceWorkflowPhase Phase, long Lease, string Control) ViewIdentity => (this.RenamerSessionId != Guid.Empty ? this.RenamerSessionId : this.Workflow?.Id ?? Guid.Empty, this.RenamerSessionId != Guid.Empty ? Guid.Empty : this.Workflow?.QuestionId ?? Guid.Empty, this.RenamerSessionId != Guid.Empty ? WorkspaceWorkflowPhase.Dismissed : this.Workflow?.Phase ?? WorkspaceWorkflowPhase.Dismissed, this.LeaseGeneration, this.Name);

        /// <summary>Owner of the captured Compress question</summary>
        [Parameter] public WorkspaceWorkflowSnapshot Workflow { get; set; }
        /// <summary>Owner of the Renamer session, distinct from the batch workflow</summary>
        [Parameter] public Guid RenamerSessionId { get; set; }
        /// <summary>Mount attachment, never transferred by the visual payload</summary>
        [Parameter] public string AttachmentId { get; set; } = "";
        /// <summary>Mount lease</summary>
        [Parameter] public long LeaseGeneration { get; set; }
        /// <summary>Authority of publications and hydration only</summary>
        [Inject] private BiviumWorkspaceService WorkspaceService { get; set; }

        #endregion
    }
}
