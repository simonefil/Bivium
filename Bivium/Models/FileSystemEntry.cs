using System.IO;
using System.Runtime.InteropServices;

namespace Bivium.Models
{
    /// <summary>
    /// Represents a single file or directory entry
    /// </summary>
    public class FileSystemEntry
    {
        #region Properties

        /// <summary>
        /// File or directory name
        /// </summary>
        public string Name { get; set; } = "";

        /// <summary>
        /// Full absolute path
        /// </summary>
        public string FullPath { get; set; } = "";

        /// <summary>
        /// Size in bytes (0 for directories)
        /// </summary>
        public long SizeBytes { get; set; } = 0;

        /// <summary>
        /// Last modification date
        /// </summary>
        public DateTime LastModified { get; set; } = DateTime.MinValue;

        /// <summary>
        /// True if the entry is a directory
        /// </summary>
        public bool IsDirectory { get; set; } = false;

        /// <summary>
        /// True if the entry is hidden
        /// </summary>
        public bool IsHidden { get; set; } = false;

        /// <summary>
        /// True if the entry is read-only
        /// </summary>
        public bool IsReadOnly { get; set; } = false;

        /// <summary>
        /// True if the entry is a symbolic link
        /// </summary>
        public bool IsSymLink { get; set; } = false;

        /// <summary>
        /// File attributes / permissions string representation
        /// Windows: RHSA flags, Linux: rwxrwxrwx
        /// </summary>
        public string Attributes { get; set; } = "";

        /// <summary>
        /// File owner name (Linux only, empty on Windows)
        /// </summary>
        public string Owner { get; set; } = "";

        #endregion

        #region Constructor

        /// <summary>
        /// Default constructor
        /// </summary>
        public FileSystemEntry()
        {
        }

        /// <summary>
        /// Creates a FileSystemEntry from a FileInfo
        /// </summary>
        /// <param name="fileInfo">Source file info</param>
        public FileSystemEntry(FileInfo fileInfo)
        {
            this.Name = fileInfo.Name;
            this.FullPath = fileInfo.FullName;
            this.SizeBytes = fileInfo.Exists ? fileInfo.Length : 0;
            this.LastModified = fileInfo.Exists ? fileInfo.LastWriteTime : DateTime.MinValue;
            this.IsDirectory = false;
            this.IsHidden = (fileInfo.Attributes & FileAttributes.Hidden) == FileAttributes.Hidden;
            this.IsReadOnly = fileInfo.IsReadOnly;
            this.IsSymLink = fileInfo.LinkTarget != null;
            this.Attributes = FormatAttributes(fileInfo, false);
            this.Owner = GetOwner(fileInfo.FullName);
        }

        /// <summary>
        /// Creates a FileSystemEntry from a DirectoryInfo
        /// </summary>
        /// <param name="dirInfo">Source directory info</param>
        public FileSystemEntry(DirectoryInfo dirInfo)
        {
            this.Name = dirInfo.Name;
            this.FullPath = dirInfo.FullName;
            this.SizeBytes = 0;
            this.LastModified = dirInfo.Exists ? dirInfo.LastWriteTime : DateTime.MinValue;
            this.IsDirectory = true;
            this.IsHidden = (dirInfo.Attributes & FileAttributes.Hidden) == FileAttributes.Hidden;
            this.IsReadOnly = (dirInfo.Attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly;
            this.IsSymLink = dirInfo.LinkTarget != null;
            this.Attributes = FormatAttributes(dirInfo, true);
            this.Owner = GetOwner(dirInfo.FullName);
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Formats file attributes to a readable string (OS-aware)
        /// </summary>
        /// <param name="fsInfo">FileSystemInfo to read attributes from</param>
        /// <param name="isDirectory">True if the entry is a directory</param>
        /// <returns>Formatted attribute string</returns>
        private static string FormatAttributes(FileSystemInfo fsInfo, bool isDirectory)
        {
            // Type prefix: d for directory, - for file
            string result = isDirectory ? "d" : "-";

            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                // Unix permissions: drwxrwxrwx or -rwxrwxrwx
                try
                {
                    UnixFileMode mode = fsInfo.UnixFileMode;
                    result += FormatUnixMode(mode);
                }
                catch
                {
                    result += "---------";
                }
            }
            else
            {
                // Windows: dRHSA or -RHSA
                FileAttributes attrs = fsInfo.Attributes;
                result += (attrs & FileAttributes.ReadOnly) == FileAttributes.ReadOnly ? "R" : "-";
                result += (attrs & FileAttributes.Hidden) == FileAttributes.Hidden ? "H" : "-";
                result += (attrs & FileAttributes.System) == FileAttributes.System ? "S" : "-";
                result += (attrs & FileAttributes.Archive) == FileAttributes.Archive ? "A" : "-";
            }

            return result;
        }

        /// <summary>
        /// Formats a UnixFileMode bitmask to rwxrwxrwx string
        /// </summary>
        /// <param name="mode">Unix file mode</param>
        /// <returns>Formatted permissions string</returns>
        private static string FormatUnixMode(UnixFileMode mode)
        {
            string result = "";
            result += (mode & UnixFileMode.UserRead) != 0 ? "r" : "-";
            result += (mode & UnixFileMode.UserWrite) != 0 ? "w" : "-";
            result += (mode & UnixFileMode.UserExecute) != 0 ? "x" : "-";
            result += (mode & UnixFileMode.GroupRead) != 0 ? "r" : "-";
            result += (mode & UnixFileMode.GroupWrite) != 0 ? "w" : "-";
            result += (mode & UnixFileMode.GroupExecute) != 0 ? "x" : "-";
            result += (mode & UnixFileMode.OtherRead) != 0 ? "r" : "-";
            result += (mode & UnixFileMode.OtherWrite) != 0 ? "w" : "-";
            result += (mode & UnixFileMode.OtherExecute) != 0 ? "x" : "-";
            return result;
        }

        /// <summary>
        /// Gets the file owner name (Linux/macOS only)
        /// </summary>
        /// <param name="path">Full file path</param>
        /// <returns>Owner name, or empty string on Windows</returns>
        private static string GetOwner(string path)
        {
            string result = "";

            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                try
                {
                    // Allocate buffer for stat struct (256 bytes is enough for any platform)
                    IntPtr buf = Marshal.AllocHGlobal(256);
                    int ret = NativeStat(path, buf);

                    if (ret == 0)
                    {
                        // st_uid is at offset 28 on Linux x86_64
                        // (st_dev[8] + st_ino[8] + st_nlink[8] + st_mode[4] = 28)
                        uint uid = (uint)Marshal.ReadInt32(buf, 28);
                        IntPtr passwd = NativeGetpwuid(uid);

                        if (passwd != IntPtr.Zero)
                        {
                            // pw_name is the first field (char* pointer)
                            IntPtr namePtr = Marshal.ReadIntPtr(passwd, 0);
                            result = Marshal.PtrToStringAnsi(namePtr);
                        }
                    }

                    Marshal.FreeHGlobal(buf);
                }
                catch
                {
                    // Fallback: owner unknown
                }
            }

            return result;
        }

        /// <summary>
        /// Native stat syscall (Linux/macOS)
        /// </summary>
        [DllImport("libc", EntryPoint = "stat", SetLastError = true)]
        private static extern int NativeStat(string path, IntPtr buf);

        /// <summary>
        /// Native getpwuid to resolve uid to username (Linux/macOS)
        /// </summary>
        [DllImport("libc", EntryPoint = "getpwuid")]
        private static extern IntPtr NativeGetpwuid(uint uid);

        #endregion

        #region Public Methods

        /// <summary>
        /// Formats the file size for display
        /// </summary>
        /// <returns>Formatted size string</returns>
        public string FormatSize()
        {
            string result = "<DIR>";

            if (!this.IsDirectory)
            {
                if (this.SizeBytes < 1024)
                {
                    result = this.SizeBytes.ToString() + " B";
                }
                else if (this.SizeBytes < 1024 * 1024)
                {
                    result = (this.SizeBytes / 1024.0).ToString("F1") + " KB";
                }
                else if (this.SizeBytes < 1024 * 1024 * 1024)
                {
                    result = (this.SizeBytes / (1024.0 * 1024.0)).ToString("F1") + " MB";
                }
                else
                {
                    result = (this.SizeBytes / (1024.0 * 1024.0 * 1024.0)).ToString("F1") + " GB";
                }
            }

            return result;
        }

        /// <summary>
        /// Formats the last modified date for display
        /// </summary>
        /// <returns>Formatted date string</returns>
        public string FormatDate()
        {
            string result = "";

            if (this.LastModified != DateTime.MinValue)
            {
                result = this.LastModified.ToString("yyyy-MM-dd HH:mm");
            }

            return result;
        }

        #endregion
    }
}
