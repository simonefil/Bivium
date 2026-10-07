namespace Bivium.Models
{
    /// <summary>
    /// Shared formatting of byte sizes for the UI
    /// </summary>
    public static class ByteSizeFormatter
    {
        #region Metodi pubblici

        /// <summary>
        /// Formats a byte count as B, KB, MB or GB with one decimal
        /// </summary>
        /// <param name="bytes">Size in bytes</param>
        /// <returns>Human-readable size</returns>
        public static string Format(long bytes)
        {
            string result;

            if (bytes < 1024)
            {
                result = bytes + " B";
            }
            else if (bytes < 1024 * 1024)
            {
                result = (bytes / 1024.0).ToString("F1") + " KB";
            }
            else if (bytes < 1024L * 1024 * 1024)
            {
                result = (bytes / (1024.0 * 1024.0)).ToString("F1") + " MB";
            }
            else
            {
                result = (bytes / (1024.0 * 1024.0 * 1024.0)).ToString("F1") + " GB";
            }

            return result;
        }

        #endregion
    }
}
