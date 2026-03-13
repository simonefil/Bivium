using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Bivium.Models;

namespace Bivium.Services
{
    /// <summary>
    /// Cross-platform file permission and ownership management
    /// </summary>
    public class PermissionService : IPermissionService
    {
        #region Class Variables

        /// <summary>
        /// Security service for path validation
        /// </summary>
        private readonly SecurityService _securityService;

        /// <summary>
        /// True if running on Linux/Unix
        /// </summary>
        private readonly bool _isUnix;

        #endregion

        #region Constructor

        /// <summary>
        /// Creates a new PermissionService
        /// </summary>
        /// <param name="securityService">Security service instance</param>
        public PermissionService(SecurityService securityService)
        {
            this._securityService = securityService;
            this._isUnix = RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Gets the permissions for a file or directory
        /// </summary>
        /// <param name="path">File or directory path</param>
        /// <returns>Permission model</returns>
        public PermissionModel GetPermissions(string path)
        {
            PermissionModel result = new PermissionModel();
            result.IsUnix = this._isUnix;

            if (this._securityService.IsPathSafe(path))
            {
                if (this._isUnix)
                {
                    this.GetUnixPermissions(path, result);
                }
                else
                {
                    this.GetWindowsPermissions(path, result);
                }
            }

            return result;
        }

        /// <summary>
        /// Sets the permissions for a file or directory
        /// </summary>
        /// <param name="path">File or directory path</param>
        /// <param name="model">Permission model to apply</param>
        /// <param name="recursive">If true, apply recursively to directory contents</param>
        /// <returns>Operation result</returns>
        public FileOperationResult SetPermissions(string path, PermissionModel model, bool recursive)
        {
            FileOperationResult result = new FileOperationResult();

            if (!this._securityService.IsPathSafe(path))
            {
                result = FileOperationResult.Fail("Invalid path: " + path);
            }
            else
            {
                try
                {
                    if (this._isUnix)
                    {
                        result = this.SetUnixPermissions(path, model, recursive);
                    }
                    else
                    {
                        result = this.SetWindowsPermissions(path, model, recursive);
                    }
                }
                catch (UnauthorizedAccessException ex)
                {
                    result = FileOperationResult.Fail("Access denied: " + ex.Message);
                }
                catch (IOException ex)
                {
                    result = FileOperationResult.Fail("I/O error: " + ex.Message);
                }
            }

            return result;
        }

        /// <summary>
        /// Sets the owner of a file or directory
        /// </summary>
        /// <param name="path">File or directory path</param>
        /// <param name="owner">New owner name</param>
        /// <param name="group">New group name (Linux only, ignored on Windows)</param>
        /// <param name="recursive">If true, apply recursively to directory contents</param>
        /// <returns>Operation result</returns>
        public FileOperationResult SetOwner(string path, string owner, string group, bool recursive)
        {
            FileOperationResult result = new FileOperationResult();

            if (!this._securityService.IsPathSafe(path))
            {
                result = FileOperationResult.Fail("Invalid path: " + path);
            }
            else
            {
                try
                {
                    if (this._isUnix)
                    {
                        result = this.SetUnixOwner(path, owner, group, recursive);
                    }
                    else
                    {
                        result = this.SetWindowsOwner(path, owner);
                    }
                }
                catch (UnauthorizedAccessException ex)
                {
                    result = FileOperationResult.Fail("Access denied: " + ex.Message);
                }
                catch (IOException ex)
                {
                    result = FileOperationResult.Fail("I/O error: " + ex.Message);
                }
            }

            return result;
        }

        #endregion

        #region Private Methods - Unix

        /// <summary>
        /// Reads Unix permissions via stat command
        /// </summary>
        /// <param name="path">File or directory path</param>
        /// <param name="model">Permission model to populate</param>
        private void GetUnixPermissions(string path, PermissionModel model)
        {
            // Use stat to get permissions and owner
            string statOutput = this.RunCommand("stat", "-c \"%a %U %G\" \"" + path + "\"");

            if (!string.IsNullOrEmpty(statOutput))
            {
                // Remove quotes if present
                statOutput = statOutput.Trim().Trim('"');
                string[] parts = statOutput.Split(' ');

                if (parts.Length >= 3)
                {
                    // Parse octal permission (e.g. 755)
                    string octal = parts[0];
                    if (octal.Length >= 3)
                    {
                        int ownerBits = octal[octal.Length - 3] - '0';
                        int groupBits = octal[octal.Length - 2] - '0';
                        int otherBits = octal[octal.Length - 1] - '0';

                        model.OwnerRead = (ownerBits & 4) != 0;
                        model.OwnerWrite = (ownerBits & 2) != 0;
                        model.OwnerExecute = (ownerBits & 1) != 0;
                        model.GroupRead = (groupBits & 4) != 0;
                        model.GroupWrite = (groupBits & 2) != 0;
                        model.GroupExecute = (groupBits & 1) != 0;
                        model.OthersRead = (otherBits & 4) != 0;
                        model.OthersWrite = (otherBits & 2) != 0;
                        model.OthersExecute = (otherBits & 1) != 0;
                    }

                    model.Owner = parts[1];
                    model.Group = parts[2];
                }
            }
        }

        /// <summary>
        /// Sets Unix permissions via chmod command
        /// </summary>
        /// <param name="path">File or directory path</param>
        /// <param name="model">Permission model to apply</param>
        /// <param name="recursive">If true, apply recursively</param>
        /// <returns>Operation result</returns>
        private FileOperationResult SetUnixPermissions(string path, PermissionModel model, bool recursive)
        {
            // Build octal permission string
            int ownerBits = (model.OwnerRead ? 4 : 0) + (model.OwnerWrite ? 2 : 0) + (model.OwnerExecute ? 1 : 0);
            int groupBits = (model.GroupRead ? 4 : 0) + (model.GroupWrite ? 2 : 0) + (model.GroupExecute ? 1 : 0);
            int otherBits = (model.OthersRead ? 4 : 0) + (model.OthersWrite ? 2 : 0) + (model.OthersExecute ? 1 : 0);
            string octal = ownerBits.ToString() + groupBits.ToString() + otherBits.ToString();

            string recursiveFlag = recursive ? "-R " : "";
            string output = this.RunCommand("chmod", recursiveFlag + octal + " \"" + path + "\"");

            FileOperationResult result = FileOperationResult.Ok(1);
            return result;
        }

        /// <summary>
        /// Sets Unix owner via chown command
        /// </summary>
        /// <param name="path">File or directory path</param>
        /// <param name="owner">Owner name</param>
        /// <param name="group">Group name</param>
        /// <param name="recursive">If true, apply recursively</param>
        /// <returns>Operation result</returns>
        private FileOperationResult SetUnixOwner(string path, string owner, string group, bool recursive)
        {
            string ownerGroup = owner;
            if (!string.IsNullOrEmpty(group))
            {
                ownerGroup = owner + ":" + group;
            }

            string recursiveFlag = recursive ? "-R " : "";
            string output = this.RunCommand("chown", recursiveFlag + ownerGroup + " \"" + path + "\"");

            FileOperationResult result = FileOperationResult.Ok(1);
            return result;
        }

        #endregion

        #region Private Methods - Windows

        /// <summary>
        /// Reads Windows file attributes and owner
        /// </summary>
        /// <param name="path">File or directory path</param>
        /// <param name="model">Permission model to populate</param>
        private void GetWindowsPermissions(string path, PermissionModel model)
        {
            FileAttributes attrs = File.GetAttributes(path);

            model.WinReadOnly = (attrs & FileAttributes.ReadOnly) == FileAttributes.ReadOnly;
            model.WinHidden = (attrs & FileAttributes.Hidden) == FileAttributes.Hidden;
            model.WinSystem = (attrs & FileAttributes.System) == FileAttributes.System;
            model.WinArchive = (attrs & FileAttributes.Archive) == FileAttributes.Archive;

            // Get owner
            try
            {
                if (Directory.Exists(path))
                {
                    DirectoryInfo dirInfo = new DirectoryInfo(path);
                    DirectorySecurity security = dirInfo.GetAccessControl();
                    IdentityReference ownerIdentity = security.GetOwner(typeof(NTAccount));
                    model.Owner = ownerIdentity.Value;
                }
                else
                {
                    FileInfo fileInfo = new FileInfo(path);
                    FileSecurity security = fileInfo.GetAccessControl();
                    IdentityReference ownerIdentity = security.GetOwner(typeof(NTAccount));
                    model.Owner = ownerIdentity.Value;
                }
            }
            catch (UnauthorizedAccessException)
            {
                model.Owner = "(access denied)";
            }
        }

        /// <summary>
        /// Sets Windows file attributes
        /// </summary>
        /// <param name="path">File or directory path</param>
        /// <param name="model">Permission model to apply</param>
        /// <param name="recursive">If true, apply recursively</param>
        /// <returns>Operation result</returns>
        private FileOperationResult SetWindowsPermissions(string path, PermissionModel model, bool recursive)
        {
            this.ApplyWindowsAttributes(path, model);

            if (recursive && Directory.Exists(path))
            {
                this.SetWindowsPermissionsRecursive(path, model);
            }

            FileOperationResult result = FileOperationResult.Ok(1);
            return result;
        }

        /// <summary>
        /// Applies Windows attributes to a single entry
        /// </summary>
        /// <param name="path">File or directory path</param>
        /// <param name="model">Permission model</param>
        private void ApplyWindowsAttributes(string path, PermissionModel model)
        {
            FileAttributes current = File.GetAttributes(path);

            // Start with current attributes, then toggle the ones we manage
            FileAttributes newAttrs = current;

            // ReadOnly
            if (model.WinReadOnly)
            {
                newAttrs = newAttrs | FileAttributes.ReadOnly;
            }
            else
            {
                newAttrs = newAttrs & ~FileAttributes.ReadOnly;
            }

            // Hidden
            if (model.WinHidden)
            {
                newAttrs = newAttrs | FileAttributes.Hidden;
            }
            else
            {
                newAttrs = newAttrs & ~FileAttributes.Hidden;
            }

            // System
            if (model.WinSystem)
            {
                newAttrs = newAttrs | FileAttributes.System;
            }
            else
            {
                newAttrs = newAttrs & ~FileAttributes.System;
            }

            // Archive
            if (model.WinArchive)
            {
                newAttrs = newAttrs | FileAttributes.Archive;
            }
            else
            {
                newAttrs = newAttrs & ~FileAttributes.Archive;
            }

            File.SetAttributes(path, newAttrs);
        }

        /// <summary>
        /// Recursively applies Windows attributes to all contents
        /// </summary>
        /// <param name="dirPath">Directory path</param>
        /// <param name="model">Permission model</param>
        private void SetWindowsPermissionsRecursive(string dirPath, PermissionModel model)
        {
            DirectoryInfo dirInfo = new DirectoryInfo(dirPath);

            // Apply to files
            FileInfo[] files = dirInfo.GetFiles();
            for (int i = 0; i < files.Length; i++)
            {
                this.ApplyWindowsAttributes(files[i].FullName, model);
            }

            // Apply to subdirectories recursively
            DirectoryInfo[] subDirs = dirInfo.GetDirectories();
            for (int i = 0; i < subDirs.Length; i++)
            {
                this.ApplyWindowsAttributes(subDirs[i].FullName, model);
                this.SetWindowsPermissionsRecursive(subDirs[i].FullName, model);
            }
        }

        /// <summary>
        /// Sets Windows file owner
        /// </summary>
        /// <param name="path">File or directory path</param>
        /// <param name="owner">Owner in DOMAIN\User format</param>
        /// <returns>Operation result</returns>
        private FileOperationResult SetWindowsOwner(string path, string owner)
        {
            FileOperationResult result = new FileOperationResult();

            try
            {
                NTAccount account = new NTAccount(owner);

                if (Directory.Exists(path))
                {
                    DirectoryInfo dirInfo = new DirectoryInfo(path);
                    DirectorySecurity security = dirInfo.GetAccessControl();
                    security.SetOwner(account);
                    dirInfo.SetAccessControl(security);
                }
                else
                {
                    FileInfo fileInfo = new FileInfo(path);
                    FileSecurity security = fileInfo.GetAccessControl();
                    security.SetOwner(account);
                    fileInfo.SetAccessControl(security);
                }

                result = FileOperationResult.Ok(1);
            }
            catch (IdentityNotMappedException ex)
            {
                result = FileOperationResult.Fail("Unknown user: " + ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                result = FileOperationResult.Fail("Access denied: " + ex.Message);
            }

            return result;
        }

        #endregion

        #region Private Methods - Utility

        /// <summary>
        /// Runs a shell command and returns stdout
        /// </summary>
        /// <param name="command">Command to run</param>
        /// <param name="arguments">Command arguments</param>
        /// <returns>Standard output</returns>
        private string RunCommand(string command, string arguments)
        {
            string result = "";

            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = command;
            startInfo.Arguments = arguments;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;

            Process process = new Process();
            process.StartInfo = startInfo;
            process.Start();

            result = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();

            // Clean up
            process.Dispose();

            return result;
        }

        #endregion
    }
}
