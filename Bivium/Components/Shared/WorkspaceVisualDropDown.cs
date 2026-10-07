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
    /// <summary>Rendering Radzen originale; espone solo hydration visuale tramite API supportate dalla derivazione</summary>
    public class WorkspaceVisualDropDown<TValue> : RadzenDropDown<TValue>, IAsyncDisposable
    {
        #region Variabili di classe

        /// <summary>Modulo del descriptor popup, senza publisher CAS separato</summary>
        private IJSObjectReference _surfaceModule;
        /// <summary>Callback di restore del controllo ufficiale</summary>
        private DotNetObjectReference<WorkspaceVisualDropDown<TValue>> _surfaceReference;
        /// <summary>Identità montata</summary>
        private (Guid Id, Guid Question, WorkspaceWorkflowPhase Phase, long Lease, string Control) _installed;
        /// <summary>Barriera delle continuazioni dopo il dispose</summary>
        private bool _disposed;
        /// <summary>Valore bound precedente, distinto dal cursore visuale non confermato</summary>
        private TValue _boundValue;
        /// <summary>Indica che esiste una baseline del valore bound</summary>
        private bool _boundInitialized;

        #endregion

        #region Metodi pubblici

        /// <summary>Ripristina apertura e cursore protetto senza SelectItem, ValueChanged, Change o comando Compress</summary>
        /// <param name="generation">Lease catturata dal descriptor</param>
        /// <param name="ownerId">Sessione o workflow catturato</param>
        /// <param name="questionId">Domanda Compress, vuota per Renamer</param>
        /// <param name="phase">Fase catturata</param>
        /// <param name="controlId">Identità app del controllo</param>
        /// <param name="visual">Popup del draft dialog acknowledged</param>
        /// <returns>True solo per un owner ancora autorizzato</returns>
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
                        // La stessa API pubblica usata da Radzen, senza il focus forzato di OpenPopup
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

        /// <summary>Rilascia il descriptor e il controllo Radzen, mai il workflow</summary>
        /// <returns>Cleanup del mount</returns>
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

        #region Metodi protected

        /// <summary>Un render dei parametri invariati non conferma né resetta l'highlight del popup aperto</summary>
        /// <returns>Lifecycle originale Radzen</returns>
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

        /// <summary>Installa il descriptor dopo il mount del controllo e del listbox Radzen</summary>
        /// <param name="firstRender">Primo render</param>
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

        #region Metodi privati

        /// <summary>Rivalida il proprietario catturato anche dopo ogni await, senza confermare valori</summary>
        /// <param name="generation">Lease del descriptor</param>
        /// <param name="ownerId">Sessione o workflow catturato</param>
        /// <param name="questionId">Domanda Compress, vuota per Renamer</param>
        /// <param name="phase">Fase catturata</param>
        /// <param name="controlId">Identità app del controllo</param>
        /// <returns>True per il medesimo mount autorizzato</returns>
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

        #region Proprietà

        /// <summary>Identità completa del mount visuale, indipendente dal valore selezionato</summary>
        private (Guid Id, Guid Question, WorkspaceWorkflowPhase Phase, long Lease, string Control) ViewIdentity => (this.RenamerSessionId != Guid.Empty ? this.RenamerSessionId : this.Workflow?.Id ?? Guid.Empty, this.RenamerSessionId != Guid.Empty ? Guid.Empty : this.Workflow?.QuestionId ?? Guid.Empty, this.RenamerSessionId != Guid.Empty ? WorkspaceWorkflowPhase.Dismissed : this.Workflow?.Phase ?? WorkspaceWorkflowPhase.Dismissed, this.LeaseGeneration, this.Name);

        /// <summary>Owner della domanda Compress catturata</summary>
        [Parameter] public WorkspaceWorkflowSnapshot Workflow { get; set; }
        /// <summary>Owner della sessione Renamer, distinto dal workflow batch</summary>
        [Parameter] public Guid RenamerSessionId { get; set; }
        /// <summary>Attachment del mount, mai trasferito dal payload visuale</summary>
        [Parameter] public string AttachmentId { get; set; } = "";
        /// <summary>Lease del mount</summary>
        [Parameter] public long LeaseGeneration { get; set; }
        /// <summary>Autorità delle sole pubblicazioni e hydration</summary>
        [Inject] private BiviumWorkspaceService WorkspaceService { get; set; }

        #endregion
    }
}
