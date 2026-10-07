using Microsoft.Extensions.Logging;
using Bivium.Controllers;
using Bivium.Models;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Bivium.Services
{
    public sealed partial class BiviumWorkspaceService
    {
        #region Variabili di classe

        /// <summary>Manifest e ricevute del trasferimento attuale</summary>
        private WorkspaceUploadRuntime _uploadRuntime;

        #endregion

        #region Metodi pubblici

        /// <summary>Apre il dialog server-owned senza accedere a file browser</summary>
        /// <param name="token">Lease del comando</param>
        /// <param name="destination">Destinazione già autorizzata</param>
        /// <returns>Sessione vuota senza handle browser</returns>
        internal WorkspaceUploadSnapshot BeginUpload(WorkspaceClientToken token, string destination)
        {
            BiviumWorkspaceSnapshot snapshot;
            Action<BiviumWorkspaceSnapshot>[] subscribers;
            WorkspaceUploadSnapshot upload;
            lock (this._lock)
            {
                this.RequireDesktopLeaseLocked(token);
                if (this._uploadRuntime?.Snapshot.Visible == true || this._workflowRuntime.Current?.IsActive == true || this._operationRuntime?.Snapshot.IsRunning == true)
                    throw new InvalidOperationException("A workspace workflow is already active");
                if (string.IsNullOrWhiteSpace(destination) || !this._workflowSecurity.IsPathSafe(destination) || !Directory.Exists(destination))
                    throw new ArgumentException("Invalid destination directory");
                upload = new WorkspaceUploadSnapshot(Guid.NewGuid(), 0, destination, WorkspaceUploadPhase.Selecting, [], []);
                this._uploadRuntime = new WorkspaceUploadRuntime(upload);
                snapshot = this.CommitUploadStateLocked();
                subscribers = this.GetSubscribers();
            }
            this.NotifySubscribers(subscribers, snapshot);
            return upload;
        }

        /// <summary>Legge manifest e ricevute soltanto per l'owner autorizzato</summary>
        /// <param name="token">Lease del lettore</param>
        /// <returns>Stato dettagliato oppure null per lettori non autorizzati</returns>
        internal WorkspaceUploadSnapshot GetUpload(WorkspaceClientToken token)
        {
            lock (this._lock)
            {
                if (!this.ValidateLeaseLocked(token) || this.IsStopped)
                    return null;
                return this._uploadRuntime?.Snapshot;
            }
        }

        /// <summary>Checkpoint CAS della selezione, consentito solo prima del primo trasferimento</summary>
        /// <param name="token">Lease del publisher</param>
        /// <param name="id">Sessione attesa</param>
        /// <param name="revision">Revisione acknowledged</param>
        /// <param name="files">Metadati delle sorgenti browser</param>
        /// <param name="directories">Directory relative, compresi i parent</param>
        /// <returns>True soltanto per il checkpoint ammesso</returns>
        internal bool TrySetUploadManifest(WorkspaceClientToken token, Guid id, long revision, WorkspaceUploadSource[] files, string[] directories)
        {
            BiviumWorkspaceSnapshot snapshot;
            Action<BiviumWorkspaceSnapshot>[] subscribers;
            WorkspaceUploadRuntime runtime;
            WorkspaceUploadSnapshot current;
            lock (this._lock)
            {
                runtime = this._uploadRuntime;
                current = runtime?.Snapshot;
                if (!this.ValidateLeaseLocked(token) || this.IsStopped || current?.Id != id || current.Revision != revision || current.Phase != WorkspaceUploadPhase.Selecting)
                    return false;
            }
            if (files == null || directories == null || files.Length > WorkspaceUploadManifestRequest.MAX_ENTRIES || directories.Length > WorkspaceUploadManifestRequest.MAX_ENTRIES - files.Length)
                throw new ArgumentException("Upload manifest exceeds entry limits or is invalid");
            // Tutti i bound precedono indici, split, ordinamento e accessi filesystem
            long metadataCharacters = 0;
            foreach (WorkspaceUploadSource source in files)
            {
                if (source == null || source.Name == null || source.RelativePath == null || source.Name.Length > WorkspaceUploadManifestRequest.MAX_PATH_CHARACTERS || source.RelativePath.Length > WorkspaceUploadManifestRequest.MAX_PATH_CHARACTERS || string.IsNullOrWhiteSpace(source.Name) || string.IsNullOrWhiteSpace(source.RelativePath))
                    throw new ArgumentException("Upload metadata exceeds path limits");
                metadataCharacters += source.Name.Length + source.RelativePath.Length;
                if (metadataCharacters > WorkspaceUploadManifestRequest.MAX_METADATA_CHARACTERS)
                    throw new ArgumentException("Upload metadata exceeds character budget");
            }
            foreach (string relative in directories)
            {
                if (relative == null || relative.Length > WorkspaceUploadManifestRequest.MAX_PATH_CHARACTERS || string.IsNullOrWhiteSpace(relative))
                    throw new ArgumentException("Upload metadata exceeds path limits");
                metadataCharacters += relative.Length;
                if (metadataCharacters > WorkspaceUploadManifestRequest.MAX_METADATA_CHARACTERS)
                    throw new ArgumentException("Upload metadata exceeds character budget");
            }
            StringComparer pathCaseComparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            HashSet<string> paths = new HashSet<string>(pathCaseComparer);
            Dictionary<WorkspaceUploadSource, WorkspaceUploadFile> previousFiles = current.Files.ToDictionary(file => file.Source);
            long totalBytes = 0;
            ImmutableArray<WorkspaceUploadFile>.Builder captured = ImmutableArray.CreateBuilder<WorkspaceUploadFile>();
            foreach (WorkspaceUploadSource source in files)
            {
                if (source.Size < 0 || source.LastModified < 0 || source.Size > (long)int.MaxValue * WorkspaceUploadSnapshot.CHUNK_SIZE || source.Name != Path.GetFileName(source.Name) || source.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    throw new ArgumentException("Invalid upload file metadata");
                if (source.Size > long.MaxValue - totalBytes)
                    throw new ArgumentException("Upload manifest size is too large");
                totalBytes += source.Size;
                string target = this.ResolveUploadPath(current.Destination, source.RelativePath);
                if (Path.GetFileName(target) != source.Name || !paths.Add(target))
                    throw new ArgumentException("Invalid or duplicate upload file path");
                previousFiles.TryGetValue(source, out WorkspaceUploadFile previous);
                captured.Add(previous ?? new WorkspaceUploadFile(Guid.NewGuid(), source, 0, 0, [], false));
            }
            ImmutableArray<WorkspaceUploadDirectory>.Builder folders = ImmutableArray.CreateBuilder<WorkspaceUploadDirectory>();
            HashSet<string> relativeDirectories = new HashSet<string>(StringComparer.Ordinal);
            foreach (string relative in directories.OrderBy(path => path.Count(character => character == '/')))
            {
                string target = this.ResolveUploadPath(current.Destination, relative);
                if (!paths.Add(target) || !relativeDirectories.Add(relative))
                    throw new ArgumentException("Invalid or duplicate upload directory path");
                folders.Add(new WorkspaceUploadDirectory(relative));
            }
            // Riserva anche il namespace temporaneo: nessun finale/directory può coincidere con esso
            foreach (WorkspaceUploadFile file in captured)
            {
                string temporary = Path.GetFullPath(GetUploadTempPath(current, file));
                if (!paths.Add(temporary) || File.Exists(temporary) || Directory.Exists(temporary))
                    throw new ArgumentException("Upload temporary path collides with an existing or selected path");
                int separator = file.Source.RelativePath.LastIndexOf('/');
                if (separator >= 0 && !relativeDirectories.Contains(file.Source.RelativePath.Substring(0, separator)))
                    throw new ArgumentException("Upload directory was not selected");
            }
            // Il parent immediato di ogni directory garantisce transitivamente tutti gli antenati
            foreach (string relative in directories)
            {
                int separator = relative.LastIndexOf('/');
                if (separator >= 0 && !relativeDirectories.Contains(relative.Substring(0, separator)))
                    throw new ArgumentException("Upload parent directory was not selected");
            }
            WorkspaceUploadSnapshot prepared = current with { Revision = current.Revision + 1, Files = captured.ToImmutable(), Directories = folders.ToImmutable(), Error = "" };
            lock (this._lock)
            {
                if (!this.ValidateLeaseLocked(token) || this.IsStopped || !ReferenceEquals(this._uploadRuntime, runtime) || runtime.Snapshot.Id != id || runtime.Snapshot.Revision != revision || runtime.Snapshot.Phase != WorkspaceUploadPhase.Selecting || runtime.Lifetime.IsCancellationRequested)
                    return false;
                runtime.Snapshot = prepared;
                snapshot = this.CommitUploadStateLocked();
                subscribers = this.GetSubscribers();
            }
            this.NotifySubscribers(subscribers, snapshot);
            return true;
        }

        /// <summary>Avvio o ripresa espliciti dopo la verifica della sorgente nel browser</summary>
        /// <param name="token">Lease dell'owner</param>
        /// <param name="id">Sessione catturata</param>
        /// <param name="revision">Ricevute verificate dal browser</param>
        /// <returns>Stato di trasferimento ammesso</returns>
        internal WorkspaceUploadSnapshot StartUpload(WorkspaceClientToken token, Guid id, long revision)
        {
            return this.ChangeUpload(token, id, current =>
            {
                if (current.Revision != revision || !current.Visible || current.Phase == WorkspaceUploadPhase.Cancelled || current.Phase == WorkspaceUploadPhase.Succeeded || (current.Files.IsEmpty && current.Directories.IsEmpty))
                    throw new InvalidOperationException("Upload state changed. Verify the source again.");
                return current with { Phase = WorkspaceUploadPhase.Uploading, Error = "" };
            });
        }

        /// <summary>Stop locale del trasporto; non elimina i chunk acknowledged</summary>
        internal WorkspaceUploadSnapshot PauseUpload(WorkspaceClientToken token, Guid id, string error = "") => this.ChangeUpload(token, id, current => !current.Visible ? current : current with { Phase = current.Phase == WorkspaceUploadPhase.Selecting ? WorkspaceUploadPhase.Selecting : string.IsNullOrEmpty(error) ? WorkspaceUploadPhase.Paused : WorkspaceUploadPhase.Failed, Error = string.IsNullOrEmpty(error) ? current.Error : error }, true);

        /// <summary>Un errore HTTP rimane nel runtime anche se il browser perde la risposta</summary>
        internal void RecordUploadError(WorkspaceClientToken token, Guid id, string error)
        {
            try { this.ChangeUpload(token, id, current => current.Visible ? current with { Error = error } : current, true); }
            catch (OperationCanceledException) { }
            catch (InvalidOperationException) { }
        }

        /// <summary>Pausa esplicita o per handoff: attende la quiescenza del writer server e conserva i chunk acknowledged</summary>
        internal async Task<WorkspaceUploadSnapshot> PauseUploadTransferAsync(WorkspaceClientToken token, Guid id, CancellationToken cancellationToken)
        {
            WorkspaceUploadRuntime runtime = this.RequireUploadRuntime(token, id);
            BiviumWorkspaceSnapshot snapshot;
            Action<BiviumWorkspaceSnapshot>[] subscribers;
            WorkspaceUploadSnapshot upload;
            await runtime.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                lock (this._lock)
                {
                    this.RequireUploadRuntimeLocked(token, id, runtime, true);
                    WorkspaceUploadSnapshot current = runtime.Snapshot;
                    upload = !current.Visible ? current : current with { Revision = current.Revision + 1, Phase = current.Phase == WorkspaceUploadPhase.Selecting ? WorkspaceUploadPhase.Selecting : WorkspaceUploadPhase.Paused };
                    runtime.Snapshot = upload;
                    snapshot = this.CommitUploadStateLocked();
                    subscribers = this.GetSubscribers();
                }
            }
            finally { runtime.Gate.Release(); }
            this.NotifySubscribers(subscribers, snapshot);
            return upload;
        }

        /// <summary>Il server conferma la fine dai file e dalle directory, non da un contatore browser</summary>
        internal WorkspaceUploadSnapshot CompleteUpload(WorkspaceClientToken token, Guid id) => this.ChangeUpload(token, id, current =>
        {
            string fileError = current.Files.FirstOrDefault(file => !string.IsNullOrEmpty(file.Error))?.Error;
            if (!string.IsNullOrEmpty(fileError))
                throw new IOException(fileError);
            if (current.Files.Any(file => !file.Completed) || current.Directories.Any(directory => !directory.Prepared || (directory.Created && !directory.Finalized)))
                throw new InvalidOperationException("Upload is not complete");
            return current with { Phase = WorkspaceUploadPhase.Succeeded, Visible = false, CurrentPath = "", Error = "" };
        });

        /// <summary>Cancel esplicito; cleanup dei soli temporanei della sessione</summary>
        /// <param name="token">Lease del gesto esplicito</param>
        /// <param name="id">Sessione da annullare</param>
        internal async Task CancelUploadAsync(WorkspaceClientToken token, Guid id)
        {
            WorkspaceUploadRuntime runtime;
            BiviumWorkspaceSnapshot snapshot;
            Action<BiviumWorkspaceSnapshot>[] subscribers;
            lock (this._lock)
            {
                runtime = this.RequireUploadRuntimeLocked(token, id);
                runtime.Snapshot = runtime.Snapshot with { Revision = runtime.Snapshot.Revision + 1, Phase = WorkspaceUploadPhase.Cancelled, Visible = false };
                this.ActivateNextTerminalClipboardLocked();
                this.CommitWorkflowStateLocked();
                snapshot = this.CommitUploadStateLocked();
                subscribers = this.GetSubscribers();
            }
            this.NotifySubscribers(subscribers, snapshot);
            await this.CleanupUploadAsync(runtime).ConfigureAwait(false);
        }

        /// <summary>Riceve il protocollo chunked esistente con rollback del solo chunk non acknowledged</summary>
        internal async Task ReceiveUploadChunkAsync(WorkspaceClientToken token, Guid sessionId, Guid fileId, string destination, string relative, string name, int chunkIndex, int totalChunks, Stream body, CancellationToken requestCancellation, bool verifyOnly = false)
        {
            long revision = this.GetSnapshot().Revision;
            try
            {
                await this.ReceiveUploadChunkCoreAsync(token, sessionId, fileId, destination, relative, name, chunkIndex, totalChunks, body, requestCancellation, verifyOnly).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                if (!verifyOnly)
                    this.RecordUploadError(token, sessionId, ex.Message);
                throw;
            }
            finally
            {
                BiviumWorkspaceSnapshot snapshot;
                Action<BiviumWorkspaceSnapshot>[] subscribers;
                lock (this._lock)
                {
                    snapshot = this._snapshot;
                    subscribers = verifyOnly || snapshot.Revision == revision ? [] : this.GetSubscribers();
                }
                // Anche i callback che resettano o cancellano la sessione devono poter acquisire il gate
                this.NotifySubscribers(subscribers, snapshot);
            }
        }

        /// <summary>Writer serializzato; nessuna notifica al circuito mentre possiede il gate fisico</summary>
        private async Task ReceiveUploadChunkCoreAsync(WorkspaceClientToken token, Guid sessionId, Guid fileId, string destination, string relative, string name, int chunkIndex, int totalChunks, Stream body, CancellationToken requestCancellation, bool verifyOnly)
        {
            WorkspaceUploadRuntime runtime = this.RequireUploadRuntime(token, sessionId);
            using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(requestCancellation, runtime.Lifetime.Token, this.GetRevocationToken(token));
            await runtime.Gate.WaitAsync(cancellation.Token).ConfigureAwait(false);
            string tempPath = "";
            long acknowledgedLength = 0;
            bool wrote = false;
            bool accepted = false;
            try
            {
                WorkspaceUploadFile file;
                lock (this._lock)
                {
                    this.RequireUploadRuntimeLocked(token, sessionId, runtime);
                    WorkspaceUploadSnapshot current = runtime.Snapshot;
                    file = current.Files.FirstOrDefault(item => item.Id == fileId) ?? throw new ArgumentException("Unknown upload file");
                    if ((!verifyOnly && current.Phase != WorkspaceUploadPhase.Uploading) || (verifyOnly && chunkIndex >= file.ReceivedChunks) || destination != current.Destination || relative != file.Source.RelativePath || name != file.Source.Name || totalChunks != GetUploadChunkCount(file.Source.Size) || chunkIndex < 0 || chunkIndex >= totalChunks || chunkIndex > file.ReceivedChunks)
                        throw new ArgumentException("Invalid upload chunk metadata");
                    string target = this.ResolveUploadPath(destination, relative);
                    if (!Directory.Exists(Path.GetDirectoryName(target)))
                        throw new ArgumentException("Upload directory was not prepared");
                    tempPath = GetUploadTempPath(current, file);
                    acknowledgedLength = file.ReceivedBytes;
                }
                long expectedBytes = Math.Min(WorkspaceUploadSnapshot.CHUNK_SIZE, file.Source.Size - chunkIndex * WorkspaceUploadSnapshot.CHUNK_SIZE);
                bool duplicate = chunkIndex < file.ReceivedChunks;
                using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                FileStream writer = null;
                if (!duplicate)
                {
                    bool owned = runtime.OwnedTemporaryPaths.TryGetValue(fileId, out string ownedPath);
                    if (owned && ownedPath != tempPath)
                        throw new IOException("Upload temporary ownership does not match");
                    if (!owned && acknowledgedLength != 0)
                        throw new IOException("Upload temporary ownership is missing");
                    writer = new FileStream(tempPath, owned ? FileMode.Open : FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                    if (!owned)
                        runtime.OwnedTemporaryPaths.Add(fileId, tempPath);
                }
                await using FileStream output = writer;
                if (output != null)
                {
                    if (output.Length != acknowledgedLength)
                        throw new IOException("Upload temporary length does not match acknowledged chunks");
                    output.Position = acknowledgedLength;
                    wrote = true;
                }
                byte[] buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await body.ReadAsync(buffer.AsMemory(), cancellation.Token).ConfigureAwait(false)) != 0)
                {
                    received += read;
                    if (received > expectedBytes)
                        throw new ArgumentException("Upload chunk size does not match manifest");
                    hash.AppendData(buffer, 0, read);
                    if (output != null)
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellation.Token).ConfigureAwait(false);
                }
                if (received != expectedBytes)
                    throw new ArgumentException("Incomplete upload chunk");
                string digest = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                if (output != null)
                {
                    await output.FlushAsync(cancellation.Token).ConfigureAwait(false);
                    await output.DisposeAsync().ConfigureAwait(false);
                }
                string permissionError = "";
                lock (this._lock)
                {
                    this.RequireUploadRuntimeLocked(token, sessionId, runtime);
                    if (duplicate)
                    {
                        if (file.ChunkHashes[chunkIndex] != digest)
                            throw new ArgumentException("Retry chunk does not match the received source");
                        if (!verifyOnly && !string.IsNullOrEmpty(file.Error))
                            throw new IOException(file.Error);
                        return;
                    }
                    WorkspaceUploadSnapshot current = runtime.Snapshot;
                    if (current.Phase != WorkspaceUploadPhase.Uploading)
                        throw new InvalidOperationException("Upload is paused. Verify the source and resume.");
                    bool completed = chunkIndex == totalChunks - 1;
                    if (completed)
                    {
                        string target = this.ResolveUploadPath(destination, relative);
                        File.Move(tempPath, target, true);
                        runtime.OwnedTemporaryPaths.Remove(fileId);
                        // Il move è irreversibile: ricevuta e stato vengono pubblicati anche se i default falliscono
                        accepted = true;
                        try
                        {
                            FileOperationResult permissions = this._workflowPermissions.ApplyDefaultCreationPermissions(target, false, CancellationToken.None);
                            if (!permissions.Success)
                                permissionError = permissions.ErrorMessage;
                        }
                        catch (Exception ex)
                        {
                            permissionError = ex.Message;
                        }
                    }
                    WorkspaceUploadFile updated = file with { ReceivedChunks = file.ReceivedChunks + 1, ReceivedBytes = file.ReceivedBytes + received, ChunkHashes = file.ChunkHashes.Add(digest), Completed = completed, Error = permissionError };
                    runtime.Snapshot = current with { Revision = current.Revision + 1, Files = current.Files.Replace(file, updated), CurrentPath = relative, Error = permissionError };
                    accepted = true;
                    this.CommitUploadStateLocked();
                }
                if (!string.IsNullOrEmpty(permissionError))
                    throw new IOException(permissionError);
            }
            finally
            {
                try
                {
                    if (wrote && !accepted && runtime.OwnedTemporaryPaths.TryGetValue(fileId, out string ownedPath) && ownedPath == tempPath && File.Exists(tempPath))
                    {
                        using FileStream rollback = new FileStream(tempPath, FileMode.Open, FileAccess.Write, FileShare.None);
                        rollback.SetLength(acknowledgedLength);
                    }
                }
                finally { runtime.Gate.Release(); }
            }
        }

        /// <summary>Directory idempotenti; i default restano limitati alle directory create dalla sessione</summary>
        internal void ReceiveUploadDirectory(WorkspaceClientToken token, Guid sessionId, string destination, string relative, bool finalize)
        {
            this.ChangeUpload(token, sessionId, current =>
            {
                if (current.Destination != destination || current.Phase != WorkspaceUploadPhase.Uploading)
                    throw new ArgumentException("Invalid upload directory metadata");
                WorkspaceUploadDirectory directory = current.Directories.FirstOrDefault(item => item.RelativePath == relative) ?? throw new ArgumentException("Unknown upload directory");
                string path = this.ResolveUploadPath(destination, relative);
                WorkspaceUploadDirectory updated = directory;
                if (finalize && !directory.Finalized)
                {
                    if (!directory.Prepared || !Directory.Exists(path))
                        throw new DirectoryNotFoundException("Upload directory not found: " + relative);
                    if (directory.Created)
                    {
                        FileOperationResult result = this._workflowPermissions.ApplyDefaultCreationPermissions(path, true, CancellationToken.None);
                        if (!result.Success)
                            throw new IOException(result.ErrorMessage);
                    }
                    updated = directory with { Finalized = true };
                }
                else if (!finalize && !directory.Prepared)
                {
                    bool created = !Directory.Exists(path);
                    Directory.CreateDirectory(path);
                    updated = directory with { Prepared = true, Created = created };
                }
                return current with { Directories = current.Directories.Replace(directory, updated), CurrentPath = relative };
            });
        }

        #endregion

        #region Metodi privati

        /// <summary>Mutazione breve con autorità lease e notifiche fuori dal lock</summary>
        private WorkspaceUploadSnapshot ChangeUpload(WorkspaceClientToken token, Guid id, Func<WorkspaceUploadSnapshot, WorkspaceUploadSnapshot> change, bool publication = false)
        {
            BiviumWorkspaceSnapshot snapshot;
            Action<BiviumWorkspaceSnapshot>[] subscribers;
            WorkspaceUploadSnapshot upload;
            lock (this._lock)
            {
                WorkspaceUploadRuntime runtime = this.RequireUploadRuntimeLocked(token, id, null, publication);
                upload = change(runtime.Snapshot) with { Revision = runtime.Snapshot.Revision + 1 };
                runtime.Snapshot = upload;
                if (!upload.Visible)
                {
                    this.ActivateNextTerminalClipboardLocked();
                    this.CommitWorkflowStateLocked();
                }
                snapshot = this.CommitUploadStateLocked();
                subscribers = this.GetSubscribers();
            }
            this.NotifySubscribers(subscribers, snapshot);
            return upload;
        }

        /// <summary>Acquisisce il runtime senza mantenere il lock durante lo stream</summary>
        private WorkspaceUploadRuntime RequireUploadRuntime(WorkspaceClientToken token, Guid id)
        {
            lock (this._lock)
                return this.RequireUploadRuntimeLocked(token, id);
        }

        /// <summary>Ogni commit byte rivalida owner, sessione e lifetime</summary>
        private WorkspaceUploadRuntime RequireUploadRuntimeLocked(WorkspaceClientToken token, Guid id, WorkspaceUploadRuntime expected = null, bool publication = false)
        {
            if (this.IsStopped || !(publication ? this.ValidateLeaseLocked(token) : this.ValidateMutationLocked(token)))
                throw new OperationCanceledException("Workspace lease revoked");
            WorkspaceUploadRuntime runtime = this._uploadRuntime;
            if (runtime == null || runtime.Snapshot.Id != id || (expected != null && !ReferenceEquals(runtime, expected)) || runtime.Snapshot.Phase == WorkspaceUploadPhase.Cancelled || runtime.Lifetime.IsCancellationRequested)
                throw new InvalidOperationException("Upload session is no longer active");
            return runtime;
        }

        /// <summary>Riusa esattamente la validazione relativa del controller esistente</summary>
        /// <param name="destination">Destinazione catturata</param>
        /// <param name="relative">Percorso relativo browser</param>
        /// <returns>Percorso assoluto validato</returns>
        private string ResolveUploadPath(string destination, string relative)
        {
            if (!this._workflowSecurity.IsPathSafe(destination) || !Directory.Exists(destination) || !FileTransferController.TryResolveUploadPath(destination, relative, out string target, out _))
                throw new ArgumentException("Invalid relative upload path");
            return target;
        }

        /// <summary>Naming e directory temporanea invariati; ID server-owned della singola sorgente</summary>
        private static string GetUploadTempPath(WorkspaceUploadSnapshot upload, WorkspaceUploadFile file) => Path.Combine(upload.Destination, "." + file.Source.Name + "." + file.Id.ToString("N") + ".uploading");

        /// <summary>Almeno un chunk anche per file vuoti, come nel protocollo esistente</summary>
        private static int GetUploadChunkCount(long size) => (int)Math.Max(1, (size + WorkspaceUploadSnapshot.CHUNK_SIZE - 1) / WorkspaceUploadSnapshot.CHUNK_SIZE);

        /// <summary>Riferimento globale leggero; dettagli separati e autorizzati</summary>
        private BiviumWorkspaceSnapshot CommitUploadStateLocked()
        {
            WorkspaceUploadSnapshot upload = this._uploadRuntime?.Snapshot;
            WorkspaceUploadReference reference = upload == null ? null : new WorkspaceUploadReference(upload.Id, upload.Revision, upload.Visible);
            this._snapshot = new BiviumWorkspaceSnapshot(this._snapshot.Revision + 1, this._snapshot.Panels, this._snapshot.FloatingWindows, this._snapshot.ActiveClientLease, this._snapshot.Desktop, this._snapshot.Handoff, this._snapshot.Workflow, this._snapshot.Operation, reference);
            return this._snapshot;
        }

        /// <summary>Annulla il trasporto e aspetta il rollback prima di rimuovere i temporanei posseduti</summary>
        /// <param name="runtime">Sessione posseduta dal workspace</param>
        private async Task CleanupUploadAsync(WorkspaceUploadRuntime runtime)
        {
            runtime.Lifetime.Cancel();
            await runtime.Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                foreach (KeyValuePair<Guid, string> temporary in runtime.OwnedTemporaryPaths.ToArray())
                {
                    try
                    {
                        File.Delete(temporary.Value);
                        runtime.OwnedTemporaryPaths.Remove(temporary.Key);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        this._logger.LogWarning("Upload temporary cleanup failed for session {SessionId}", runtime.Snapshot.Id);
                    }
                }
            }
            finally
            {
                runtime.Gate.Release();
            }
        }

        #endregion
    }
}
