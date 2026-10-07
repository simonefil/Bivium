using System;
using System.Collections.Immutable;

namespace Bivium.Models
{
    /// <summary>Stato osservabile dell'operazione ammessa</summary>
    public enum WorkspaceOperationPhase { Running, CancellationRequested, Succeeded, Failed, Cancelled }

    /// <summary>Piano chiuso e immutabile: non contiene token browser né azioni eseguibili arbitrarie</summary>
    /// <param name="Kind">Operazione ammessa</param>
    /// <param name="SourcePath">Sorgente catturata</param>
    /// <param name="ParentPath">Destinazione catturata</param>
    /// <param name="Name">Nome confermato e validato</param>
    /// <param name="SourcePaths">Selezione delete immutabile catturata all'invocazione</param>
    /// <param name="Transfers">Selezione filtrata soltanto dalle decisioni consumate</param>
    /// <param name="OverwritePaths">Sole sovrascritture approvate</param>
    /// <param name="FromClipboard">Ownership della riconciliazione cut</param>
    /// <param name="RenamerSessionId">Sessione proprietaria del batch</param>
    /// <param name="RenameItems">Preview materializzata e nomi temporanei</param>
    /// <param name="FormDraft">Draft acknowledged congelato al salvataggio</param>
    /// <param name="FormContext">Valori originali per ownership e identità terminali</param>
    /// <param name="ExtractToOwnFolder">Modalità estrazione del piano</param>
    internal sealed record WorkspaceOperationPlan(WorkspaceWorkflowKind Kind, string SourcePath, string ParentPath, string Name, ImmutableArray<string> SourcePaths = default, ImmutableArray<WorkspaceTransferEntry> Transfers = default, ImmutableArray<string> OverwritePaths = default, bool FromClipboard = false, Guid RenamerSessionId = default, ImmutableArray<WorkspaceRenameItem> RenameItems = default, string FormDraft = "", string FormContext = "", bool ExtractToOwnFolder = false);

    /// <summary>Proiezione leggera del task workspace-owned, senza percorsi o piano esecutivo</summary>
    /// <param name="Id">Identità stabile del task</param>
    /// <param name="Revision">CAS per richieste di cancellazione</param>
    /// <param name="WorkflowId">Workflow proprietario</param>
    /// <param name="Kind">Tipo ammesso</param>
    /// <param name="Phase">Fase corrente</param>
    /// <param name="FilesProcessed">Elementi completati</param>
    /// <param name="FilesFailed">Elementi falliti</param>
    /// <param name="ErrorMessage">Errore finale</param>
    /// <param name="ProgressCurrent">Posizione pubblicata nel passo corrente</param>
    /// <param name="ProgressTotal">Totale del passo corrente</param>
    /// <param name="Stage">Etichetta del passo server-owned</param>
    public sealed record WorkspaceOperationSnapshot(Guid Id, long Revision, Guid WorkflowId, WorkspaceWorkflowKind Kind, WorkspaceOperationPhase Phase, int FilesProcessed, int FilesFailed, string ErrorMessage, int ProgressCurrent = 0, int ProgressTotal = 0, string Stage = "")
    {
        /// <summary>La cancellazione richiesta non equivale al completamento del task</summary>
        public bool IsRunning => this.Phase is WorkspaceOperationPhase.Running or WorkspaceOperationPhase.CancellationRequested;
    }

    /// <summary>Posizione effettiva dell'elemento nei due passaggi, incluso rollback fallito</summary>
    public enum WorkspaceRenameItemPhase { Original, Temporary, Finalized, RolledBack, RollbackFailed }

    /// <summary>Stato immutabile letto solo dall'adapter autorizzato, mai nelle notifiche globali</summary>
    /// <param name="Item">Riga catturata del piano</param>
    /// <param name="Phase">Ultimo esito del passo</param>
    /// <param name="CurrentPath">Posizione risultante dal servizio file</param>
    /// <param name="ErrorMessage">Errore della riga, incluso rollback</param>
    public sealed record WorkspaceRenameItemState(WorkspaceRenameItem Item, WorkspaceRenameItemPhase Phase, string CurrentPath, string ErrorMessage);
}
