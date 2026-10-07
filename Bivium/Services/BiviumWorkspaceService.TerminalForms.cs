using Bivium.Models;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json;

namespace Bivium.Services
{
    public sealed partial class BiviumWorkspaceService
    {
        #region Variabili di classe

        /// <summary>Coda bounded server-owned di richieste OSC, indipendente dal circuito</summary>
        private readonly Queue<WorkspaceWorkflowSnapshot> _terminalClipboardRequests = new Queue<WorkspaceWorkflowSnapshot>();

        #endregion

        #region Metodi pubblici

        /// <summary>Pubblicazione dopo il rilascio del lock terminale; non accede alla clipboard OS</summary>
        /// <param name="request">Richiesta OSC già catturata dalla sessione</param>
        /// <param name="isSourceCurrent">Controllo breve dell'identità nel registro terminale, senza I/O, notifiche o lock di sessione</param>
        internal void EnqueueTerminalClipboard(TerminalClientEvent request, Func<bool> isSourceCurrent)
        {
            Action<BiviumWorkspaceSnapshot>[] subscribers;
            BiviumWorkspaceSnapshot snapshot;
            lock (this._lock)
            {
                // Reset rimuove la sorgente e svuota la coda sotto questo stesso lock workspace
                if (this.IsStopped || this._terminalClipboardRequests.Count >= 8 || isSourceCurrent == null || !isSourceCurrent())
                    return;
                WorkspaceTerminalContext context = new WorkspaceTerminalContext(ImmutableArray.Create(request.SessionId), ClipboardRequestId: request.Id);
                WorkspaceWorkflowInvocation invocation = new WorkspaceWorkflowInvocation("", "", -1, request.Title, "Copy", -1, FormContext: JsonSerializer.Serialize(context));
                this._terminalClipboardRequests.Enqueue(new WorkspaceWorkflowSnapshot(Guid.NewGuid(), 0, WorkspaceWorkflowKind.TerminalClipboard, invocation, WorkspaceWorkflowPhase.AwaitingInput, request.Text, Guid.NewGuid(), Guid.Empty, ""));
                this.ActivateNextTerminalClipboardLocked();
                snapshot = this.CommitWorkflowStateLocked();
                subscribers = this.GetSubscribers();
            }
            this.NotifySubscribers(subscribers, snapshot);
        }

        #endregion

        #region Metodi privati

        /// <summary>Le domande queued hanno già identità; non vengono ricreate alla hydration</summary>
        private void ActivateNextTerminalClipboardLocked()
        {
            if (this._terminalClipboardRequests.Count == 0 || this._uploadRuntime?.Snapshot.Visible == true || this._workflowRuntime.Current?.IsActive == true || this._operationRuntime?.Snapshot.IsRunning == true)
                return;
            this._workflowRuntime.Current = this._terminalClipboardRequests.Dequeue();
            this._workflowRuntime.Responses.Clear();
        }

        /// <summary>Lo stesso runner esegue solo il piano terminale già ammesso</summary>
        private FileOperationResult RunWorkspaceTerminal(WorkspaceOperationRuntime operation)
        {
            WorkspaceOperationPlan plan = operation.Plan;
            WorkspaceTerminalContext context = JsonSerializer.Deserialize<WorkspaceTerminalContext>(plan.FormContext);
            TerminalRuntimeService terminal = this._workflowServices.GetRequiredService<TerminalRuntimeService>();
            return terminal.ExecuteAdmittedWorkspaceAction(plan.Kind, context.SessionIds, plan.FormDraft);
        }

        #endregion
    }
}
