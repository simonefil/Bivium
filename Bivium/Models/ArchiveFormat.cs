namespace Bivium.Models
{
    /// <summary>
    /// Supported archive formats for compression/decompression
    /// </summary>
    public enum ArchiveFormat
    {
        /// <summary>
        /// ZIP archive
        /// </summary>
        Zip,

        /// <summary>
        /// TAR archive (no compression)
        /// </summary>
        Tar,

        /// <summary>
        /// TAR archive with GZip compression
        /// </summary>
        TarGz,

        /// <summary>
        /// TAR archive with BZip2 compression
        /// </summary>
        TarBz2,

        /// <summary>
        /// TAR archive with XZ compression
        /// </summary>
        TarXz,

        /// <summary>
        /// TAR archive with Zstandard compression
        /// </summary>
        TarZst
    }
}
