using Bivium.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Bivium.Services
{
    public sealed partial class BiviumWorkspaceService
    {
        #region Metodi pubblici

        /// <summary>Cattura entry e permessi una volta, mantenendo gli errori di lettura preesistenti</summary>
        /// <param name="token">Lease dell'invocazione</param>
        /// <param name="kind">Form entry richiesta</param>
        /// <param name="entry">Entry catturata dal comando</param>
        /// <param name="panelIndex">Pannello di origine</param>
        /// <returns>Workflow con proprietà e permessi materializzati</returns>
        internal WorkspaceWorkflowSnapshot BeginEntryFormWorkflow(WorkspaceClientToken token, WorkspaceWorkflowKind kind, FileSystemEntry entry, int panelIndex)
        {
            if (!this.ValidateMutation(token) || entry == null || !this._workflowSecurity.IsPathSafe(entry.FullPath))
                throw new UnauthorizedAccessException("This browser no longer controls the workspace");
            PermissionModel permissions = null;
            string error = "";
            try
            {
                permissions = this._workflowPermissions.GetPermissions(entry.FullPath);
            }
            catch (Exception ex)
            {
                error = "Could not read permissions: " + ex.Message;
            }
            string draft;
            string context = "";
            if (kind == WorkspaceWorkflowKind.Properties)
            {
                draft = JsonSerializer.Serialize(new WorkspacePropertiesDraft(entry, permissions));
            }
            else if (kind == WorkspaceWorkflowKind.Permissions)
            {
                context = JsonSerializer.Serialize(new WorkspacePermissionsContext(entry.Name, entry.IsDirectory, permissions?.Owner ?? "", permissions?.Group ?? "", permissions != null));
                draft = JsonSerializer.Serialize(new WorkspacePermissionsDraft(permissions ?? new PermissionModel(), false));
            }
            else
            {
                throw new ArgumentException("Invalid entry form");
            }
            return this.BeginFormWorkflow(token, kind, new WorkspaceWorkflowInvocation(entry.FullPath, Path.GetDirectoryName(entry.FullPath), panelIndex, kind.ToString(), error, -1, FormContext: context), draft);
        }

        /// <summary>Apre una form tipizzata sullo stesso runtime, senza nuove letture durante hydration</summary>
        /// <param name="token">Lease del comando</param>
        /// <param name="kind">Tipo chiuso della form</param>
        /// <param name="invocation">Contesto catturato</param>
        /// <param name="draft">Valori iniziali non sensibili</param>
        /// <returns>Workflow oppure estrazione già ammessa atomicamente</returns>
        internal WorkspaceWorkflowSnapshot BeginFormWorkflow(WorkspaceClientToken token, WorkspaceWorkflowKind kind, WorkspaceWorkflowInvocation invocation, string draft)
        {
            BiviumWorkspaceSnapshot snapshot;
            Action<BiviumWorkspaceSnapshot>[] subscribers;
            WorkspaceWorkflowSnapshot workflow;
            lock (this._lock)
            {
                this.RequireDesktopLeaseLocked(token);
                if (!IsFormKind(kind) || invocation == null)
                    throw new ArgumentException("Invalid form invocation");
                if (this._uploadRuntime?.Snapshot.Visible == true || this._workflowRuntime.Current?.IsActive == true || this._operationRuntime?.Snapshot.IsRunning == true)
                    throw new InvalidOperationException("A workspace workflow is already active");
                if (kind is WorkspaceWorkflowKind.Permissions or WorkspaceWorkflowKind.Properties && !this._workflowSecurity.IsPathSafe(invocation.SourcePath))
                    throw new ArgumentException("Invalid form path");
                if (kind is WorkspaceWorkflowKind.Compress or WorkspaceWorkflowKind.Extract)
                {
                    if (!this._workflowSecurity.IsPathSafe(invocation.ParentPath) || invocation.SourcePaths.IsDefaultOrEmpty)
                        throw new ArgumentException("Invalid archive invocation");
                    foreach (string path in invocation.SourcePaths)
                        if (!this._workflowSecurity.IsPathSafe(path))
                            throw new ArgumentException("Invalid archive path");
                }
                draft = NormalizeFormDraft(kind, draft);
                workflow = new WorkspaceWorkflowSnapshot(Guid.NewGuid(), 0, kind, invocation, WorkspaceWorkflowPhase.AwaitingInput, draft, Guid.NewGuid(), Guid.Empty, kind == WorkspaceWorkflowKind.Permissions ? invocation.Label : "");
                this._workflowRuntime.Current = workflow;
                this._workflowRuntime.Responses.Clear();
                WorkspaceOperationRuntime admitted = null;
                if (kind == WorkspaceWorkflowKind.Extract)
                {
                    admitted = this.AdmitWorkflowOperationLocked(workflow.Id, this.CreateFormPlan(workflow));
                    workflow = workflow with { Phase = WorkspaceWorkflowPhase.Running, OperationId = admitted.Snapshot.Id };
                    this._workflowRuntime.Current = workflow;
                }
                snapshot = this.CommitWorkflowStateLocked();
                subscribers = this.GetSubscribers();
                if (admitted != null)
                    admitted.Work = Task.Run(() => this.RunWorkspaceOperation(admitted));
            }
            this.NotifySubscribers(subscribers, snapshot);
            return workflow;
        }

        /// <summary>Riconosce soltanto le form effettivamente migrate</summary>
        internal static bool IsFormKind(WorkspaceWorkflowKind kind) => kind is WorkspaceWorkflowKind.EditorExtensions or WorkspaceWorkflowKind.CreationPermissions or WorkspaceWorkflowKind.Permissions or WorkspaceWorkflowKind.Compress or WorkspaceWorkflowKind.Extract or WorkspaceWorkflowKind.Properties or WorkspaceWorkflowKind.About or WorkspaceWorkflowKind.Authentication or WorkspaceWorkflowKind.TerminalRename or WorkspaceWorkflowKind.TerminalClose or WorkspaceWorkflowKind.TerminalClipboard;

        /// <summary>Form con draft modificabile e salvataggio esplicito anche dopo un errore, come nella UX preesistente</summary>
        internal static bool IsEditableFormKind(WorkspaceWorkflowKind kind) => kind is WorkspaceWorkflowKind.EditorExtensions or WorkspaceWorkflowKind.CreationPermissions or WorkspaceWorkflowKind.Permissions or WorkspaceWorkflowKind.Compress;

        #endregion

        #region Metodi privati

        /// <summary>Deserializza e riserializza i payload chiusi; auth scarta ogni campo fuori allowlist</summary>
        private static string NormalizeFormDraft(WorkspaceWorkflowKind kind, string draft)
        {
            return kind switch
            {
                WorkspaceWorkflowKind.EditorExtensions => draft ?? "",
                WorkspaceWorkflowKind.TerminalRename or WorkspaceWorkflowKind.TerminalClipboard => draft ?? "",
                WorkspaceWorkflowKind.TerminalClose => "",
                WorkspaceWorkflowKind.CreationPermissions => JsonSerializer.Serialize(JsonSerializer.Deserialize<DefaultCreationPermissionsSettings>(draft) ?? throw new ArgumentException("Invalid creation permissions")),
                WorkspaceWorkflowKind.Permissions => JsonSerializer.Serialize(JsonSerializer.Deserialize<WorkspacePermissionsDraft>(draft) ?? throw new ArgumentException("Invalid permissions")),
                WorkspaceWorkflowKind.Compress => JsonSerializer.Serialize(JsonSerializer.Deserialize<WorkspaceCompressDraft>(draft) ?? throw new ArgumentException("Invalid compression draft")),
                WorkspaceWorkflowKind.Properties => JsonSerializer.Serialize(JsonSerializer.Deserialize<WorkspacePropertiesDraft>(draft) ?? throw new ArgumentException("Invalid properties")),
                WorkspaceWorkflowKind.About => JsonSerializer.Serialize(JsonSerializer.Deserialize<WorkspaceAboutDraft>(draft) ?? throw new ArgumentException("Invalid about")),
                WorkspaceWorkflowKind.Authentication => JsonSerializer.Serialize(JsonSerializer.Deserialize<WorkspaceAuthenticationDraft>(draft) ?? throw new ArgumentException("Invalid authentication draft")),
                WorkspaceWorkflowKind.Extract => "",
                _ => throw new ArgumentException("Unsupported form")
            };
        }

        /// <summary>Deriva il piano dal draft acknowledged e dal contesto originale catturato</summary>
        private WorkspaceOperationPlan CreateFormPlan(WorkspaceWorkflowSnapshot workflow)
        {
            WorkspaceWorkflowInvocation invocation = workflow.InvocationParameters;
            if (workflow.Kind == WorkspaceWorkflowKind.Compress)
            {
                WorkspaceCompressDraft draft = JsonSerializer.Deserialize<WorkspaceCompressDraft>(workflow.Draft);
                string name = draft.OutputName?.Trim();
                if (!Enum.IsDefined(draft.Format) || string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    throw new ArgumentException("Archive name must be a file name, not a path.");
            }
            if (workflow.Kind == WorkspaceWorkflowKind.Permissions && !JsonSerializer.Deserialize<WorkspacePermissionsContext>(invocation.FormContext).CanSave)
                throw new ArgumentException("Cannot save permissions because current permissions were not loaded.");
            if (workflow.Kind == WorkspaceWorkflowKind.Properties)
            {
                WorkspacePropertiesDraft draft = JsonSerializer.Deserialize<WorkspacePropertiesDraft>(workflow.Draft);
                if (!draft.Entry.IsDirectory || draft.Size >= 0)
                    throw new ArgumentException("The directory size is already available or this entry is not a directory.");
            }
            if (workflow.Kind == WorkspaceWorkflowKind.CreationPermissions)
            {
                DefaultCreationPermissionsSettings settings = JsonSerializer.Deserialize<DefaultCreationPermissionsSettings>(workflow.Draft);
                if (settings.FilePermissions == null || settings.DirectoryPermissions == null)
                    throw new ArgumentException("Invalid default creation permissions");
            }
            if (workflow.Kind is WorkspaceWorkflowKind.TerminalRename or WorkspaceWorkflowKind.TerminalClose)
            {
                WorkspaceTerminalContext context = JsonSerializer.Deserialize<WorkspaceTerminalContext>(invocation.FormContext);
                if (context == null || context.SessionIds.IsDefault || context.SessionIds.Any(id => id <= 0) || (workflow.Kind == WorkspaceWorkflowKind.TerminalRename && context.SessionIds.Length != 1))
                    throw new ArgumentException("Invalid terminal invocation");
            }
            return new WorkspaceOperationPlan(workflow.Kind, invocation.SourcePath, invocation.ParentPath, "", invocation.SourcePaths, FormDraft: workflow.Draft, FormContext: invocation.FormContext, ExtractToOwnFolder: invocation.ExtractToOwnFolder);
        }

        #endregion
    }
}
