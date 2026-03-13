namespace Bivium.Models
{
    /// <summary>
    /// Internal clipboard for copy/cut operations
    /// </summary>
    public class ClipboardState
    {
        #region Properties

        /// <summary>
        /// List of source file/directory paths in the clipboard
        /// </summary>
        public List<string> Paths { get; set; } = new List<string>();

        /// <summary>
        /// True if the operation is Cut (move), false if Copy
        /// </summary>
        public bool IsCut { get; set; } = false;

        #endregion

        #region Constructor

        /// <summary>
        /// Default constructor
        /// </summary>
        public ClipboardState()
        {
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Returns true if the clipboard has entries
        /// </summary>
        /// <returns>True if not empty</returns>
        public bool HasEntries()
        {
            return this.Paths.Count > 0;
        }

        /// <summary>
        /// Clears the clipboard
        /// </summary>
        public void Clear()
        {
            this.Paths.Clear();
            this.IsCut = false;
        }

        #endregion
    }
}
