using Bivium.Models;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Bivium.Services
{
    public sealed partial class BiviumWorkspaceService
    {
        #region Costanti

        /// <summary>Risposta alla chiusura editor: salva e poi chiude</summary>
        internal const string EDITOR_CLOSE_SAVE = "save";

        /// <summary>Risposta alla chiusura editor: scarta le modifiche e chiude</summary>
        internal const string EDITOR_CLOSE_DISCARD = "discard";

        #endregion

        #region Variabili di classe

        /// <summary>Runtime form e domanda senza ownership di circuito</summary>
        private readonly WorkspaceWorkflowRuntime _workflowRuntime = new WorkspaceWorkflowRuntime();

        #endregion

        #region Metodi pubblici

        /// <summary>Legge il workflow solo per il lease autorevole, anche durante il drain</summary>
        /// <param name="token">Lease del lettore</param>
        /// <returns>Workflow immutabile oppure null</returns>
        internal WorkspaceWorkflowSnapshot GetWorkflow(WorkspaceClientToken token)
        {
            lock (this._lock)
                return !this.IsStopped && this.ValidateLeaseLocked(token) ? this._workflowRuntime.Current : null;
        }

        /// <summary>Apre una domanda Input con percorsi già catturati e validazione path esistente</summary>
        /// <param name="token">Lease che invoca il comando</param>
        /// <param name="kind">Comando chiuso</param>
        /// <param name="parameters">Argomenti catturati dall'invocazione</param>
        /// <param name="draft">Valore iniziale</param>
        /// <returns>Workflow ammesso</returns>
        internal WorkspaceWorkflowSnapshot BeginInputWorkflow(WorkspaceClientToken token, WorkspaceWorkflowKind kind, WorkspaceWorkflowInvocation parameters, string draft)
        {
            if (parameters == null || kind is not (WorkspaceWorkflowKind.CreateFile or WorkspaceWorkflowKind.CreateDirectory or WorkspaceWorkflowKind.RenameEntry) || !this._workflowSecurity.IsPathSafe(parameters.ParentPath) || (kind == WorkspaceWorkflowKind.RenameEntry && !this._workflowSecurity.IsPathSafe(parameters.SourcePath)))
                throw new ArgumentException("Invalid workflow invocation");
            BiviumWorkspaceSnapshot snapshot;
            Action<BiviumWorkspaceSnapshot>[] subscribers;
            WorkspaceWorkflowSnapshot workflow;
            lock (this._lock)
            {
                this.ThrowIfStopped();
                if (!this.ValidateMutationLocked(token))
                    throw new UnauthorizedAccessException("This browser no longer controls the workspace");
                if (this._uploadRuntime?.Snapshot.Visible == true || this._workflowRuntime.Current?.IsActive == true || this._operationRuntime?.Snapshot.IsRunning == true)
                    throw new InvalidOperationException("A workspace workflow is already active");
                workflow = new WorkspaceWorkflowSnapshot(Guid.NewGuid(), 0, kind, parameters, WorkspaceWorkflowPhase.AwaitingInput, draft ?? "", Guid.NewGuid(), Guid.Empty, "");
                this._workflowRuntime.Current = workflow;
                this._workflowRuntime.Responses.Clear();
                snapshot = this.CommitWorkflowStateLocked();
                subscribers = this.GetSubscribers();
            }
            this.NotifySubscribers(subscribers, snapshot);
            return workflow;
        }

        /// <summary>Apre la conferma delete sulla selezione immutabile, senza eseguire filesystem</summary>
        /// <param name="token">Lease che invoca il comando</param>
        /// <param name="parameters">Selezione e presentazione catturate</param>
        /// <returns>Domanda server-owned ammessa</returns>
        internal WorkspaceWorkflowSnapshot BeginDeleteWorkflow(WorkspaceClientToken token, WorkspaceWorkflowInvocation parameters)
        {
            BiviumWorkspaceSnapshot snapshot;
            Action<BiviumWorkspaceSnapshot>[] subscribers;
            WorkspaceWorkflowSnapshot workflow;
            lock (this._lock)
            {
                this.ThrowIfStopped();
                if (!this.ValidateMutationLocked(token))
                    throw new UnauthorizedAccessException("This browser no longer controls the workspace");
                if (this._uploadRuntime?.Snapshot.Visible == true || this._workflowRuntime.Current?.IsActive == true || this._operationRuntime?.Snapshot.IsRunning == true)
                    throw new InvalidOperationException("A workspace workflow is already active");
                if (parameters == null || parameters.SourcePaths.IsDefaultOrEmpty)
                    throw new ArgumentException("Invalid workflow invocation");
                foreach (string path in parameters.SourcePaths)
                    if (!this._workflowSecurity.IsPathSafe(path))
                        throw new ArgumentException("Invalid workflow invocation");
                workflow = new WorkspaceWorkflowSnapshot(Guid.NewGuid(), 0, WorkspaceWorkflowKind.DeleteEntries, parameters, WorkspaceWorkflowPhase.AwaitingConfirmation, "", Guid.NewGuid(), Guid.Empty, "");
                this._workflowRuntime.Current = workflow;
                this._workflowRuntime.Responses.Clear();
                snapshot = this.CommitWorkflowStateLocked();
                subscribers = this.GetSubscribers();
            }
            this.NotifySubscribers(subscribers, snapshot);
            return workflow;
        }

        /// <summary>Cattura alert editor, chiusura editor con modifiche o intento reset, senza letture o effetti alla hydration</summary>
        /// <param name="token">Lease che apre la domanda</param>
        /// <param name="kind">Soltanto alert editor, chiusura editor o reset workspace</param>
        /// <param name="title">Titolo immutabile preesistente</param>
        /// <param name="message">Testo immutabile già materializzato</param>
        /// <returns>Domanda trasferibile con identità stabile</returns>
        internal WorkspaceWorkflowSnapshot BeginConfirmationWorkflow(WorkspaceClientToken token, WorkspaceWorkflowKind kind, string title, string message)
        {
            if (kind is not (WorkspaceWorkflowKind.EditorAlert or WorkspaceWorkflowKind.ResetWorkspace or WorkspaceWorkflowKind.EditorClose))
                throw new ArgumentException("Unsupported confirmation workflow");
            BiviumWorkspaceSnapshot snapshot;
            Action<BiviumWorkspaceSnapshot>[] subscribers;
            WorkspaceWorkflowSnapshot workflow;
            lock (this._lock)
            {
                this.RequireDesktopLeaseLocked(token);
                if (this._uploadRuntime?.Snapshot.Visible == true || this._workflowRuntime.Current?.IsActive == true || this._operationRuntime?.Snapshot.IsRunning == true)
                    throw new InvalidOperationException("A workspace workflow is already active");
                WorkspaceWorkflowInvocation invocation = new WorkspaceWorkflowInvocation("", "", -1, title, message, -1);
                workflow = new WorkspaceWorkflowSnapshot(Guid.NewGuid(), 0, kind, invocation, WorkspaceWorkflowPhase.AwaitingConfirmation, "", Guid.NewGuid(), Guid.Empty, "");
                this._workflowRuntime.Current = workflow;
                this._workflowRuntime.Responses.Clear();
                snapshot = this.CommitWorkflowStateLocked();
                subscribers = this.GetSubscribers();
            }
            this.NotifySubscribers(subscribers, snapshot);
            return workflow;
        }

        /// <summary>Pubblica il solo draft; non risponde alla domanda né esegue comandi</summary>
        /// <param name="token">Lease del publisher</param>
        /// <param name="id">Workflow montato</param>
        /// <param name="expectedRevision">Revisione del draft precedente</param>
        /// <param name="draft">Valore corrente</param>
        /// <param name="workflow">Workflow dopo il tentativo</param>
        /// <returns>True solo per il checkpoint acknowledged</returns>
        internal bool TryUpdateWorkflowDraft(WorkspaceClientToken token, Guid id, long expectedRevision, string draft, out WorkspaceWorkflowSnapshot workflow)
        {
            BiviumWorkspaceSnapshot snapshot;
            Action<BiviumWorkspaceSnapshot>[] subscribers = Array.Empty<Action<BiviumWorkspaceSnapshot>>();
            lock (this._lock)
            {
                workflow = this._workflowRuntime.Current;
                if (this.IsStopped || !this.ValidateLeaseLocked(token) || workflow == null || workflow.Id != id || workflow.Revision != expectedRevision || (workflow.Phase != WorkspaceWorkflowPhase.AwaitingInput && !(workflow.Phase == WorkspaceWorkflowPhase.Failed && IsEditableFormKind(workflow.Kind))))
                    return false;
                if (IsFormKind(workflow.Kind))
                {
                    if (workflow.Kind is WorkspaceWorkflowKind.Properties or WorkspaceWorkflowKind.About or WorkspaceWorkflowKind.Extract or WorkspaceWorkflowKind.TerminalClose or WorkspaceWorkflowKind.TerminalClipboard)
                        return workflow.Draft == draft;
                    draft = NormalizeFormDraft(workflow.Kind, draft);
                }
                if (workflow.Draft != (draft ?? ""))
                {
                    // L'errore inline di validazione nome descrive il valore rifiutato, non quello modificato
                    workflow = workflow with { Revision = workflow.Revision + 1, Draft = draft ?? "", ErrorMessage = IsFormKind(workflow.Kind) ? workflow.ErrorMessage : "" };
                    this._workflowRuntime.Current = workflow;
                    this.CommitWorkflowStateLocked();
                    subscribers = this.GetSubscribers();
                }
                snapshot = this._snapshot;
            }
            this.NotifySubscribers(subscribers, snapshot);
            return true;
        }

        /// <summary>Consuma la risposta e ammette un piano chiuso atomicamente con il lease</summary>
        /// <param name="token">Lease che risponde</param>
        /// <param name="response">Risposta tipizzata con identità consume-once</param>
        /// <returns>Esito; una risposta duplicata non avvia nuovamente il task</returns>
        internal WorkspaceWorkflowResponseResult RespondToWorkflow(WorkspaceClientToken token, WorkspaceWorkflowResponse response)
        {
            BiviumWorkspaceSnapshot snapshot;
            Action<BiviumWorkspaceSnapshot>[] subscribers;
            WorkspaceOperationRuntime admitted = null;
            Action completeReset = null;
            WorkspaceUploadRuntime resetUpload = null;
            TerminalRuntimeService resetTerminal = null;
            // Risoluzione fuori lock: il singleton terminale dipende dal workspace
            if (response?.Cancelled == false)
            {
                WorkspaceWorkflowSnapshot observed = this.GetWorkflow(token);
                if (observed?.Id == response.WorkflowId && observed.Kind == WorkspaceWorkflowKind.ResetWorkspace)
                    resetTerminal = this._workflowServices.GetRequiredService<TerminalRuntimeService>();
            }
            lock (this._lock)
            {
                if (this.IsStopped || !this.ValidateMutationLocked(token))
                    return WorkspaceWorkflowResponseResult.Denied;
                if (response == null || response.ResponseId == Guid.Empty)
                    return WorkspaceWorkflowResponseResult.Stale;
                if (this._workflowRuntime.Responses.TryGetValue(response.ResponseId, out WorkspaceWorkflowResponse consumed))
                    return consumed == response ? WorkspaceWorkflowResponseResult.Duplicate : WorkspaceWorkflowResponseResult.Denied;
                WorkspaceWorkflowSnapshot workflow = this._workflowRuntime.Current;
                if (workflow == null || workflow.Id != response.WorkflowId || workflow.Revision != response.Revision || workflow.QuestionId != response.QuestionId || (workflow.Phase != WorkspaceWorkflowPhase.AwaitingInput && workflow.Phase != WorkspaceWorkflowPhase.AwaitingConfirmation && workflow.Phase != WorkspaceWorkflowPhase.AwaitingOverwrite && workflow.Phase != WorkspaceWorkflowPhase.Failed && !(workflow.IsActive && workflow.Phase == WorkspaceWorkflowPhase.Cancelled) && !(workflow.Kind == WorkspaceWorkflowKind.Properties && workflow.Phase == WorkspaceWorkflowPhase.Running && response.Cancelled)))
                    return WorkspaceWorkflowResponseResult.Stale;
                if (workflow.Phase != WorkspaceWorkflowPhase.AwaitingOverwrite && response.Overwrite != null)
                    return WorkspaceWorkflowResponseResult.Stale;
                if (workflow.Kind == WorkspaceWorkflowKind.Properties && workflow.Phase == WorkspaceWorkflowPhase.Running && response.Cancelled)
                {
                    // Chiudere la form non cancella il calcolo già ammesso
                    this._workflowRuntime.Current = workflow with { Revision = workflow.Revision + 1, Phase = WorkspaceWorkflowPhase.Dismissed };
                }
                else if ((workflow.Phase == WorkspaceWorkflowPhase.Failed && (!IsEditableFormKind(workflow.Kind) || response.Cancelled)) || workflow.Phase == WorkspaceWorkflowPhase.Cancelled)
                {
                    if (!response.Cancelled)
                        return WorkspaceWorkflowResponseResult.Stale;
                    this._workflowRuntime.Current = workflow with { Revision = workflow.Revision + 1, Phase = WorkspaceWorkflowPhase.Dismissed };
                }
                else if (workflow.Phase == WorkspaceWorkflowPhase.AwaitingOverwrite)
                {
                    if (response.Cancelled || response.Overwrite == null || !Enum.IsDefined(response.Overwrite.Value) || !string.IsNullOrEmpty(response.Value))
                        return WorkspaceWorkflowResponseResult.Stale;
                    WorkspaceOverwriteQuestion question = workflow.Conflicts[workflow.ConflictIndex];
                    ImmutableArray<string> overwrite = workflow.OverwritePaths;
                    ImmutableArray<string> skipped = workflow.SkippedPaths;
                    int next = workflow.ConflictIndex + 1;
                    if (response.Overwrite == OverwriteChoice.No)
                        skipped = skipped.Add(question.SourcePath);
                    else if (response.Overwrite == OverwriteChoice.Yes)
                        overwrite = overwrite.Add(question.SourcePath);
                    else
                    {
                        for (int i = workflow.ConflictIndex; i < workflow.Conflicts.Length; i++)
                            overwrite = overwrite.Add(workflow.Conflicts[i].SourcePath);
                        next = workflow.Conflicts.Length;
                    }
                    workflow = workflow with { Revision = workflow.Revision + 1, ConflictIndex = next, OverwritePaths = overwrite, SkippedPaths = skipped, QuestionId = Guid.NewGuid() };
                    this._workflowRuntime.Current = workflow;
                    if (next == workflow.Conflicts.Length)
                    {
                        admitted = this.AdmitWorkflowOperationLocked(workflow.Id, this.CreateTransferPlan(workflow));
                        this._workflowRuntime.Current = workflow with { Phase = WorkspaceWorkflowPhase.Running, OperationId = admitted.Snapshot.Id };
                    }
                }
                else if (workflow.Phase == WorkspaceWorkflowPhase.AwaitingConfirmation)
                {
                    if (workflow.Kind is not (WorkspaceWorkflowKind.DeleteEntries or WorkspaceWorkflowKind.BatchRename or WorkspaceWorkflowKind.EditorAlert or WorkspaceWorkflowKind.ResetWorkspace or WorkspaceWorkflowKind.EditorClose))
                        return WorkspaceWorkflowResponseResult.Stale;
                    // Soltanto la chiusura editor porta una scelta tipizzata: save oppure discard
                    if (workflow.Kind == WorkspaceWorkflowKind.EditorClose ? !response.Cancelled && response.Value is not (EDITOR_CLOSE_SAVE or EDITOR_CLOSE_DISCARD) : !string.IsNullOrEmpty(response.Value))
                        return WorkspaceWorkflowResponseResult.Stale;
                    if (workflow.Kind is WorkspaceWorkflowKind.EditorAlert or WorkspaceWorkflowKind.EditorClose)
                    {
                        // Consume-once: save o discard vengono eseguiti dall'adapter che ha risposto, mai dal browser successivo
                        this._workflowRuntime.Current = workflow with { Revision = workflow.Revision + 1, Phase = WorkspaceWorkflowPhase.Dismissed };
                    }
                    else if (response.Cancelled)
                    {
                        this._workflowRuntime.Current = workflow with { Revision = workflow.Revision + 1, Phase = workflow.Kind == WorkspaceWorkflowKind.BatchRename ? WorkspaceWorkflowPhase.Dismissed : WorkspaceWorkflowPhase.Cancelled };
                    }
                    else if (workflow.Kind == WorkspaceWorkflowKind.ResetWorkspace)
                    {
                        if (this._operationRuntime?.Snapshot.IsRunning == true || resetTerminal == null)
                            return WorkspaceWorkflowResponseResult.Stale;
                        // Consume-once, registro PTY e desktop si linearizzano con il lease corrente.
                        // Nessuna attesa, disposal o callback viene eseguita sotto il lock workspace.
                        completeReset = resetTerminal.DetachAllSessionsForWorkspaceReset();
                        this.ResetWorkspaceLocked(out resetUpload);
                    }
                    else
                    {
                        WorkspaceWorkflowInvocation invocation = workflow.InvocationParameters;
                        WorkspaceOperationPlan plan = new WorkspaceOperationPlan(workflow.Kind, "", "", "", invocation.SourcePaths, RenamerSessionId: invocation.RenamerSessionId, RenameItems: invocation.RenameItems);
                        admitted = this.AdmitWorkflowOperationLocked(workflow.Id, plan);
                        this._workflowRuntime.Current = workflow with { Revision = workflow.Revision + 1, Phase = WorkspaceWorkflowPhase.Running, OperationId = admitted.Snapshot.Id };
                    }
                }
                else if (IsFormKind(workflow.Kind))
                {
                    if (response.Value != workflow.Draft)
                        return WorkspaceWorkflowResponseResult.Stale;
                    if (response.Cancelled || workflow.Kind == WorkspaceWorkflowKind.About || (workflow.Kind == WorkspaceWorkflowKind.TerminalRename && string.IsNullOrWhiteSpace(workflow.Draft)))
                    {
                        this._workflowRuntime.Current = workflow with { Revision = workflow.Revision + 1, Phase = WorkspaceWorkflowPhase.Dismissed };
                    }
                    else if (workflow.Kind == WorkspaceWorkflowKind.Authentication)
                    {
                        // Le mutazioni auth restano sugli endpoint con l'autorità preesistente
                        return WorkspaceWorkflowResponseResult.Stale;
                    }
                    else if (workflow.Kind == WorkspaceWorkflowKind.TerminalClipboard)
                    {
                        // Consume-once prima del gesto OS; nessuna esecuzione al browser successivo
                        this._workflowRuntime.Current = workflow with { Revision = workflow.Revision + 1, Phase = WorkspaceWorkflowPhase.Succeeded };
                    }
                    else
                    {
                        try
                        {
                            admitted = this.AdmitWorkflowOperationLocked(workflow.Id, this.CreateFormPlan(workflow));
                            this._workflowRuntime.Current = workflow with { Revision = workflow.Revision + 1, Phase = WorkspaceWorkflowPhase.Running, OperationId = admitted.Snapshot.Id, QuestionId = workflow.Kind == WorkspaceWorkflowKind.Properties ? Guid.NewGuid() : workflow.QuestionId, ErrorMessage = "" };
                        }
                        catch (ArgumentException ex)
                        {
                            this._workflowRuntime.Current = workflow with { Revision = workflow.Revision + 1, Phase = WorkspaceWorkflowPhase.Failed, QuestionId = Guid.NewGuid(), ErrorMessage = ex.Message };
                        }
                    }
                }
                else if (!response.Cancelled && !string.IsNullOrEmpty(response.Value))
                {
                    // Stesse verifiche dei servizi preesistenti; i permessi OS restano verificati dall'I/O reale
                    FileOperationResult validation = this._workflowFiles.ValidateNameOperation(workflow.Kind, workflow.InvocationParameters.SourcePath, workflow.InvocationParameters.ParentPath, response.Value);
                    if (!validation.Success)
                    {
                        // Il nome non valido resta sulla stessa domanda: il campo conserva testo e focus e mostra l'errore inline
                        this._workflowRuntime.Current = workflow with { Revision = workflow.Revision + 1, Draft = response.Value, ErrorMessage = validation.ErrorMessage };
                    }
                    else
                    {
                        WorkspaceOperationPlan plan = new WorkspaceOperationPlan(workflow.Kind, workflow.InvocationParameters.SourcePath, workflow.InvocationParameters.ParentPath, response.Value);
                        admitted = this.AdmitWorkflowOperationLocked(workflow.Id, plan);
                        this._workflowRuntime.Current = workflow with { Revision = workflow.Revision + 1, Phase = WorkspaceWorkflowPhase.Running, Draft = response.Value, OperationId = admitted.Snapshot.Id, ErrorMessage = "" };
                    }
                }
                else
                {
                    // L'errore inline apparteneva alla domanda annullata: non deve sopravvivere nella status bar
                    this._workflowRuntime.Current = workflow with { Revision = workflow.Revision + 1, Phase = WorkspaceWorkflowPhase.Cancelled, ErrorMessage = "" };
                }
                this._workflowRuntime.Responses.Add(response.ResponseId, response);
                this.ActivateNextTerminalClipboardLocked();
                snapshot = this.CommitWorkflowStateLocked();
                subscribers = this.GetSubscribers();
                // Il task può iniziare, ma non può pubblicare prima che questo commit rilasci il lock
                if (admitted != null)
                    admitted.Work = Task.Run(() => this.RunWorkspaceOperation(admitted));
            }
            this.NotifySubscribers(subscribers, snapshot);
            try { completeReset?.Invoke(); }
            finally
            {
                if (resetUpload != null)
                    this.CleanupUploadAsync(resetUpload).GetAwaiter().GetResult();
            }
            return WorkspaceWorkflowResponseResult.Accepted;
        }

        #endregion

        #region Metodi privati

        /// <summary>Cattura la clipboard autorevole e invoca il percorso transfer esistente</summary>
        /// <param name="token">Lease dell'invocazione</param>
        /// <param name="destinationDir">Directory catturata</param>
        /// <param name="panelIndex">Pannello destinatario</param>
        /// <returns>Workflow oppure operazione già ammessa senza conflitti</returns>
        internal WorkspaceWorkflowSnapshot BeginPasteWorkflow(WorkspaceClientToken token, string destinationDir, int panelIndex)
        {
            BiviumWorkspaceSnapshot snapshot = this.GetSnapshot();
            List<FileTransferEntry> entries = new List<FileTransferEntry>();
            foreach (string path in snapshot.Desktop.ClipboardPaths)
                entries.Add(new FileTransferEntry(path, snapshot.Desktop.ClipboardIsCut ? FileTransferMode.Move : FileTransferMode.Copy));
            return this.BeginTransferWorkflow(token, entries, destinationDir, panelIndex, true);
        }

        /// <summary>Cattura sorgenti, modalità e conflitti una volta; hydration non invoca questo metodo</summary>
        /// <param name="token">Lease dell'invocazione</param>
        /// <param name="entries">Entry server-side, copiate in record immutabili</param>
        /// <param name="destinationDir">Destinazione catturata</param>
        /// <param name="panelIndex">Pannello destinatario</param>
        /// <param name="fromClipboard">True solo per paste con clipboard ancora corrispondente</param>
        /// <returns>Workflow con decisioni server-owned</returns>
        internal WorkspaceWorkflowSnapshot BeginTransferWorkflow(WorkspaceClientToken token, IReadOnlyList<FileTransferEntry> entries, string destinationDir, int panelIndex, bool fromClipboard = false)
        {
            BiviumWorkspaceSnapshot snapshot;
            Action<BiviumWorkspaceSnapshot>[] subscribers;
            WorkspaceWorkflowSnapshot workflow;
            lock (this._lock)
            {
                this.RequireDesktopLeaseLocked(token);
                if (this._uploadRuntime?.Snapshot.Visible == true || this._workflowRuntime.Current?.IsActive == true || this._operationRuntime?.Snapshot.IsRunning == true)
                    throw new InvalidOperationException("A workspace workflow is already active");
                if (entries == null || entries.Count == 0 || !this._workflowSecurity.IsPathSafe(destinationDir))
                    throw new ArgumentException("Invalid transfer invocation");
                ImmutableArray<WorkspaceTransferEntry>.Builder captured = ImmutableArray.CreateBuilder<WorkspaceTransferEntry>();
                ImmutableArray<WorkspaceOverwriteQuestion>.Builder conflicts = ImmutableArray.CreateBuilder<WorkspaceOverwriteQuestion>();
                foreach (FileTransferEntry entry in entries)
                {
                    if (entry == null || !this._workflowSecurity.IsPathSafe(entry.SourcePath))
                        throw new ArgumentException("Invalid transfer invocation");
                    FileTransferMode mode = entry.Mode ?? this._workflowFiles.ResolveDefaultTransferMode(entry.SourcePath, destinationDir);
                    if (!Enum.IsDefined(mode))
                        throw new ArgumentException("Invalid transfer mode");
                    captured.Add(new WorkspaceTransferEntry(entry.SourcePath, mode));
                    bool directory = Directory.Exists(entry.SourcePath);
                    bool file = File.Exists(entry.SourcePath);
                    string target = Path.Combine(destinationDir, Path.GetFileName(entry.SourcePath));
                    if ((directory || file) && !this.SameWorkflowPath(entry.SourcePath, target) && (directory ? Directory.Exists(target) : File.Exists(target)))
                    {
                        string type = directory ? "directory" : "file";
                        string message = "A " + type + " named '" + Path.GetFileName(entry.SourcePath) + "' already exists in the destination.\n\nSource: " + entry.SourcePath + "\nDestination: " + target + "\n\n" + (directory ? "Merge it and overwrite conflicting contents?" : "Overwrite it?");
                        conflicts.Add(new WorkspaceOverwriteQuestion(entry.SourcePath, target, directory, "Overwrite " + type, message));
                    }
                }
                ImmutableArray<WorkspaceTransferEntry> transfers = captured.ToImmutable();
                ImmutableArray<string> sources = transfers.Select(entry => entry.SourcePath).ToImmutableArray();
                WorkspaceWorkflowKind kind = transfers.All(entry => entry.Mode == FileTransferMode.Copy) ? WorkspaceWorkflowKind.CopyEntries : transfers.All(entry => entry.Mode == FileTransferMode.Move) ? WorkspaceWorkflowKind.MoveEntries : WorkspaceWorkflowKind.TransferEntries;
                if (fromClipboard && (!sources.SequenceEqual(this._snapshot.Desktop.ClipboardPaths) || this._snapshot.Desktop.ClipboardIsCut != (kind == WorkspaceWorkflowKind.MoveEntries)))
                    throw new InvalidOperationException("The clipboard changed before paste was admitted");
                WorkspaceWorkflowInvocation invocation = new WorkspaceWorkflowInvocation("", destinationDir, panelIndex, kind.ToString(), "", -1, sources, transfers, fromClipboard);
                workflow = new WorkspaceWorkflowSnapshot(Guid.NewGuid(), 0, kind, invocation, conflicts.Count > 0 ? WorkspaceWorkflowPhase.AwaitingOverwrite : WorkspaceWorkflowPhase.Running, "", Guid.NewGuid(), Guid.Empty, "", conflicts.ToImmutable(), 0, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty);
                this._workflowRuntime.Responses.Clear();
                this._workflowRuntime.Current = workflow;
                WorkspaceOperationRuntime admitted = null;
                if (conflicts.Count == 0)
                {
                    admitted = this.AdmitWorkflowOperationLocked(workflow.Id, this.CreateTransferPlan(workflow));
                    workflow = workflow with { OperationId = admitted.Snapshot.Id };
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

        /// <summary>Ammette la preview acknowledged senza rigenerare Rand o i metodi</summary>
        /// <param name="token">Lease dell'invocazione</param>
        /// <param name="sessionId">Sessione renamer montata</param>
        /// <param name="expectedRevision">Revisione della preview visualizzata</param>
        /// <returns>Domanda pronta per la risposta esplicita del pulsante Rename</returns>
        internal WorkspaceWorkflowSnapshot BeginBatchRenameWorkflow(WorkspaceClientToken token, Guid sessionId, long expectedRevision)
        {
            BiviumWorkspaceSnapshot snapshot;
            Action<BiviumWorkspaceSnapshot>[] subscribers;
            WorkspaceWorkflowSnapshot workflow;
            lock (this._lock)
            {
                this.RequireDesktopLeaseLocked(token);
                if (this._uploadRuntime?.Snapshot.Visible == true || this._workflowRuntime.Current?.IsActive == true || this._operationRuntime?.Snapshot.IsRunning == true)
                    throw new InvalidOperationException("A workspace workflow is already active");
                RenamerSessionSnapshot session = this._desktopRuntime.Renamer;
                if (session == null || session.Id != sessionId || session.Revision != expectedRevision)
                    throw new InvalidOperationException("The rename preview changed before admission");
                RenamerDraft draft = JsonSerializer.Deserialize<RenamerDraft>(session.Draft);
                if (draft.Methods.Count == 0 || draft.Preview.Any(item => item.HasConflict || item.HasError))
                    throw new InvalidOperationException("The rename preview is not executable");
                string suffix = ".bivium_rename_temp_" + Guid.NewGuid().ToString("N");
                ImmutableArray<WorkspaceRenameItem>.Builder items = ImmutableArray.CreateBuilder<WorkspaceRenameItem>();
                foreach (RenamePreviewItem item in draft.Preview)
                {
                    if (item.OriginalName == item.NewName)
                        continue;
                    FileOperationResult validation = this._workflowFiles.ValidateNameOperation(WorkspaceWorkflowKind.RenameEntry, item.OriginalFullPath, "", item.NewName);
                    if (!validation.Success)
                        throw new InvalidOperationException(validation.ErrorMessage);
                    items.Add(new WorkspaceRenameItem(item.OriginalFullPath, item.OriginalName, item.NewName, Path.Combine(Path.GetDirectoryName(item.OriginalFullPath), item.NewName + suffix)));
                }
                if (items.Count == 0)
                    throw new InvalidOperationException("The rename preview has no changed names");
                WorkspaceWorkflowInvocation invocation = new WorkspaceWorkflowInvocation("", "", -1, "Advanced Rename", "", -1, RenamerSessionId: sessionId, RenameItems: items.ToImmutable());
                workflow = new WorkspaceWorkflowSnapshot(Guid.NewGuid(), 0, WorkspaceWorkflowKind.BatchRename, invocation, WorkspaceWorkflowPhase.AwaitingConfirmation, session.Draft, Guid.NewGuid(), Guid.Empty, "");
                this._workflowRuntime.Current = workflow;
                this._workflowRuntime.Responses.Clear();
                snapshot = this.CommitWorkflowStateLocked();
                subscribers = this.GetSubscribers();
            }
            this.NotifySubscribers(subscribers, snapshot);
            return workflow;
        }

        /// <summary>Deriva il piano soltanto dalle decisioni già consumate</summary>
        /// <param name="workflow">Workflow autorevole</param>
        /// <returns>Piano senza entry saltate e con sole sovrascritture approvate</returns>
        private WorkspaceOperationPlan CreateTransferPlan(WorkspaceWorkflowSnapshot workflow)
        {
            WorkspaceWorkflowInvocation invocation = workflow.InvocationParameters;
            ImmutableArray<WorkspaceTransferEntry> transfers = invocation.Transfers.Where(entry => !workflow.SkippedPaths.Any(path => this.SameWorkflowPath(path, entry.SourcePath))).ToImmutableArray();
            return new WorkspaceOperationPlan(workflow.Kind, "", invocation.ParentPath, "", invocation.SourcePaths, transfers, workflow.OverwritePaths, invocation.FromClipboard);
        }

        /// <summary>Stesse regole di confronto filesystem usate dal Commander preesistente</summary>
        /// <param name="left">Primo percorso</param>
        /// <param name="right">Secondo percorso</param>
        /// <returns>True se i percorsi risolvono alla stessa entry</returns>
        private bool SameWorkflowPath(string left, string right)
        {
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }

        /// <summary>Pubblica soltanto riferimenti runtime e progresso leggero</summary>
        /// <returns>Snapshot committato sotto il lock workspace</returns>
        private BiviumWorkspaceSnapshot CommitWorkflowStateLocked()
        {
            this._snapshot = new BiviumWorkspaceSnapshot(this._snapshot.Revision + 1, this._snapshot.Panels, this._snapshot.FloatingWindows, this._snapshot.ActiveClientLease, this._snapshot.Desktop, this._snapshot.Handoff, this._workflowRuntime.Reference, this._operationRuntime?.Snapshot, this._snapshot.Upload);
            return this._snapshot;
        }

        #endregion
    }
}
