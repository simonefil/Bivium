namespace Bivium.Models
{
    /// <summary>
    /// Cross-platform permission representation
    /// </summary>
    public class PermissionModel
    {
        #region Properties - Common

        /// <summary>
        /// File or directory owner name
        /// </summary>
        public string Owner { get; set; } = "";

        /// <summary>
        /// Group name (Linux only, empty on Windows)
        /// </summary>
        public string Group { get; set; } = "";

        /// <summary>
        /// True if running on Linux/Unix
        /// </summary>
        public bool IsUnix { get; set; } = false;

        #endregion

        #region Properties - Unix (rwx)

        /// <summary>
        /// Owner read permission
        /// </summary>
        public bool OwnerRead { get; set; } = false;

        /// <summary>
        /// Owner write permission
        /// </summary>
        public bool OwnerWrite { get; set; } = false;

        /// <summary>
        /// Owner execute permission
        /// </summary>
        public bool OwnerExecute { get; set; } = false;

        /// <summary>
        /// Group read permission
        /// </summary>
        public bool GroupRead { get; set; } = false;

        /// <summary>
        /// Group write permission
        /// </summary>
        public bool GroupWrite { get; set; } = false;

        /// <summary>
        /// Group execute permission
        /// </summary>
        public bool GroupExecute { get; set; } = false;

        /// <summary>
        /// Others read permission
        /// </summary>
        public bool OthersRead { get; set; } = false;

        /// <summary>
        /// Others write permission
        /// </summary>
        public bool OthersWrite { get; set; } = false;

        /// <summary>
        /// Others execute permission
        /// </summary>
        public bool OthersExecute { get; set; } = false;

        #endregion

        #region Properties - Windows attributes

        /// <summary>
        /// Read-only attribute (Windows)
        /// </summary>
        public bool WinReadOnly { get; set; } = false;

        /// <summary>
        /// Hidden attribute (Windows)
        /// </summary>
        public bool WinHidden { get; set; } = false;

        /// <summary>
        /// System attribute (Windows)
        /// </summary>
        public bool WinSystem { get; set; } = false;

        /// <summary>
        /// Archive attribute (Windows)
        /// </summary>
        public bool WinArchive { get; set; } = false;

        #endregion

        #region Constructor

        /// <summary>
        /// Default constructor
        /// </summary>
        public PermissionModel()
        {
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Returns the permission as a formatted string
        /// </summary>
        /// <returns>Formatted permission string</returns>
        public string ToDisplayString()
        {
            string result = "";

            if (this.IsUnix)
            {
                // Unix style: rwxrwxrwx
                result += this.OwnerRead ? "r" : "-";
                result += this.OwnerWrite ? "w" : "-";
                result += this.OwnerExecute ? "x" : "-";
                result += this.GroupRead ? "r" : "-";
                result += this.GroupWrite ? "w" : "-";
                result += this.GroupExecute ? "x" : "-";
                result += this.OthersRead ? "r" : "-";
                result += this.OthersWrite ? "w" : "-";
                result += this.OthersExecute ? "x" : "-";
            }
            else
            {
                // Windows style: RHSA
                result += this.WinReadOnly ? "R" : "-";
                result += this.WinHidden ? "H" : "-";
                result += this.WinSystem ? "S" : "-";
                result += this.WinArchive ? "A" : "-";
            }

            return result;
        }

        #endregion
    }
}
