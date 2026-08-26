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
        /// <param name="cancellationToken">Cancellation token for lease revocation</param>
        /// <returns>Operation result</returns>
        FileOperationResult SetPermissions(string path, PermissionModel model, bool recursive, CancellationToken cancellationToken = default);

        /// <summary>
        /// Applies configured ownership and permissions to a newly created entry
        /// </summary>
        /// <param name="path">Created file or directory path</param>
        /// <param name="isDirectory">Whether the created entry is a directory</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Operation result</returns>
        FileOperationResult ApplyDefaultCreationPermissions(string path, bool isDirectory, CancellationToken cancellationToken = default);

        /// <summary>
        /// Sets the owner of a file or directory
        /// </summary>
        /// <param name="path">File or directory path</param>
        /// <param name="owner">New owner name</param>
        /// <param name="group">New group name (Linux only, ignored on Windows)</param>
        /// <param name="recursive">If true, apply recursively to directory contents</param>
        /// <param name="cancellationToken">Cancellation token for lease revocation</param>
        /// <returns>Operation result</returns>
        FileOperationResult SetOwner(string path, string owner, string group, bool recursive, CancellationToken cancellationToken = default);
    }
}
