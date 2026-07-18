using System.Formats.Tar;
using System.IO.Compression;
using SharpCompress.Common;
using SharpCompress.Readers;
using Bivium.Models;

namespace Bivium.Services
{
    /// <summary>
    /// Handles archive compression and extraction operations
    /// </summary>
    public class ArchiveService : IArchiveService
    {
        #region Class Variables

        /// <summary>
        /// Security service for path validation
        /// </summary>
        private readonly SecurityService _securityService;

        /// <summary>
        /// Supported archive extensions mapped to detection
        /// </summary>
        private static readonly string[] s_archiveExtensions = new string[]
        {
            ".zip", ".tar", ".tar.gz", ".tgz", ".tar.bz2", ".tbz2",
            ".tar.xz", ".txz", ".tar.zst", ".tzst"
        };

        #endregion

        #region Constructor

        /// <summary>
        /// Creates a new ArchiveService
        /// </summary>
        /// <param name="securityService">Security service instance</param>
        public ArchiveService(SecurityService securityService)
        {
            this._securityService = securityService;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Extracts an archive to a destination directory
        /// </summary>
        /// <param name="archivePath">Path to the archive file</param>
        /// <param name="destinationDir">Directory to extract into</param>
        /// <param name="onProgress">Progress callback (current, total, currentFileName)</param>
        /// <param name="cancellationToken">Cancellation token for lease revocation</param>
        /// <returns>Operation result</returns>
        public FileOperationResult ExtractArchive(string archivePath, string destinationDir, Action<int, int, string> onProgress, CancellationToken cancellationToken = default)
        {
            FileOperationResult result = new FileOperationResult();

            cancellationToken.ThrowIfCancellationRequested();

            if (!this._securityService.IsPathSafe(archivePath) || !this._securityService.IsPathSafe(destinationDir))
            {
                result = FileOperationResult.Fail("Invalid path");
                return result;
            }

            if (!File.Exists(archivePath))
            {
                result = FileOperationResult.Fail("Archive not found: " + archivePath);
                return result;
            }

            try
            {
                string lowerPath = archivePath.ToLowerInvariant();

                // ZIP: use native System.IO.Compression
                if (lowerPath.EndsWith(".zip"))
                {
                    result = this.ExtractZip(archivePath, destinationDir, onProgress, cancellationToken);
                }
                // TAR.ZST: native tar + ZstdSharp
                else if (lowerPath.EndsWith(".tar.zst") || lowerPath.EndsWith(".tzst"))
                {
                    result = this.ExtractTarZst(archivePath, destinationDir, onProgress, cancellationToken);
                }
                // TAR, TAR.GZ, TAR.BZ2, TAR.XZ: SharpCompress handles all
                else
                {
                    result = this.ExtractWithSharpCompress(archivePath, destinationDir, onProgress, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result = FileOperationResult.Fail("Extraction failed: " + ex.Message);
            }

            return result;
        }

        /// <summary>
        /// Creates an archive from a list of files and directories
        /// </summary>
        /// <param name="outputPath">Output archive file path</param>
        /// <param name="sourcePaths">List of file/directory paths to compress</param>
        /// <param name="format">Archive format to create</param>
        /// <param name="onProgress">Progress callback (current, total, currentFileName)</param>
        /// <param name="cancellationToken">Cancellation token for lease revocation</param>
        /// <returns>Operation result</returns>
        public FileOperationResult CreateArchive(string outputPath, List<string> sourcePaths, ArchiveFormat format, Action<int, int, string> onProgress, CancellationToken cancellationToken = default)
        {
            FileOperationResult result = new FileOperationResult();

            cancellationToken.ThrowIfCancellationRequested();

            if (!this._securityService.IsPathSafe(outputPath))
            {
                result = FileOperationResult.Fail("Invalid output path");
                return result;
            }
            if (sourcePaths == null || sourcePaths.Count == 0)
            {
                result = FileOperationResult.Fail("No source paths selected");
                return result;
            }

            try
            {
                string validationError = this.ValidateArchiveSources(outputPath, sourcePaths);
                if (!string.IsNullOrEmpty(validationError))
                {
                    result = FileOperationResult.Fail(validationError);
                    return result;
                }

                // Count total files for progress
                int totalFiles = 0;
                for (int i = 0; i < sourcePaths.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    totalFiles += this.CountFiles(sourcePaths[i], cancellationToken);
                }

                if (format == ArchiveFormat.Zip)
                {
                    result = this.CreateZip(outputPath, sourcePaths, totalFiles, onProgress, cancellationToken);
                }
                else
                {
                    result = this.CreateTarVariant(outputPath, sourcePaths, format, totalFiles, onProgress, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result = FileOperationResult.Fail("Compression failed: " + ex.Message);
            }

            return result;
        }

        /// <summary>
        /// Checks if a file path has a supported archive extension
        /// </summary>
        /// <param name="path">File path to check</param>
        /// <returns>True if the file is a supported archive</returns>
        public bool IsArchive(string path)
        {
            bool result = false;
            string lowerPath = path.ToLowerInvariant();

            for (int i = 0; i < s_archiveExtensions.Length; i++)
            {
                if (lowerPath.EndsWith(s_archiveExtensions[i]))
                {
                    result = true;
                    break;
                }
            }

            return result;
        }

        /// <summary>
        /// Gets the default file extension for an archive format
        /// </summary>
        /// <param name="format">Archive format</param>
        /// <returns>File extension including dot</returns>
        public string GetExtension(ArchiveFormat format)
        {
            string result = ".zip";

            if (format == ArchiveFormat.Tar)
            {
                result = ".tar";
            }
            else if (format == ArchiveFormat.TarGz)
            {
                result = ".tar.gz";
            }
            else if (format == ArchiveFormat.TarBz2)
            {
                result = ".tar.bz2";
            }
            else if (format == ArchiveFormat.TarXz)
            {
                result = ".tar.xz";
            }
            else if (format == ArchiveFormat.TarZst)
            {
                result = ".tar.zst";
            }

            return result;
        }

        #endregion

        #region Private Methods - Extraction

        /// <summary>
        /// Extracts a ZIP archive using native System.IO.Compression
        /// </summary>
        /// <param name="archivePath">Path to the ZIP file</param>
        /// <param name="destinationDir">Destination directory</param>
        /// <param name="onProgress">Progress callback</param>
        /// <returns>Operation result</returns>
        private FileOperationResult ExtractZip(string archivePath, string destinationDir, Action<int, int, string> onProgress, CancellationToken cancellationToken)
        {
            int processed = 0;
            using ZipArchive archive = ZipFile.OpenRead(archivePath);
            int totalEntries = archive.Entries.Count;

            for (int i = 0; i < archive.Entries.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ZipArchiveEntry entry = archive.Entries[i];
                string destPath = Path.Combine(destinationDir, entry.FullName);

                // Security: prevent path traversal
                if (!this.IsPathInsideDirectory(destPath, destinationDir))
                {
                    continue;
                }

                if (string.IsNullOrEmpty(entry.Name))
                {
                    // Directory entry
                    Directory.CreateDirectory(destPath);
                }
                else
                {
                    // File entry
                    Directory.CreateDirectory(Path.GetDirectoryName(destPath));
                    using Stream source = entry.Open();
                    using FileStream destination = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    this.CopyStreamCancellable(source, destination, cancellationToken);
                    processed++;
                }

                if (onProgress != null)
                {
                    onProgress(i + 1, totalEntries, entry.FullName);
                }
            }

            FileOperationResult result = FileOperationResult.Ok(processed);
            return result;
        }

        /// <summary>
        /// Extracts a TAR.ZST archive using ZstdSharp + native TAR
        /// </summary>
        /// <param name="archivePath">Path to the tar.zst file</param>
        /// <param name="destinationDir">Destination directory</param>
        /// <param name="onProgress">Progress callback</param>
        /// <returns>Operation result</returns>
        private FileOperationResult ExtractTarZst(string archivePath, string destinationDir, Action<int, int, string> onProgress, CancellationToken cancellationToken)
        {
            int processed = 0;

            // First pass: count entries
            int totalEntries = 0;
            using (FileStream countStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read))
            using (ZstdSharp.DecompressionStream countDecomp = new ZstdSharp.DecompressionStream(countStream))
            using (TarReader countReader = new TarReader(countDecomp))
            {
                while (countReader.GetNextEntry() != null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    totalEntries++;
                }
            }

            // Second pass: extract
            using FileStream fileStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read);
            using ZstdSharp.DecompressionStream decompStream = new ZstdSharp.DecompressionStream(fileStream);
            using TarReader tarReader = new TarReader(decompStream);
            int current = 0;

            TarEntry entry = tarReader.GetNextEntry();
            while (entry != null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                current++;
                string destPath = Path.Combine(destinationDir, entry.Name);

                // Security: prevent path traversal
                if (this.IsPathInsideDirectory(destPath, destinationDir))
                {
                    if (entry.EntryType == TarEntryType.Directory)
                    {
                        Directory.CreateDirectory(destPath);
                    }
                    else if (entry.EntryType == TarEntryType.RegularFile)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(destPath));
                        using FileStream destination = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
                        if (entry.DataStream != null)
                            this.CopyStreamCancellable(entry.DataStream, destination, cancellationToken);
                        processed++;
                    }
                }

                if (onProgress != null)
                {
                    onProgress(current, totalEntries, entry.Name);
                }

                entry = tarReader.GetNextEntry();
            }

            FileOperationResult result = FileOperationResult.Ok(processed);
            return result;
        }

        /// <summary>
        /// Extracts TAR, TAR.GZ, TAR.BZ2, TAR.XZ archives using SharpCompress
        /// </summary>
        /// <param name="archivePath">Path to the archive file</param>
        /// <param name="destinationDir">Destination directory</param>
        /// <param name="onProgress">Progress callback</param>
        /// <returns>Operation result</returns>
        private FileOperationResult ExtractWithSharpCompress(string archivePath, string destinationDir, Action<int, int, string> onProgress, CancellationToken cancellationToken)
        {
            int processed = 0;

            // First pass: count entries
            int totalEntries = 0;
            using (FileStream countStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read))
            using (IReader countReader = ReaderFactory.OpenReader(countStream))
            {
                while (countReader.MoveToNextEntry())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    totalEntries++;
                }
            }

            // Second pass: extract
            using FileStream fileStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read);
            using IReader reader = ReaderFactory.OpenReader(fileStream);
            int current = 0;

            while (reader.MoveToNextEntry())
            {
                cancellationToken.ThrowIfCancellationRequested();
                current++;
                IEntry entry = reader.Entry;

                if (onProgress != null)
                {
                    onProgress(current, totalEntries, entry.Key);
                }

                if (entry.IsDirectory)
                {
                    string dirPath = Path.Combine(destinationDir, entry.Key);
                    if (this.IsPathInsideDirectory(dirPath, destinationDir))
                    {
                        Directory.CreateDirectory(dirPath);
                    }
                }
                else
                {
                    string destPath = Path.Combine(destinationDir, entry.Key);

                    // Security: prevent path traversal
                    if (this.IsPathInsideDirectory(destPath, destinationDir))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(destPath));
                        using Stream source = reader.OpenEntryStream();
                        using FileStream destination = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
                        this.CopyStreamCancellable(source, destination, cancellationToken);
                        processed++;
                    }
                }
            }

            FileOperationResult result = FileOperationResult.Ok(processed);
            return result;
        }

        #endregion

        #region Private Methods - Security

        /// <summary>
        /// Checks whether a target path resolves inside a destination directory
        /// </summary>
        /// <param name="targetPath">Target file or directory path</param>
        /// <param name="destinationDir">Expected parent directory</param>
        /// <returns>True if target path is inside destination directory</returns>
        private bool IsPathInsideDirectory(string targetPath, string destinationDir)
        {
            string fullTarget = Path.GetFullPath(targetPath);
            string fullDir = Path.GetFullPath(destinationDir);

            if (!fullDir.EndsWith(Path.DirectorySeparatorChar))
            {
                fullDir += Path.DirectorySeparatorChar;
            }

            bool result = fullTarget.StartsWith(fullDir, this.GetPathComparison());
            return result;
        }

        /// <summary>
        /// Gets the appropriate path comparison for the current platform
        /// </summary>
        /// <returns>String comparison for filesystem paths</returns>
        private StringComparison GetPathComparison()
        {
            StringComparison result = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return result;
        }

        #endregion

        #region Private Methods - Creation

        /// <summary>
        /// Creates a ZIP archive using native System.IO.Compression
        /// </summary>
        /// <param name="outputPath">Output ZIP file path</param>
        /// <param name="sourcePaths">Source file/directory paths</param>
        /// <param name="totalFiles">Total file count for progress</param>
        /// <param name="onProgress">Progress callback</param>
        /// <returns>Operation result</returns>
        private FileOperationResult CreateZip(string outputPath, List<string> sourcePaths, int totalFiles, Action<int, int, string> onProgress, CancellationToken cancellationToken)
        {
            int processed = 0;
            using FileStream outStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
            using ZipArchive archive = new ZipArchive(outStream, ZipArchiveMode.Create);

            for (int i = 0; i < sourcePaths.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string source = sourcePaths[i];

                if (Directory.Exists(source))
                {
                    string baseName = Path.GetFileName(source);
                    this.AddDirectoryToZip(archive, source, baseName, ref processed, totalFiles, onProgress, cancellationToken);
                }
                else if (File.Exists(source))
                {
                    string entryName = Path.GetFileName(source);
                    this.AddFileToZip(archive, source, entryName, cancellationToken);
                    processed++;

                    if (onProgress != null)
                    {
                        onProgress(processed, totalFiles, entryName);
                    }
                }
            }

            FileOperationResult result = FileOperationResult.Ok(processed);
            return result;
        }

        /// <summary>
        /// Recursively adds a directory to a ZIP archive
        /// </summary>
        /// <param name="archive">ZIP archive being built</param>
        /// <param name="dirPath">Directory path to add</param>
        /// <param name="entryBase">Base path for archive entries</param>
        /// <param name="processed">Running file count</param>
        /// <param name="totalFiles">Total file count for progress</param>
        /// <param name="onProgress">Progress callback</param>
        private void AddDirectoryToZip(ZipArchive archive, string dirPath, string entryBase, ref int processed, int totalFiles, Action<int, int, string> onProgress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Add files in this directory
            DirectoryInfo dirInfo = new DirectoryInfo(dirPath);
            FileInfo[] files = dirInfo.GetFiles();
            DirectoryInfo[] subDirs = dirInfo.GetDirectories();

            if (files.Length == 0 && subDirs.Length == 0)
            {
                archive.CreateEntry(entryBase + "/");
            }

            for (int i = 0; i < files.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string entryName = entryBase + "/" + files[i].Name;
                this.AddFileToZip(archive, files[i].FullName, entryName, cancellationToken);
                processed++;

                if (onProgress != null)
                {
                    onProgress(processed, totalFiles, entryName);
                }
            }

            // Recurse into subdirectories
            for (int i = 0; i < subDirs.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string subBase = entryBase + "/" + subDirs[i].Name;
                this.AddDirectoryToZip(archive, subDirs[i].FullName, subBase, ref processed, totalFiles, onProgress, cancellationToken);
            }
        }

        /// <summary>
        /// Creates a TAR-based archive (tar, tar.gz, tar.bz2, tar.xz, tar.zst)
        /// </summary>
        /// <param name="outputPath">Output archive file path</param>
        /// <param name="sourcePaths">Source file/directory paths</param>
        /// <param name="format">Archive format</param>
        /// <param name="totalFiles">Total file count for progress</param>
        /// <param name="onProgress">Progress callback</param>
        /// <returns>Operation result</returns>
        private FileOperationResult CreateTarVariant(string outputPath, List<string> sourcePaths, ArchiveFormat format, int totalFiles, Action<int, int, string> onProgress, CancellationToken cancellationToken)
        {
            int processed = 0;
            using FileStream outStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);

            // Wrap in compression stream based on format
            using Stream compressionStream = this.WrapWithCompression(outStream, format);

            // Write tar entries
            using TarWriter tarWriter = new TarWriter(compressionStream, TarEntryFormat.Pax, leaveOpen: false);

            for (int i = 0; i < sourcePaths.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string source = sourcePaths[i];

                if (Directory.Exists(source))
                {
                    string baseName = Path.GetFileName(source);
                    this.AddDirectoryToTar(tarWriter, source, baseName, ref processed, totalFiles, onProgress, cancellationToken);
                }
                else if (File.Exists(source))
                {
                    string entryName = Path.GetFileName(source);
                    this.AddFileToTar(tarWriter, source, entryName, cancellationToken);
                    processed++;

                    if (onProgress != null)
                    {
                        onProgress(processed, totalFiles, entryName);
                    }
                }
            }

            FileOperationResult result = FileOperationResult.Ok(processed);
            return result;
        }

        /// <summary>
        /// Wraps a file stream with the appropriate compression stream
        /// </summary>
        /// <param name="outStream">Output file stream</param>
        /// <param name="format">Archive format determining compression type</param>
        /// <returns>Compression stream wrapping the output stream</returns>
        private Stream WrapWithCompression(FileStream outStream, ArchiveFormat format)
        {
            Stream result = outStream;

            if (format == ArchiveFormat.TarGz)
            {
                result = new GZipStream(outStream, CompressionLevel.Optimal, leaveOpen: false);
            }
            else if (format == ArchiveFormat.TarBz2)
            {
                result = SharpCompress.Compressors.BZip2.BZip2Stream.Create(outStream, SharpCompress.Compressors.CompressionMode.Compress, false, false);
            }
            else if (format == ArchiveFormat.TarXz)
            {
                result = new SharpCompress.Compressors.Xz.XZStream(outStream);
            }
            else if (format == ArchiveFormat.TarZst)
            {
                result = new ZstdSharp.CompressionStream(outStream, level: 3, leaveOpen: false);
            }

            return result;
        }

        /// <summary>
        /// Recursively adds a directory to a TAR archive
        /// </summary>
        /// <param name="tarWriter">TAR writer</param>
        /// <param name="dirPath">Directory path to add</param>
        /// <param name="entryBase">Base path for tar entries</param>
        /// <param name="processed">Running file count</param>
        /// <param name="totalFiles">Total file count for progress</param>
        /// <param name="onProgress">Progress callback</param>
        private void AddDirectoryToTar(TarWriter tarWriter, string dirPath, string entryBase, ref int processed, int totalFiles, Action<int, int, string> onProgress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Add files in this directory
            DirectoryInfo dirInfo = new DirectoryInfo(dirPath);
            FileInfo[] files = dirInfo.GetFiles();
            DirectoryInfo[] subDirs = dirInfo.GetDirectories();

            if (files.Length == 0 && subDirs.Length == 0)
            {
                tarWriter.WriteEntry(dirPath, entryBase);
            }

            for (int i = 0; i < files.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string entryName = entryBase + "/" + files[i].Name;
                this.AddFileToTar(tarWriter, files[i].FullName, entryName, cancellationToken);
                processed++;

                if (onProgress != null)
                {
                    onProgress(processed, totalFiles, entryName);
                }
            }

            // Recurse into subdirectories
            for (int i = 0; i < subDirs.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string subBase = entryBase + "/" + subDirs[i].Name;
                this.AddDirectoryToTar(tarWriter, subDirs[i].FullName, subBase, ref processed, totalFiles, onProgress, cancellationToken);
            }
        }

        #endregion

        #region Private Methods - Helpers

        /// <summary>
        /// Adds a ZIP file by copying it in cancellable blocks
        /// </summary>
        private void AddFileToZip(ZipArchive archive, string sourcePath, string entryName, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            using FileStream source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using Stream destination = entry.Open();
            this.CopyStreamCancellable(source, destination, cancellationToken);
        }

        /// <summary>
        /// Adds a TAR file through a stream that observes revocation during writes
        /// </summary>
        private void AddFileToTar(TarWriter tarWriter, string sourcePath, string entryName, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using FileStream source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using CancellationObservingStream cancellableSource = new CancellationObservingStream(source, cancellationToken);
            PaxTarEntry entry = new PaxTarEntry(TarEntryType.RegularFile, entryName);
            entry.DataStream = cancellableSource;
            entry.ModificationTime = File.GetLastWriteTimeUtc(sourcePath);
            tarWriter.WriteEntry(entry);
        }

        /// <summary>
        /// Copies a stream while observing revocation for large payloads
        /// </summary>
        private void CopyStreamCancellable(Stream source, Stream destination, CancellationToken cancellationToken)
        {
            source.CopyToAsync(destination, 128 * 1024, cancellationToken).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Read-only stream that checks a token before each read performed by TarWriter
        /// </summary>
        private sealed class CancellationObservingStream : Stream
        {
            private readonly Stream _inner;

            private readonly CancellationToken _cancellationToken;

            public CancellationObservingStream(Stream inner, CancellationToken cancellationToken)
            {
                this._inner = inner;
                this._cancellationToken = cancellationToken;
            }

            public override bool CanRead { get { return this._inner.CanRead; } }

            public override bool CanSeek { get { return this._inner.CanSeek; } }

            public override bool CanWrite { get { return false; } }

            public override long Length { get { return this._inner.Length; } }

            public override long Position
            {
                get { return this._inner.Position; }
                set { this._inner.Position = value; }
            }

            public override void Flush()
            {
                this._inner.Flush();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                this._cancellationToken.ThrowIfCancellationRequested();
                return this._inner.Read(buffer, offset, count);
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                this._cancellationToken.ThrowIfCancellationRequested();
                return this._inner.Seek(offset, origin);
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }
        }

        /// <summary>
        /// Validates source paths before archive creation starts
        /// </summary>
        /// <param name="outputPath">Archive output path</param>
        /// <param name="sourcePaths">Source paths to compress</param>
        /// <returns>Error message, or empty if valid</returns>
        private string ValidateArchiveSources(string outputPath, List<string> sourcePaths)
        {
            string result = "";
            string fullOutputPath = Path.GetFullPath(outputPath);

            for (int i = 0; i < sourcePaths.Count; i++)
            {
                string sourcePath = sourcePaths[i];

                if (!this._securityService.IsPathSafe(sourcePath))
                {
                    result = "Invalid source path: " + sourcePath;
                    break;
                }
                if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
                {
                    result = "Source not found: " + sourcePath;
                    break;
                }

                string fullSourcePath = Path.GetFullPath(sourcePath);
                bool samePath = string.Equals(Path.TrimEndingDirectorySeparator(fullSourcePath), Path.TrimEndingDirectorySeparator(fullOutputPath), this.GetPathComparison());

                if (samePath)
                {
                    result = "Archive output cannot overwrite a selected source: " + sourcePath;
                    break;
                }
                if (Directory.Exists(sourcePath) && this.IsPathInsideDirectory(fullOutputPath, fullSourcePath))
                {
                    result = "Archive output cannot be created inside a selected source directory: " + sourcePath;
                    break;
                }
            }

            return result;
        }

        /// <summary>
        /// Counts total files in a path (file = 1, directory = recursive count)
        /// </summary>
        /// <param name="path">File or directory path</param>
        /// <returns>Total file count</returns>
        private int CountFiles(string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = 0;

            if (File.Exists(path))
            {
                count = 1;
            }
            else if (Directory.Exists(path))
            {
                DirectoryInfo dirInfo = new DirectoryInfo(path);

                try
                {
                    FileInfo[] files = dirInfo.GetFiles();
                    count += files.Length;

                    DirectoryInfo[] subDirs = dirInfo.GetDirectories();
                    for (int i = 0; i < subDirs.Length; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        count += this.CountFiles(subDirs[i].FullName, cancellationToken);
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    // Skip inaccessible
                }
                catch (IOException)
                {
                    // Skip inaccessible
                }
            }

            return count;
        }

        #endregion
    }
}
