using System;
using System.Collections.Immutable;
using System.Linq;

namespace Bivium.Models
{
    /// <summary>Transfer phases; hydration does not start byte requests</summary>
    public enum WorkspaceUploadPhase { Selecting, Uploading, Paused, Failed, Succeeded, Cancelled }

    /// <summary>Lightweight global reference, without manifest, destinations or hashes</summary>
    public sealed record WorkspaceUploadReference(Guid Id, long Revision, bool Visible);

    /// <summary>Browser metadata, without handles or file content</summary>
    public sealed record WorkspaceUploadSource(string RelativePath, string Name, long Size, long LastModified);

    /// <summary>HTTP payload of the selection, separate from the circuit's SignalR messages</summary>
    public sealed record WorkspaceUploadManifestRequest(WorkspaceUploadSource[] Files, string[] Directories)
    {
        /// <summary>HTTP bound applied before manifest deserialization</summary>
        public const long MAX_REQUEST_BYTES = 4L * 1024 * 1024;
        /// <summary>Overall cardinality of files and directories before preparation</summary>
        public const int MAX_ENTRIES = 10000;
        /// <summary>Bound of each path or name string before filesystem resolution</summary>
        public const int MAX_PATH_CHARACTERS = 4096;
        /// <summary>Overall budget of the metadata strings</summary>
        public const int MAX_METADATA_CHARACTERS = 1024 * 1024;
    }

    /// <summary>Server receipts for persisted chunks; Completed prevents a second final commit</summary>
    public sealed record WorkspaceUploadFile(Guid Id, WorkspaceUploadSource Source, int ReceivedChunks, long ReceivedBytes, ImmutableArray<string> ChunkHashes, bool Completed, string Error = "");

    /// <summary>Directory preparation and finalization already present in the protocol</summary>
    public sealed record WorkspaceUploadDirectory(string RelativePath, bool Prepared = false, bool Created = false, bool Finalized = false);

    /// <summary>Detailed snapshot accessible only with the current lease</summary>
    public sealed record WorkspaceUploadSnapshot(Guid Id, long Revision, string Destination, WorkspaceUploadPhase Phase, ImmutableArray<WorkspaceUploadFile> Files, ImmutableArray<WorkspaceUploadDirectory> Directories, string CurrentPath = "", string Error = "", bool Visible = true)
    {
        /// <summary>Chunk size unchanged from the existing protocol</summary>
        public const long CHUNK_SIZE = 50L * 1024 * 1024;

        /// <summary>The selection remains editable only before start and owner transfer</summary>
        public bool CanEditManifest => this.Phase == WorkspaceUploadPhase.Selecting;

        /// <summary>Domain aggregates computed by the server projection, never by browser callbacks</summary>
        public long TotalBytes => this.Files.Sum(file => file.Source.Size);
        /// <summary>Bytes of acknowledged chunks only</summary>
        public long ReceivedBytes => this.Files.Sum(file => file.ReceivedBytes);
        /// <summary>Files whose final move has already been performed</summary>
        public int CompletedFiles => this.Files.Count(file => file.Completed);
        /// <summary>Acknowledged progress, including empty files</summary>
        public int Percent => this.TotalBytes > 0 ? (int)(100.0 * this.ReceivedBytes / this.TotalBytes) : this.Files.IsEmpty ? 0 : (int)(100.0 * this.CompletedFiles / this.Files.Length);
    }
}
