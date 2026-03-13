namespace Bivium.Models
{
    /// <summary>
    /// Result of a file operation (copy, move, delete, etc.)
    /// </summary>
    public class FileOperationResult
    {
        #region Properties

        /// <summary>
        /// True if the operation completed successfully
        /// </summary>
        public bool Success { get; set; } = false;

        /// <summary>
        /// Error message if the operation failed
        /// </summary>
        public string ErrorMessage { get; set; } = "";

        /// <summary>
        /// Number of files/directories successfully processed
        /// </summary>
        public int FilesProcessed { get; set; } = 0;

        /// <summary>
        /// Number of files/directories that failed
        /// </summary>
        public int FilesFailed { get; set; } = 0;

        #endregion

        #region Constructor

        /// <summary>
        /// Default constructor
        /// </summary>
        public FileOperationResult()
        {
        }

        #endregion

        #region Static Methods

        /// <summary>
        /// Creates a successful result
        /// </summary>
        /// <param name="filesProcessed">Number of files processed</param>
        /// <returns>Successful result</returns>
        public static FileOperationResult Ok(int filesProcessed)
        {
            FileOperationResult result = new FileOperationResult();
            result.Success = true;
            result.FilesProcessed = filesProcessed;
            return result;
        }

        /// <summary>
        /// Creates a failed result
        /// </summary>
        /// <param name="errorMessage">Error description</param>
        /// <returns>Failed result</returns>
        public static FileOperationResult Fail(string errorMessage)
        {
            FileOperationResult result = new FileOperationResult();
            result.Success = false;
            result.ErrorMessage = errorMessage;
            return result;
        }

        #endregion
    }
}
