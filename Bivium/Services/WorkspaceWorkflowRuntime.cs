using Bivium.Models;
using System;
using System.Collections.Generic;

namespace Bivium.Services
{
    /// <summary>Stato del workflow protetto esclusivamente dal lock del workspace</summary>
    internal sealed class WorkspaceWorkflowRuntime
    {
        /// <summary>Workflow corrente; nessun componente o callback del circuito viene trattenuto</summary>
        internal WorkspaceWorkflowSnapshot Current { get; set; }

        /// <summary>Risposte delle domande del workflow corrente, anche dopo l'avanzamento al conflitto successivo</summary>
        internal Dictionary<Guid, WorkspaceWorkflowResponse> Responses { get; } = new Dictionary<Guid, WorkspaceWorkflowResponse>();

        /// <summary>Riferimento piccolo per le notifiche globali</summary>
        internal WorkspaceWorkflowReference Reference => this.Current == null ? null : new WorkspaceWorkflowReference(this.Current.Id, this.Current.Revision, this.Current.Kind, this.Current.Phase);
    }
}
