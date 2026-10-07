namespace Bivium.Models
{
    /// <summary>
    /// Formattazione condivisa delle dimensioni in byte per la UI
    /// </summary>
    public static class ByteSizeFormatter
    {
        #region Metodi pubblici

        /// <summary>
        /// Formatta un numero di byte in B, KB, MB o GB con un decimale
        /// </summary>
        /// <param name="bytes">Dimensione in byte</param>
        /// <returns>Dimensione leggibile</returns>
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
