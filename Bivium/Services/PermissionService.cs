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

            if (!this._securityService.IsPathSafe(path))
            {
                throw new IOException("Invalid path: " + path);
            }

            if (this._isUnix)
            {
                this.GetUnixPermissions(path, result);
            }
            else
            {
                this.GetWindowsPermissions(path, result);
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
            CommandResult commandResult;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                commandResult = this.RunCommand("stat", new List<string> { "-f", "%Lp %Su %Sg", path });
            }
            else
            {
                commandResult = this.RunCommand("stat", new List<string> { "-c", "%a %U %G", path });
            }

            if (!commandResult.Success)
            {
                throw new IOException(commandResult.ErrorMessage);
            }

            string statOutput = commandResult.Output;

            bool parsed = false;

            if (!string.IsNullOrEmpty(statOutput))
            {
                // Remove quotes if present
                statOutput = statOutput.Trim().Trim('"');
                string[] parts = statOutput.Split(' ', StringSplitOptions.RemoveEmptyEntries);

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
                    parsed = true;
                }
            }

            if (!parsed)
            {
                throw new IOException("Could not parse permission data for: " + path);
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
            UnixFileMode mode = this.BuildUnixFileMode(model);
            int processed = 0;
            int failed = 0;
            string lastError = "";

            this.ApplyUnixFileMode(path, mode, recursive, ref processed, ref failed, ref lastError);

            FileOperationResult result = new FileOperationResult();
            result.Success = failed == 0;
            result.FilesProcessed = processed;
            result.FilesFailed = failed;
            result.ErrorMessage = lastError;
            return result;
        }

        /// <summary>
        /// Builds a UnixFileMode value from the permission model
        /// </summary>
        /// <param name="model">Permission model</param>
        /// <returns>Unix file mode</returns>
        private UnixFileMode BuildUnixFileMode(PermissionModel model)
        {
            UnixFileMode result = 0;

            if (model.OwnerRead) result |= UnixFileMode.UserRead;
            if (model.OwnerWrite) result |= UnixFileMode.UserWrite;
            if (model.OwnerExecute) result |= UnixFileMode.UserExecute;
            if (model.GroupRead) result |= UnixFileMode.GroupRead;
            if (model.GroupWrite) result |= UnixFileMode.GroupWrite;
            if (model.GroupExecute) result |= UnixFileMode.GroupExecute;
            if (model.OthersRead) result |= UnixFileMode.OtherRead;
            if (model.OthersWrite) result |= UnixFileMode.OtherWrite;
            if (model.OthersExecute) result |= UnixFileMode.OtherExecute;

            return result;
        }

        /// <summary>
        /// Applies Unix permissions to one path and optionally its directory contents
        /// </summary>
        /// <param name="path">File or directory path</param>
        /// <param name="mode">Unix file mode to apply</param>
        /// <param name="recursive">If true, apply recursively</param>
        /// <param name="processed">Number of successfully processed entries</param>
        /// <param name="failed">Number of failed entries</param>
        /// <param name="lastError">Last error message</param>
        private void ApplyUnixFileMode(string path, UnixFileMode mode, bool recursive, ref int processed, ref int failed, ref string lastError)
        {
            try
            {
                File.SetUnixFileMode(path, mode);
                processed++;
            }
            catch (UnauthorizedAccessException ex)
            {
                failed++;
                lastError = "Access denied: " + ex.Message;
                return;
            }
            catch (IOException ex)
            {
                failed++;
                lastError = "I/O error: " + ex.Message;
                return;
            }

            if (!recursive || !Directory.Exists(path) || this.IsSymbolicLink(path))
            {
                return;
            }

            try
            {
                string[] entries = Directory.GetFileSystemEntries(path);
                for (int i = 0; i < entries.Length; i++)
                {
                    this.ApplyUnixFileMode(entries[i], mode, true, ref processed, ref failed, ref lastError);
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                failed++;
                lastError = "Access denied: " + ex.Message;
            }
            catch (IOException ex)
            {
                failed++;
                lastError = "I/O error: " + ex.Message;
            }
        }

        /// <summary>
        /// Checks whether a filesystem entry is a symbolic link
        /// </summary>
        /// <param name="path">File or directory path</param>
        /// <returns>True if the entry is a symbolic link</returns>
        private bool IsSymbolicLink(string path)
        {
            FileAttributes attributes = File.GetAttributes(path);
            bool result = (attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
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

            List<string> arguments = new List<string>();
            if (recursive)
            {
                arguments.Add("-R");
            }
            arguments.Add(ownerGroup);
            arguments.Add(path);

            CommandResult commandResult = this.RunCommand("chown", arguments);
            if (!commandResult.Success)
            {
                return FileOperationResult.Fail(commandResult.ErrorMessage);
            }

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
        /// Runs a command without shell expansion and returns output plus exit status
        /// </summary>
        /// <param name="command">Command to run</param>
        /// <param name="arguments">Command arguments</param>
        /// <returns>Command result</returns>
        private CommandResult RunCommand(string command, List<string> arguments)
        {
            CommandResult result = new CommandResult();

            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = command;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;

            for (int i = 0; i < arguments.Count; i++)
            {
                startInfo.ArgumentList.Add(arguments[i]);
            }

            Process process = new Process();
            process.StartInfo = startInfo;
            process.Start();

            result.Output = process.StandardOutput.ReadToEnd().Trim();
            result.ErrorMessage = process.StandardError.ReadToEnd().Trim();
            process.WaitForExit();
            result.ExitCode = process.ExitCode;

            if (!result.Success && string.IsNullOrEmpty(result.ErrorMessage))
            {
                result.ErrorMessage = command + " failed with exit code " + result.ExitCode;
            }

            // Clean up
            process.Dispose();

            return result;
        }

        #endregion

        #region Nested Classes

        /// <summary>
        /// Result of a spawned process
        /// </summary>
        private class CommandResult
        {
            #region Properties

            /// <summary>
            /// Standard output
            /// </summary>
            public string Output { get; set; } = "";

            /// <summary>
            /// Standard error or generated error message
            /// </summary>
            public string ErrorMessage { get; set; } = "";

            /// <summary>
            /// Process exit code
            /// </summary>
            public int ExitCode { get; set; } = 0;

            /// <summary>
            /// True if command exited successfully
            /// </summary>
            public bool Success => this.ExitCode == 0;

            #endregion
        }

        #endregion
    }
}
