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

        /// <summary>Bounded server-owned queue of OSC requests, independent of the circuit</summary>
        private readonly Queue<WorkspaceWorkflowSnapshot> _terminalClipboardRequests = new Queue<WorkspaceWorkflowSnapshot>();

        #endregion

        #region Metodi pubblici

        /// <summary>Publication after the terminal lock is released; does not access the OS clipboard</summary>
        /// <param name="request">OSC request already captured by the session</param>
        /// <param name="isSourceCurrent">Short identity check in the terminal registry, with no I/O, notifications or session lock</param>
        internal void EnqueueTerminalClipboard(TerminalClientEvent request, Func<bool> isSourceCurrent)
        {
            Action<BiviumWorkspaceSnapshot>[] subscribers;
            BiviumWorkspaceSnapshot snapshot;
            lock (this._lock)
            {
                // Reset removes the source and empties the queue under this same workspace lock
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

        /// <summary>Queued questions already have an identity; they are not recreated on hydration</summary>
        private void ActivateNextTerminalClipboardLocked()
        {
            if (this._terminalClipboardRequests.Count == 0 || this._uploadRuntime?.Snapshot.Visible == true || this._workflowRuntime.Current?.IsActive == true || this._operationRuntime?.Snapshot.IsRunning == true)
                return;
            this._workflowRuntime.Current = this._terminalClipboardRequests.Dequeue();
            this._workflowRuntime.Responses.Clear();
        }

        /// <summary>The same runner executes only the already admitted terminal plan</summary>
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
