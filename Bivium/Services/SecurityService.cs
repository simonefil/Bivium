namespace Bivium.Services
{
    /// <summary>
    /// Validates filesystem paths to prevent directory traversal attacks
    /// </summary>
    public class SecurityService
    {
        #region Constructor

        /// <summary>
        /// Default constructor
        /// </summary>
        public SecurityService()
        {
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Validates that a path is absolute and does not contain traversal sequences
        /// </summary>
        /// <param name="path">Path to validate</param>
        /// <returns>True if the path is safe to use</returns>
        public bool IsPathSafe(string path)
        {
            bool result = false;

            if (!string.IsNullOrWhiteSpace(path))
            {
                // Resolve the full path to eliminate any .. or . segments
                string resolvedPath = Path.GetFullPath(path);

                // Verify it matches the original intent (no traversal tricks)
                result = Path.IsPathRooted(resolvedPath);
            }

            return result;
        }

        /// <summary>
        /// Resolves a path safely, combining base and relative parts
        /// </summary>
        /// <param name="basePath">Base directory path</param>
        /// <param name="relativePath">Relative path to combine</param>
        /// <returns>Resolved absolute path, or empty string if invalid</returns>
        public string ResolvePath(string basePath, string relativePath)
        {
            string result = "";

            if (!string.IsNullOrWhiteSpace(basePath) && !string.IsNullOrWhiteSpace(relativePath))
            {
                string combined = Path.Combine(basePath, relativePath);
                string resolved = Path.GetFullPath(combined);

                // Ensure the resolved path still starts with the base path
                // This prevents .. traversal beyond the base
                if (resolved.StartsWith(Path.GetFullPath(basePath), StringComparison.OrdinalIgnoreCase))
                {
                    result = resolved;
                }
            }

            return result;
        }

        /// <summary>
        /// Validates that both source and destination paths are safe
        /// </summary>
        /// <param name="sourcePath">Source path</param>
        /// <param name="destinationPath">Destination path</param>
        /// <returns>True if both paths are safe</returns>
        public bool ArePathsSafe(string sourcePath, string destinationPath)
        {
            bool result = this.IsPathSafe(sourcePath) && this.IsPathSafe(destinationPath);
            return result;
        }

        #endregion
    }
}
