using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Bivium.Services
{
    /// <summary>
    /// Manages shell process lifecycle and I/O for the web terminal
    /// Uses ConPTY on Windows, script wrapper on Linux
    /// </summary>
    public class ShellService : IDisposable
    {
        #region Windows ConPTY P/Invoke

        /// <summary>
        /// Console coordinate structure
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct COORD
        {
            /// <summary>
            /// Column count
            /// </summary>
            public short X;

            /// <summary>
            /// Row count
            /// </summary>
            public short Y;
        }

        /// <summary>
        /// Security attributes for pipe creation
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct SECURITY_ATTRIBUTES
        {
            /// <summary>
            /// Structure size
            /// </summary>
            public int nLength;

            /// <summary>
            /// Security descriptor pointer
            /// </summary>
            public IntPtr lpSecurityDescriptor;

            /// <summary>
            /// Whether handles are inheritable
            /// </summary>
            public bool bInheritHandle;
        }

        /// <summary>
        /// Startup info structure for CreateProcess
        /// </summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFOEX
        {
            /// <summary>
            /// Structure size (of STARTUPINFO portion)
            /// </summary>
            public int cb;

            /// <summary>
            /// Reserved (must be null)
            /// </summary>
            public IntPtr lpReserved;

            /// <summary>
            /// Desktop name
            /// </summary>
            public IntPtr lpDesktop;

            /// <summary>
            /// Window title
            /// </summary>
            public IntPtr lpTitle;

            /// <summary>
            /// Window X position
            /// </summary>
            public int dwX;

            /// <summary>
            /// Window Y position
            /// </summary>
            public int dwY;

            /// <summary>
            /// Window width
            /// </summary>
            public int dwXSize;

            /// <summary>
            /// Window height
            /// </summary>
            public int dwYSize;

            /// <summary>
            /// Console character width
            /// </summary>
            public int dwXCountChars;

            /// <summary>
            /// Console character height
            /// </summary>
            public int dwYCountChars;

            /// <summary>
            /// Fill attribute
            /// </summary>
            public int dwFillAttribute;

            /// <summary>
            /// Flags
            /// </summary>
            public int dwFlags;

            /// <summary>
            /// Show window command
            /// </summary>
            public short wShowWindow;

            /// <summary>
            /// Reserved (must be zero)
            /// </summary>
            public short cbReserved2;

            /// <summary>
            /// Reserved (must be null)
            /// </summary>
            public IntPtr lpReserved2;

            /// <summary>
            /// Standard input handle
            /// </summary>
            public IntPtr hStdInput;

            /// <summary>
            /// Standard output handle
            /// </summary>
            public IntPtr hStdOutput;

            /// <summary>
            /// Standard error handle
            /// </summary>
            public IntPtr hStdError;

            /// <summary>
            /// Extended attribute list pointer
            /// </summary>
            public IntPtr lpAttributeList;
        }

        /// <summary>
        /// Process information returned by CreateProcess
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            /// <summary>
            /// Process handle
            /// </summary>
            public IntPtr hProcess;

            /// <summary>
            /// Primary thread handle
            /// </summary>
            public IntPtr hThread;

            /// <summary>
            /// Process ID
            /// </summary>
            public int dwProcessId;

            /// <summary>
            /// Thread ID
            /// </summary>
            public int dwThreadId;
        }

        /// <summary>
        /// Creates a pseudo console (ConPTY)
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int CreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out IntPtr phPC);

        /// <summary>
        /// Resizes a pseudo console
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

        /// <summary>
        /// Closes a pseudo console
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern void ClosePseudoConsole(IntPtr hPC);

        /// <summary>
        /// Creates an anonymous pipe
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, ref SECURITY_ATTRIBUTES lpPipeAttributes, uint nSize);

        /// <summary>
        /// Initializes a process thread attribute list
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

        /// <summary>
        /// Updates a process thread attribute
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

        /// <summary>
        /// Deletes a process thread attribute list
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

        /// <summary>
        /// Creates a new process
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessW(string lpApplicationName, string lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string lpCurrentDirectory, ref STARTUPINFOEX lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

        /// <summary>
        /// Closes a kernel handle
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        /// <summary>
        /// Waits for a single kernel object
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        /// <summary>
        /// Pseudo console attribute constant
        /// </summary>
        private static readonly IntPtr PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = (IntPtr)0x00020016;

        /// <summary>
        /// Extended startup info flag
        /// </summary>
        private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;

        #endregion

        #region Class Variables

        /// <summary>
        /// ConPTY handle (Windows only)
        /// </summary>
        private IntPtr _ptyHandle = IntPtr.Zero;

        /// <summary>
        /// Pipe for writing input to the PTY (Windows)
        /// </summary>
        private SafeFileHandle _pipeInputWrite;

        /// <summary>
        /// Pipe for reading output from the PTY (Windows)
        /// </summary>
        private SafeFileHandle _pipeOutputRead;

        /// <summary>
        /// Attribute list pointer (Windows, must be freed)
        /// </summary>
        private IntPtr _attributeList = IntPtr.Zero;

        /// <summary>
        /// Process information (Windows)
        /// </summary>
        private PROCESS_INFORMATION _processInfo;

        /// <summary>
        /// Process wrapper for Linux
        /// </summary>
        private Process _process;

        /// <summary>
        /// Whether the process is running
        /// </summary>
        private bool _isRunning = false;

        /// <summary>
        /// Callback for data received from the shell
        /// </summary>
        private Action<string> _onDataReceived;

        /// <summary>
        /// Callback when the process exits
        /// </summary>
        private Action _onExit;

        /// <summary>
        /// Reader thread for PTY output
        /// </summary>
        private Thread _readThread;

        /// <summary>
        /// Monitor thread for process exit (Windows)
        /// </summary>
        private Thread _exitThread;

        /// <summary>
        /// Whether running on Windows
        /// </summary>
        private bool _isWindows = false;

        #endregion

        #region Constructor

        /// <summary>
        /// Default constructor
        /// </summary>
        public ShellService()
        {
            this._isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        }

        #endregion

        #region Properties

        /// <summary>
        /// Whether the shell process is currently running
        /// </summary>
        public bool IsRunning => this._isRunning;

        #endregion

        #region Public Methods

        /// <summary>
        /// Starts a shell process with the appropriate shell for the platform
        /// </summary>
        /// <param name="workingDirectory">Starting directory for the shell</param>
        /// <param name="onDataReceived">Callback for output data</param>
        /// <param name="onExit">Callback when the process exits</param>
        public void Start(string workingDirectory, Action<string> onDataReceived, Action onExit)
        {
            if (this._isRunning)
            {
                return;
            }

            this._onDataReceived = onDataReceived;
            this._onExit = onExit;

            if (this._isWindows)
            {
                this.StartWindows(workingDirectory);
            }
            else
            {
                this.StartLinux(workingDirectory);
            }
        }

        /// <summary>
        /// Sends input data to the shell
        /// </summary>
        /// <param name="data">Input data string</param>
        public void SendInput(string data)
        {
            if (!this._isRunning)
            {
                return;
            }

            if (this._isWindows)
            {
                this.SendInputWindows(data);
            }
            else
            {
                this.SendInputLinux(data);
            }
        }

        /// <summary>
        /// Resizes the terminal
        /// </summary>
        /// <param name="cols">Number of columns</param>
        /// <param name="rows">Number of rows</param>
        public void Resize(int cols, int rows)
        {
            if (!this._isRunning)
            {
                return;
            }

            if (this._isWindows && this._ptyHandle != IntPtr.Zero)
            {
                COORD size = new COORD();
                size.X = (short)cols;
                size.Y = (short)rows;
                ResizePseudoConsole(this._ptyHandle, size);
            }
        }

        /// <summary>
        /// Stops the shell process
        /// </summary>
        public void Stop()
        {
            this._isRunning = false;

            if (this._isWindows)
            {
                this.StopWindows();
            }
            else
            {
                this.StopLinux();
            }
        }

        /// <summary>
        /// Disposes all resources
        /// </summary>
        public void Dispose()
        {
            this.Stop();
        }

        #endregion

        #region Windows ConPTY Implementation

        /// <summary>
        /// Starts a shell using ConPTY on Windows
        /// </summary>
        /// <param name="workingDirectory">Starting directory</param>
        private void StartWindows(string workingDirectory)
        {
            // Create pipes: ptyInput (we write) -> ConPTY reads; ConPTY writes -> ptyOutput (we read)
            SafeFileHandle pipeInputRead;
            SafeFileHandle pipeOutputWrite;

            SECURITY_ATTRIBUTES sa = new SECURITY_ATTRIBUTES();
            sa.nLength = Marshal.SizeOf(sa);
            sa.bInheritHandle = true;

            // Input pipe: we write to _pipeInputWrite, ConPTY reads from pipeInputRead
            CreatePipe(out pipeInputRead, out this._pipeInputWrite, ref sa, 0);

            // Output pipe: ConPTY writes to pipeOutputWrite, we read from _pipeOutputRead
            CreatePipe(out this._pipeOutputRead, out pipeOutputWrite, ref sa, 0);

            // Create the pseudo console
            COORD consoleSize = new COORD();
            consoleSize.X = 120;
            consoleSize.Y = 30;

            int hr = CreatePseudoConsole(consoleSize, pipeInputRead, pipeOutputWrite, 0, out this._ptyHandle);
            if (hr != 0)
            {
                throw new InvalidOperationException("CreatePseudoConsole failed with HRESULT: " + hr);
            }

            // Close the pipe ends that the ConPTY now owns
            pipeInputRead.Dispose();
            pipeOutputWrite.Dispose();

            // Prepare the attribute list for CreateProcess
            IntPtr listSize = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref listSize);

            this._attributeList = Marshal.AllocHGlobal(listSize.ToInt32());
            InitializeProcThreadAttributeList(this._attributeList, 1, 0, ref listSize);

            // Add the pseudo console handle as a process attribute
            UpdateProcThreadAttribute(this._attributeList, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, this._ptyHandle, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero);

            // Build the command line
            string shellPath = this.DetectShell();
            string shellArgs = this.GetShellArgs(shellPath);
            string commandLine = shellPath;
            if (!string.IsNullOrEmpty(shellArgs))
            {
                commandLine = shellPath + " " + shellArgs;
            }

            // Create the process
            STARTUPINFOEX startupInfo = new STARTUPINFOEX();
            startupInfo.cb = Marshal.SizeOf(typeof(STARTUPINFOEX));
            startupInfo.lpAttributeList = this._attributeList;

            bool created = CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, false, EXTENDED_STARTUPINFO_PRESENT, IntPtr.Zero, workingDirectory, ref startupInfo, out this._processInfo);
            if (!created)
            {
                int error = Marshal.GetLastWin32Error();
                throw new InvalidOperationException("CreateProcess failed with error: " + error);
            }

            this._isRunning = true;

            // Start reading output from the PTY
            this._readThread = new Thread(this.ReadPtyOutput);
            this._readThread.IsBackground = true;
            this._readThread.Start();

            // Start monitoring for process exit
            this._exitThread = new Thread(this.MonitorProcessExit);
            this._exitThread.IsBackground = true;
            this._exitThread.Start();
        }

        /// <summary>
        /// Sends input to the ConPTY input pipe
        /// </summary>
        /// <param name="data">Input string</param>
        private void SendInputWindows(string data)
        {
            if (this._pipeInputWrite != null && !this._pipeInputWrite.IsInvalid)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(data);
                FileStream stream = new FileStream(this._pipeInputWrite, FileAccess.Write, bufferSize: 256, isAsync: false);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }
        }

        /// <summary>
        /// Reads output from the ConPTY output pipe in a background thread
        /// </summary>
        private void ReadPtyOutput()
        {
            try
            {
                FileStream stream = new FileStream(this._pipeOutputRead, FileAccess.Read, bufferSize: 4096, isAsync: false);
                byte[] buffer = new byte[4096];
                int bytesRead = 0;

                while (this._isRunning)
                {
                    bytesRead = stream.Read(buffer, 0, buffer.Length);

                    if (bytesRead > 0)
                    {
                        string data = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                        this._onDataReceived?.Invoke(data);
                    }
                    else
                    {
                        // Pipe closed
                        break;
                    }
                }
            }
            catch (IOException)
            {
                // Pipe broken, process likely exited
            }
        }

        /// <summary>
        /// Monitors the process handle for exit
        /// </summary>
        private void MonitorProcessExit()
        {
            // Wait for process to exit (INFINITE = 0xFFFFFFFF)
            WaitForSingleObject(this._processInfo.hProcess, 0xFFFFFFFF);
            this._isRunning = false;
            this._onExit?.Invoke();
        }

        /// <summary>
        /// Stops the ConPTY process on Windows
        /// </summary>
        private void StopWindows()
        {
            // Close the pseudo console (this signals the process to terminate)
            if (this._ptyHandle != IntPtr.Zero)
            {
                ClosePseudoConsole(this._ptyHandle);
                this._ptyHandle = IntPtr.Zero;
            }

            // Close process handles
            if (this._processInfo.hProcess != IntPtr.Zero)
            {
                CloseHandle(this._processInfo.hProcess);
                this._processInfo.hProcess = IntPtr.Zero;
            }

            if (this._processInfo.hThread != IntPtr.Zero)
            {
                CloseHandle(this._processInfo.hThread);
                this._processInfo.hThread = IntPtr.Zero;
            }

            // Cleanup attribute list
            if (this._attributeList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(this._attributeList);
                Marshal.FreeHGlobal(this._attributeList);
                this._attributeList = IntPtr.Zero;
            }

            // Close pipes
            if (this._pipeInputWrite != null && !this._pipeInputWrite.IsInvalid)
            {
                this._pipeInputWrite.Dispose();
                this._pipeInputWrite = null;
            }

            if (this._pipeOutputRead != null && !this._pipeOutputRead.IsInvalid)
            {
                this._pipeOutputRead.Dispose();
                this._pipeOutputRead = null;
            }
        }

        #endregion

        #region Linux Implementation

        /// <summary>
        /// Starts a shell using script as PTY wrapper on Linux
        /// </summary>
        /// <param name="workingDirectory">Starting directory</param>
        private void StartLinux(string workingDirectory)
        {
            string shellPath = this.DetectShell();

            // Use script command as PTY wrapper
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = "/usr/bin/script";
            startInfo.Arguments = "-qfc \"" + shellPath + "\" /dev/null";
            startInfo.WorkingDirectory = workingDirectory;
            startInfo.RedirectStandardInput = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.Environment["TERM"] = "xterm-256color";
            startInfo.Environment["COLORTERM"] = "truecolor";

            this._process = new Process();
            this._process.StartInfo = startInfo;
            this._process.EnableRaisingEvents = true;
            this._process.Exited += this.OnLinuxProcessExited;
            this._process.Start();
            this._isRunning = true;

            // Start reader thread
            this._readThread = new Thread(this.ReadLinuxOutput);
            this._readThread.IsBackground = true;
            this._readThread.Start();
        }

        /// <summary>
        /// Sends input to the Linux shell process
        /// </summary>
        /// <param name="data">Input string</param>
        private void SendInputLinux(string data)
        {
            if (this._process != null && !this._process.HasExited)
            {
                this._process.StandardInput.Write(data);
                this._process.StandardInput.Flush();
            }
        }

        /// <summary>
        /// Reads output from the Linux shell in a background thread
        /// </summary>
        private void ReadLinuxOutput()
        {
            try
            {
                char[] buffer = new char[4096];
                int charsRead = 0;

                while (this._isRunning && this._process != null && !this._process.HasExited)
                {
                    charsRead = this._process.StandardOutput.Read(buffer, 0, buffer.Length);

                    if (charsRead > 0)
                    {
                        string data = new string(buffer, 0, charsRead);
                        this._onDataReceived?.Invoke(data);
                    }
                }
            }
            catch (InvalidOperationException)
            {
                // Process was killed
            }
            catch (IOException)
            {
                // Pipe broken
            }
        }

        /// <summary>
        /// Handles Linux process exit
        /// </summary>
        private void OnLinuxProcessExited(object sender, EventArgs e)
        {
            this._isRunning = false;
            this._onExit?.Invoke();
        }

        /// <summary>
        /// Stops the Linux shell process
        /// </summary>
        private void StopLinux()
        {
            if (this._process != null)
            {
                if (!this._process.HasExited)
                {
                    try
                    {
                        this._process.Kill(true);
                    }
                    catch (InvalidOperationException)
                    {
                        // Already exited
                    }
                }

                this._process.Dispose();
                this._process = null;
            }
        }

        #endregion

        #region Shell Detection

        /// <summary>
        /// Detects the best available shell for the current platform
        /// </summary>
        /// <returns>Path to the shell executable</returns>
        private string DetectShell()
        {
            string result = "";

            if (this._isWindows)
            {
                // Priority: pwsh (PowerShell 7+) -> powershell (5.x) -> cmd
                result = this.FindExecutable("pwsh");

                if (string.IsNullOrEmpty(result))
                {
                    result = this.FindExecutable("powershell");
                }

                if (string.IsNullOrEmpty(result))
                {
                    result = "cmd.exe";
                }
            }
            else
            {
                // Linux/macOS: use the user's default shell
                string shellEnv = Environment.GetEnvironmentVariable("SHELL");

                if (!string.IsNullOrEmpty(shellEnv) && File.Exists(shellEnv))
                {
                    result = shellEnv;
                }
                else
                {
                    result = this.FindExecutable("bash");

                    if (string.IsNullOrEmpty(result))
                    {
                        result = this.FindExecutable("sh");
                    }

                    if (string.IsNullOrEmpty(result))
                    {
                        result = "/bin/sh";
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Gets shell-specific arguments for interactive mode
        /// </summary>
        /// <param name="shellPath">Path to the shell</param>
        /// <returns>Arguments string</returns>
        private string GetShellArgs(string shellPath)
        {
            string result = "";
            string shellName = Path.GetFileNameWithoutExtension(shellPath).ToLowerInvariant();

            if (shellName == "pwsh" || shellName == "powershell")
            {
                result = "-NoLogo -NoExit";
            }

            return result;
        }

        /// <summary>
        /// Finds an executable in the system PATH
        /// </summary>
        /// <param name="name">Executable name</param>
        /// <returns>Full path or empty string if not found</returns>
        private string FindExecutable(string name)
        {
            string result = "";
            string pathEnv = Environment.GetEnvironmentVariable("PATH");

            if (string.IsNullOrEmpty(pathEnv))
            {
                return result;
            }

            char separator = this._isWindows ? ';' : ':';
            string[] paths = pathEnv.Split(separator);
            string[] extensions = this._isWindows
                ? new string[] { ".exe", ".cmd", ".bat" }
                : new string[] { "" };

            for (int i = 0; i < paths.Length; i++)
            {
                for (int j = 0; j < extensions.Length; j++)
                {
                    string fullPath = Path.Combine(paths[i], name + extensions[j]);
                    if (File.Exists(fullPath))
                    {
                        result = fullPath;
                        break;
                    }
                }

                if (!string.IsNullOrEmpty(result))
                {
                    break;
                }
            }

            return result;
        }

        #endregion
    }
}
