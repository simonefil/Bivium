using System;
using System.Collections.Immutable;

namespace Bivium.Models
{
    /// <summary>Workflow effettivamente migrati; nessun comando arbitrario o delegate serializzato</summary>
    public enum WorkspaceWorkflowKind { CreateFile, CreateDirectory, RenameEntry, DeleteEntries, CopyEntries, MoveEntries, TransferEntries, BatchRename, EditorExtensions, CreationPermissions, Permissions, Compress, Extract, Properties, About, Authentication, TerminalRename, TerminalClose, TerminalClipboard, EditorAlert, ResetWorkspace, EditorClose }

    /// <summary>Fase autorevole, mai avanzata dalla sola hydration</summary>
    public enum WorkspaceWorkflowPhase { AwaitingInput, Running, Succeeded, Failed, Cancelled, Dismissed, AwaitingConfirmation, AwaitingOverwrite }

    /// <summary>Argomenti catturati all'invocazione, indipendenti dai pannelli del browser successivo</summary>
    /// <param name="SourcePath">Sorgente rename oppure vuoto per creazione</param>
    /// <param name="ParentPath">Directory destinataria catturata</param>
    /// <param name="PanelIndex">Pannello di origine per la sola riconciliazione UI</param>
    /// <param name="Title">Titolo già usato dal workflow locale</param>
    /// <param name="Label">Etichetta del campo</param>
    /// <param name="SelectionLength">Selezione iniziale del campo</param>
    /// <param name="SourcePaths">Selezione immutabile catturata per delete, assente per Input</param>
    /// <param name="Transfers">Sorgenti e modalità transfer catturate</param>
    /// <param name="FromClipboard">Paste della clipboard interna, non drag/drop</param>
    /// <param name="RenamerSessionId">Sessione proprietaria della preview batch</param>
    /// <param name="RenameItems">Preview esatta con temporanei assegnati una volta</param>
    /// <param name="FormContext">Contesto serializzato chiuso catturato all'apertura, mai riferimenti mutabili</param>
    /// <param name="ExtractToOwnFolder">Modalità estrazione catturata dal comando esistente</param>
    public sealed record WorkspaceWorkflowInvocation(string SourcePath, string ParentPath, int PanelIndex, string Title, string Label, int SelectionLength, ImmutableArray<string> SourcePaths = default, ImmutableArray<WorkspaceTransferEntry> Transfers = default, bool FromClipboard = false, Guid RenamerSessionId = default, ImmutableArray<WorkspaceRenameItem> RenameItems = default, string FormContext = "", bool ExtractToOwnFolder = false);

    /// <summary>Runtime immutabile letto dall'adapter autorizzato, non trasmesso nelle notifiche globali</summary>
    /// <param name="Id">Identità stabile del workflow</param>
    /// <param name="Revision">CAS del draft e delle risposte</param>
    /// <param name="Kind">Comando tipizzato</param>
    /// <param name="InvocationParameters">Parametri catturati</param>
    /// <param name="Phase">Fase corrente</param>
    /// <param name="Draft">Testo corrente della form Input</param>
    /// <param name="QuestionId">Identità della domanda, non rigenerata al cambio browser</param>
    /// <param name="OperationId">Operazione ammessa, oppure vuoto</param>
    /// <param name="ErrorMessage">Errore del workflow</param>
    /// <param name="Conflicts">Domande di sovrascrittura materializzate</param>
    /// <param name="ConflictIndex">Domanda corrente nella sequenza</param>
    /// <param name="OverwritePaths">Sorgenti esplicitamente approvate</param>
    /// <param name="SkippedPaths">Sorgenti esplicitamente saltate</param>
    public sealed record WorkspaceWorkflowSnapshot(Guid Id, long Revision, WorkspaceWorkflowKind Kind, WorkspaceWorkflowInvocation InvocationParameters, WorkspaceWorkflowPhase Phase, string Draft, Guid QuestionId, Guid OperationId, string ErrorMessage, ImmutableArray<WorkspaceOverwriteQuestion> Conflicts = default, int ConflictIndex = 0, ImmutableArray<string> OverwritePaths = default, ImmutableArray<string> SkippedPaths = default)
    {
        /// <summary>True finché domanda o operazione appartengono al workspace</summary>
        public bool IsActive => this.Phase is WorkspaceWorkflowPhase.AwaitingInput or WorkspaceWorkflowPhase.AwaitingConfirmation or WorkspaceWorkflowPhase.AwaitingOverwrite or WorkspaceWorkflowPhase.Running or WorkspaceWorkflowPhase.Failed || (this.Kind is WorkspaceWorkflowKind.BatchRename or WorkspaceWorkflowKind.EditorExtensions or WorkspaceWorkflowKind.CreationPermissions or WorkspaceWorkflowKind.Permissions or WorkspaceWorkflowKind.Compress or WorkspaceWorkflowKind.Extract or WorkspaceWorkflowKind.TerminalRename or WorkspaceWorkflowKind.TerminalClose && this.Phase == WorkspaceWorkflowPhase.Cancelled);
    }

    /// <summary>Riferimento leggero, senza argomenti e draft della form</summary>
    /// <param name="Id">Workflow referenziato</param>
    /// <param name="Revision">Revisione da leggere nel runtime</param>
    /// <param name="Kind">Tipo del workflow</param>
    /// <param name="Phase">Fase corrente</param>
    public sealed record WorkspaceWorkflowReference(Guid Id, long Revision, WorkspaceWorkflowKind Kind, WorkspaceWorkflowPhase Phase);

    /// <summary>Risposta consume-once: identità e payload non vengono ricavati dallo stato UI successivo</summary>
    /// <param name="WorkflowId">Workflow destinatario</param>
    /// <param name="Revision">Revisione osservata</param>
    /// <param name="QuestionId">Domanda osservata</param>
    /// <param name="ResponseId">Identità stabile anche per una ritrasmissione</param>
    /// <param name="Cancelled">Annullamento esplicito dell'utente</param>
    /// <param name="Value">Valore confermato</param>
    /// <param name="Overwrite">Scelta tipizzata, presente soltanto per una domanda overwrite</param>
    public sealed record WorkspaceWorkflowResponse(Guid WorkflowId, long Revision, Guid QuestionId, Guid ResponseId, bool Cancelled, string Value, OverwriteChoice? Overwrite = null);

    /// <summary>Modalità e sorgente catturate, senza entry mutabili dei pannelli</summary>
    /// <param name="SourcePath">Sorgente catturata</param>
    /// <param name="Mode">Modalità risolta all'invocazione</param>
    public sealed record WorkspaceTransferEntry(string SourcePath, FileTransferMode Mode);

    /// <summary>Conflitto materializzato una volta; hydration non interroga nuovamente il filesystem</summary>
    /// <param name="SourcePath">Sorgente della domanda</param>
    /// <param name="DestinationPath">Destinazione della domanda</param>
    /// <param name="IsDirectory">Tipo catturato per il messaggio preesistente</param>
    /// <param name="Title">Titolo materializzato</param>
    /// <param name="Message">Messaggio materializzato</param>
    public sealed record WorkspaceOverwriteQuestion(string SourcePath, string DestinationPath, bool IsDirectory, string Title, string Message);

    /// <summary>Una riga della preview esatta con nome temporaneo assegnato una sola volta</summary>
    /// <param name="OriginalPath">Sorgente catturata</param>
    /// <param name="OriginalName">Nome originale per il rollback</param>
    /// <param name="NewName">Nome esatto mostrato nella preview</param>
    /// <param name="TemporaryPath">Percorso temporaneo del primo passaggio</param>
    public sealed record WorkspaceRenameItem(string OriginalPath, string OriginalName, string NewName, string TemporaryPath);

    /// <summary>Duplicate non esegue nuovamente alcun effetto; Denied include browser revocati</summary>
    public enum WorkspaceWorkflowResponseResult { Accepted, Duplicate, Stale, Denied }
}
