namespace Bivium.Models
{
    /// <summary>
    /// Result of reading text content from a file
    /// </summary>
    public class FileTextResult : FileOperationResult
    {
        #region Properties

        /// <summary>
        /// Text content read from the file
        /// </summary>
        public string Content { get; set; } = "";

        #endregion

        #region Static Methods

        /// <summary>
        /// Creates a successful text read result
        /// </summary>
        /// <param name="content">File content</param>
        /// <returns>Successful result</returns>
        public static FileTextResult Ok(string content)
        {
            FileTextResult result = new FileTextResult();
            result.Success = true;
            result.FilesProcessed = 1;
            result.Content = content;
            return result;
        }

        /// <summary>
        /// Creates a failed text read result
        /// </summary>
        /// <param name="errorMessage">Error description</param>
        /// <returns>Failed result</returns>
        public new static FileTextResult Fail(string errorMessage)
        {
            FileTextResult result = new FileTextResult();
            result.Success = false;
            result.ErrorMessage = errorMessage;
            return result;
        }

        #endregion
    }
}
