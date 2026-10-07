using System;
using System.Collections.Immutable;
using System.Linq;

namespace Bivium.Models
{
    /// <summary>Fasi del trasferimento; hydration non avvia richieste byte</summary>
    public enum WorkspaceUploadPhase { Selecting, Uploading, Paused, Failed, Succeeded, Cancelled }

    /// <summary>Riferimento globale leggero, senza manifest, destinazioni o hash</summary>
    public sealed record WorkspaceUploadReference(Guid Id, long Revision, bool Visible);

    /// <summary>Metadati browser, senza handle o contenuto file</summary>
    public sealed record WorkspaceUploadSource(string RelativePath, string Name, long Size, long LastModified);

    /// <summary>Payload HTTP della selezione, separato dai messaggi SignalR del circuito</summary>
    public sealed record WorkspaceUploadManifestRequest(WorkspaceUploadSource[] Files, string[] Directories)
    {
        /// <summary>Bound HTTP applicato prima della deserializzazione del manifest</summary>
        public const long MAX_REQUEST_BYTES = 4L * 1024 * 1024;
        /// <summary>Cardinalità complessiva di file e directory prima della preparazione</summary>
        public const int MAX_ENTRIES = 10000;
        /// <summary>Bound di ogni stringa percorso o nome prima della risoluzione filesystem</summary>
        public const int MAX_PATH_CHARACTERS = 4096;
        /// <summary>Budget complessivo delle stringhe di metadata</summary>
        public const int MAX_METADATA_CHARACTERS = 1024 * 1024;
    }

    /// <summary>Ricevute server per i chunk persistiti; Completed impedisce un secondo commit finale</summary>
    public sealed record WorkspaceUploadFile(Guid Id, WorkspaceUploadSource Source, int ReceivedChunks, long ReceivedBytes, ImmutableArray<string> ChunkHashes, bool Completed, string Error = "");

    /// <summary>Preparazione e finalizzazione directory esistenti nel protocollo</summary>
    public sealed record WorkspaceUploadDirectory(string RelativePath, bool Prepared = false, bool Created = false, bool Finalized = false);

    /// <summary>Snapshot dettagliato accessibile soltanto con la lease corrente</summary>
    public sealed record WorkspaceUploadSnapshot(Guid Id, long Revision, string Destination, WorkspaceUploadPhase Phase, ImmutableArray<WorkspaceUploadFile> Files, ImmutableArray<WorkspaceUploadDirectory> Directories, string CurrentPath = "", string Error = "", bool Visible = true)
    {
        /// <summary>Dimensione chunk invariata rispetto al protocollo esistente</summary>
        public const long CHUNK_SIZE = 50L * 1024 * 1024;

        /// <summary>La selezione resta modificabile soltanto prima dell'avvio e del trasferimento owner</summary>
        public bool CanEditManifest => this.Phase == WorkspaceUploadPhase.Selecting;

        /// <summary>Aggregati di dominio calcolati dalla projection server, mai dai callback browser</summary>
        public long TotalBytes => this.Files.Sum(file => file.Source.Size);
        /// <summary>Byte dei soli chunk acknowledged</summary>
        public long ReceivedBytes => this.Files.Sum(file => file.ReceivedBytes);
        /// <summary>File il cui move finale è già stato eseguito</summary>
        public int CompletedFiles => this.Files.Count(file => file.Completed);
        /// <summary>Progresso acknowledged, compresi i file vuoti</summary>
        public int Percent => this.TotalBytes > 0 ? (int)(100.0 * this.ReceivedBytes / this.TotalBytes) : this.Files.IsEmpty ? 0 : (int)(100.0 * this.CompletedFiles / this.Files.Length);
    }
}
