using Bivium.Models;

namespace Bivium.Services
{
    /// <summary>
    /// Interface for file permission and ownership operations
    /// </summary>
    public interface IPermissionService
    {
        /// <summary>
        /// Gets the permissions for a file or directory
        /// </summary>
        /// <param name="path">File or directory path</param>
        /// <returns>Permission model</returns>
        PermissionModel GetPermissions(string path);

        /// <summary>
        /// Sets the permissions for a file or directory
        /// </summary>
        /// <param name="path">File or directory path</param>
        /// <param name="model">Permission model to apply</param>
        /// <param name="recursive">If true, apply recursively to directory contents</param>
        /// <returns>Operation result</returns>
        FileOperationResult SetPermissions(string path, PermissionModel model, bool recursive);

        /// <summary>
        /// Sets the owner of a file or directory
        /// </summary>
        /// <param name="path">File or directory path</param>
        /// <param name="owner">New owner name</param>
        /// <param name="group">New group name (Linux only, ignored on Windows)</param>
        /// <param name="recursive">If true, apply recursively to directory contents</param>
        /// <returns>Operation result</returns>
        FileOperationResult SetOwner(string path, string owner, string group, bool recursive);
    }
}
