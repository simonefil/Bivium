using Bivium.Models;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Bivium.Services
{
    /// <summary>Task e cancellazione posseduti dal workspace, mai dal lease del browser</summary>
    internal sealed class WorkspaceOperationRuntime
    {
        /// <summary>Piano immutabile ammesso</summary>
        internal WorkspaceOperationPlan Plan { get; init; }
        /// <summary>Stato protetto dal lock workspace</summary>
        internal WorkspaceOperationSnapshot Snapshot { get; set; }
        /// <summary>Cancellazione esplicita o stop workspace, non revoca browser</summary>
        internal CancellationTokenSource Cancellation { get; init; }
        /// <summary>Task avviato una sola volta dopo l'ammissione</summary>
        internal Task Work { get; set; }

        /// <summary>Stato two-pass protetto dal lock workspace e indipendente dall'adapter</summary>
        internal List<WorkspaceRenameItemState> RenameState { get; set; } = new List<WorkspaceRenameItemState>();
        /// <summary>Stesso intervallo di notifica progresso usato dal Commander preesistente</summary>
        internal DateTime LastProgressPublicationUtc { get; set; }
        /// <summary>Conteggi effettivi fra due pubblicazioni coalescenti</summary>
        internal int FilesProcessed { get; set; }
        /// <summary>Radici fallite conservate fra le pubblicazioni</summary>
        internal int FilesFailed { get; set; }
        /// <summary>Passo effettivo fra due pubblicazioni coalescenti</summary>
        internal int ProgressCurrent { get; set; }
        /// <summary>Totale del passo effettivo</summary>
        internal int ProgressTotal { get; set; }
        /// <summary>Etichetta del passo effettivo</summary>
        internal string Stage { get; set; } = "";
        /// <summary>Risultato dettagliato privato del task, pubblicato atomicamente al completamento</summary>
        internal string ResultDraft { get; set; }
    }
}
