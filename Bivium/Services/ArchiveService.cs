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
        /// <returns>Operation result</returns>
        public FileOperationResult ExtractArchive(string archivePath, string destinationDir, Action<int, int, string> onProgress)
        {
            FileOperationResult result = new FileOperationResult();

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
                    result = this.ExtractZip(archivePath, destinationDir, onProgress);
                }
                // TAR.ZST: native tar + ZstdSharp
                else if (lowerPath.EndsWith(".tar.zst") || lowerPath.EndsWith(".tzst"))
                {
                    result = this.ExtractTarZst(archivePath, destinationDir, onProgress);
                }
                // TAR, TAR.GZ, TAR.BZ2, TAR.XZ: SharpCompress handles all
                else
                {
                    result = this.ExtractWithSharpCompress(archivePath, destinationDir, onProgress);
                }
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
        /// <returns>Operation result</returns>
        public FileOperationResult CreateArchive(string outputPath, List<string> sourcePaths, ArchiveFormat format, Action<int, int, string> onProgress)
        {
            FileOperationResult result = new FileOperationResult();

            if (!this._securityService.IsPathSafe(outputPath))
            {
                result = FileOperationResult.Fail("Invalid output path");
                return result;
            }

            try
            {
                // Count total files for progress
                int totalFiles = 0;
                for (int i = 0; i < sourcePaths.Count; i++)
                {
                    totalFiles += this.CountFiles(sourcePaths[i]);
                }

                if (format == ArchiveFormat.Zip)
                {
                    result = this.CreateZip(outputPath, sourcePaths, totalFiles, onProgress);
                }
                else
                {
                    result = this.CreateTarVariant(outputPath, sourcePaths, format, totalFiles, onProgress);
                }
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
        private FileOperationResult ExtractZip(string archivePath, string destinationDir, Action<int, int, string> onProgress)
        {
            int processed = 0;
            ZipArchive archive = ZipFile.OpenRead(archivePath);
            int totalEntries = archive.Entries.Count;

            for (int i = 0; i < archive.Entries.Count; i++)
            {
                ZipArchiveEntry entry = archive.Entries[i];
                string destPath = Path.Combine(destinationDir, entry.FullName);

                // Security: prevent path traversal
                string fullDest = Path.GetFullPath(destPath);
                string fullDir = Path.GetFullPath(destinationDir);
                if (!fullDest.StartsWith(fullDir))
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
                    entry.ExtractToFile(destPath, true);
                    processed++;
                }

                if (onProgress != null)
                {
                    onProgress(i + 1, totalEntries, entry.FullName);
                }
            }

            archive.Dispose();

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
        private FileOperationResult ExtractTarZst(string archivePath, string destinationDir, Action<int, int, string> onProgress)
        {
            int processed = 0;

            // First pass: count entries
            int totalEntries = 0;
            FileStream countStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read);
            ZstdSharp.DecompressionStream countDecomp = new ZstdSharp.DecompressionStream(countStream);
            TarReader countReader = new TarReader(countDecomp);

            while (countReader.GetNextEntry() != null)
            {
                totalEntries++;
            }

            countReader.Dispose();
            countDecomp.Dispose();
            countStream.Dispose();

            // Second pass: extract
            FileStream fileStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read);
            ZstdSharp.DecompressionStream decompStream = new ZstdSharp.DecompressionStream(fileStream);
            TarReader tarReader = new TarReader(decompStream);
            int current = 0;

            TarEntry entry = tarReader.GetNextEntry();
            while (entry != null)
            {
                current++;
                string destPath = Path.Combine(destinationDir, entry.Name);

                // Security: prevent path traversal
                string fullDest = Path.GetFullPath(destPath);
                string fullDir = Path.GetFullPath(destinationDir);

                if (fullDest.StartsWith(fullDir))
                {
                    if (entry.EntryType == TarEntryType.Directory)
                    {
                        Directory.CreateDirectory(destPath);
                    }
                    else if (entry.EntryType == TarEntryType.RegularFile)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(destPath));
                        entry.ExtractToFile(destPath, true);
                        processed++;
                    }
                }

                if (onProgress != null)
                {
                    onProgress(current, totalEntries, entry.Name);
                }

                entry = tarReader.GetNextEntry();
            }

            tarReader.Dispose();
            decompStream.Dispose();
            fileStream.Dispose();

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
        private FileOperationResult ExtractWithSharpCompress(string archivePath, string destinationDir, Action<int, int, string> onProgress)
        {
            int processed = 0;

            // First pass: count entries
            int totalEntries = 0;
            FileStream countStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read);
            IReader countReader = ReaderFactory.OpenReader(countStream);
            while (countReader.MoveToNextEntry())
            {
                totalEntries++;
            }
            countReader.Dispose();
            countStream.Dispose();

            // Second pass: extract
            FileStream fileStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read);
            IReader reader = ReaderFactory.OpenReader(fileStream);
            int current = 0;

            while (reader.MoveToNextEntry())
            {
                current++;
                IEntry entry = reader.Entry;

                if (onProgress != null)
                {
                    onProgress(current, totalEntries, entry.Key);
                }

                if (entry.IsDirectory)
                {
                    string dirPath = Path.Combine(destinationDir, entry.Key);
                    Directory.CreateDirectory(dirPath);
                }
                else
                {
                    string destPath = Path.Combine(destinationDir, entry.Key);

                    // Security: prevent path traversal
                    string fullDest = Path.GetFullPath(destPath);
                    string fullDir = Path.GetFullPath(destinationDir);

                    if (fullDest.StartsWith(fullDir))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(destPath));
                        reader.WriteEntryToFile(destPath, new ExtractionOptions() { Overwrite = true });
                        processed++;
                    }
                }
            }

            reader.Dispose();
            fileStream.Dispose();

            FileOperationResult result = FileOperationResult.Ok(processed);
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
        private FileOperationResult CreateZip(string outputPath, List<string> sourcePaths, int totalFiles, Action<int, int, string> onProgress)
        {
            int processed = 0;
            FileStream outStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
            ZipArchive archive = new ZipArchive(outStream, ZipArchiveMode.Create);

            for (int i = 0; i < sourcePaths.Count; i++)
            {
                string source = sourcePaths[i];

                if (Directory.Exists(source))
                {
                    string baseName = Path.GetFileName(source);
                    this.AddDirectoryToZip(archive, source, baseName, ref processed, totalFiles, onProgress);
                }
                else if (File.Exists(source))
                {
                    string entryName = Path.GetFileName(source);
                    archive.CreateEntryFromFile(source, entryName, CompressionLevel.Optimal);
                    processed++;

                    if (onProgress != null)
                    {
                        onProgress(processed, totalFiles, entryName);
                    }
                }
            }

            archive.Dispose();
            outStream.Dispose();

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
        private void AddDirectoryToZip(ZipArchive archive, string dirPath, string entryBase, ref int processed, int totalFiles, Action<int, int, string> onProgress)
        {
            // Add files in this directory
            DirectoryInfo dirInfo = new DirectoryInfo(dirPath);
            FileInfo[] files = dirInfo.GetFiles();

            for (int i = 0; i < files.Length; i++)
            {
                string entryName = entryBase + "/" + files[i].Name;
                archive.CreateEntryFromFile(files[i].FullName, entryName, CompressionLevel.Optimal);
                processed++;

                if (onProgress != null)
                {
                    onProgress(processed, totalFiles, entryName);
                }
            }

            // Recurse into subdirectories
            DirectoryInfo[] subDirs = dirInfo.GetDirectories();

            for (int i = 0; i < subDirs.Length; i++)
            {
                string subBase = entryBase + "/" + subDirs[i].Name;
                this.AddDirectoryToZip(archive, subDirs[i].FullName, subBase, ref processed, totalFiles, onProgress);
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
        private FileOperationResult CreateTarVariant(string outputPath, List<string> sourcePaths, ArchiveFormat format, int totalFiles, Action<int, int, string> onProgress)
        {
            int processed = 0;
            FileStream outStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);

            // Wrap in compression stream based on format
            Stream compressionStream = this.WrapWithCompression(outStream, format);

            // Write tar entries
            TarWriter tarWriter = new TarWriter(compressionStream, TarEntryFormat.Pax, leaveOpen: false);

            for (int i = 0; i < sourcePaths.Count; i++)
            {
                string source = sourcePaths[i];

                if (Directory.Exists(source))
                {
                    string baseName = Path.GetFileName(source);
                    this.AddDirectoryToTar(tarWriter, source, baseName, ref processed, totalFiles, onProgress);
                }
                else if (File.Exists(source))
                {
                    string entryName = Path.GetFileName(source);
                    tarWriter.WriteEntry(source, entryName);
                    processed++;

                    if (onProgress != null)
                    {
                        onProgress(processed, totalFiles, entryName);
                    }
                }
            }

            tarWriter.Dispose();

            // Compression stream is disposed by TarWriter (leaveOpen: false)
            // But if compressionStream != outStream, outStream may need disposal
            if (compressionStream != outStream)
            {
                outStream.Dispose();
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
        private void AddDirectoryToTar(TarWriter tarWriter, string dirPath, string entryBase, ref int processed, int totalFiles, Action<int, int, string> onProgress)
        {
            // Add files in this directory
            DirectoryInfo dirInfo = new DirectoryInfo(dirPath);
            FileInfo[] files = dirInfo.GetFiles();

            for (int i = 0; i < files.Length; i++)
            {
                string entryName = entryBase + "/" + files[i].Name;
                tarWriter.WriteEntry(files[i].FullName, entryName);
                processed++;

                if (onProgress != null)
                {
                    onProgress(processed, totalFiles, entryName);
                }
            }

            // Recurse into subdirectories
            DirectoryInfo[] subDirs = dirInfo.GetDirectories();

            for (int i = 0; i < subDirs.Length; i++)
            {
                string subBase = entryBase + "/" + subDirs[i].Name;
                this.AddDirectoryToTar(tarWriter, subDirs[i].FullName, subBase, ref processed, totalFiles, onProgress);
            }
        }

        #endregion

        #region Private Methods - Helpers

        /// <summary>
        /// Counts total files in a path (file = 1, directory = recursive count)
        /// </summary>
        /// <param name="path">File or directory path</param>
        /// <returns>Total file count</returns>
        private int CountFiles(string path)
        {
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
                        count += this.CountFiles(subDirs[i].FullName);
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    // Skip inaccessible
                }
            }

            return count;
        }

        #endregion
    }
}
