using Bivium.Models;
using System;
using System.Collections.Generic;
using System.Threading;

namespace Bivium.Services
{
    /// <summary>Workspace lifetime; the gate serializes stream, retry and cleanup, never the takeover</summary>
    internal sealed class WorkspaceUploadRuntime
    {
        /// <summary>Last immutable state, protected by the workspace lock</summary>
        internal WorkspaceUploadSnapshot Snapshot { get; set; }

        /// <summary>Only one physical writer or cleanup at a time</summary>
        internal SemaphoreSlim Gate { get; } = new SemaphoreSlim(1, 1);

        /// <summary>Only temporary files created with CreateNew; protected by the gate, removed after move or cleanup</summary>
        internal Dictionary<Guid, string> OwnedTemporaryPaths { get; } = new Dictionary<Guid, string>();

        /// <summary>Cancelled only by cancel/reset/stop, not by circuit dispose</summary>
        internal CancellationTokenSource Lifetime { get; } = new CancellationTokenSource();

        /// <summary>Captures the initial snapshot of the session</summary>
        internal WorkspaceUploadRuntime(WorkspaceUploadSnapshot snapshot) => this.Snapshot = snapshot;
    }
}
