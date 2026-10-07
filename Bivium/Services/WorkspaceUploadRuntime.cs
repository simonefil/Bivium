using Bivium.Models;
using System;
using System.Collections.Generic;
using System.Threading;

namespace Bivium.Services
{
    /// <summary>Lifetime workspace; il gate serializza stream, retry e cleanup, mai il takeover</summary>
    internal sealed class WorkspaceUploadRuntime
    {
        /// <summary>Ultimo stato immutabile, protetto dal lock workspace</summary>
        internal WorkspaceUploadSnapshot Snapshot { get; set; }

        /// <summary>Un solo writer fisico o cleanup alla volta</summary>
        internal SemaphoreSlim Gate { get; } = new SemaphoreSlim(1, 1);

        /// <summary>Solo temporanei creati con CreateNew; protetto dal gate, rimosso dopo move o cleanup</summary>
        internal Dictionary<Guid, string> OwnedTemporaryPaths { get; } = new Dictionary<Guid, string>();

        /// <summary>Cancellato solo da cancel/reset/stop, non da dispose del circuito</summary>
        internal CancellationTokenSource Lifetime { get; } = new CancellationTokenSource();

        /// <summary>Cattura lo snapshot iniziale della sessione</summary>
        internal WorkspaceUploadRuntime(WorkspaceUploadSnapshot snapshot) => this.Snapshot = snapshot;
    }
}
