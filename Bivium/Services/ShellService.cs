using Porta.Pty;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Bivium.Services
{
    /// <summary>
    /// Manages shell process lifecycle and I/O for the web terminal
    /// </summary>
    public class ShellService : IDisposable
    {
        #region Class Variables

        /// <summary>
        /// Active PTY connection
        /// </summary>
        private IPtyConnection _connection;

        /// <summary>
        /// Cancellation token for the output reader
        /// </summary>
        private CancellationTokenSource _readCancellation;

        /// <summary>
        /// Reader task for PTY output
        /// </summary>
        private Task _readTask;

        /// <summary>
        /// Exit handler captured for the active PTY connection
        /// </summary>
        private EventHandler<PtyExitedEventArgs> _processExitedHandler;

        /// <summary>
        /// Whether the process is running
        /// </summary>
        private bool _isRunning = false;

        /// <summary>
        /// Whether stop was requested by the component
        /// </summary>
        private bool _stopRequested = false;

        /// <summary>
        /// Callback when the process exits
        /// </summary>
        private Action _onExit;

        /// <summary>
        /// Whether this service has been disposed
        /// </summary>
        private bool _disposed = false;

        /// <summary>
        /// Whether running on Windows
        /// </summary>
        private readonly bool _isWindows = false;

        /// <summary>
        /// Synchronizes process state changes
        /// </summary>
        private readonly object _stateLock = new object();

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
        public bool IsRunning
        {
            get
            {
                lock (this._stateLock)
                {
                    return this._isRunning;
                }
            }
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Starts a shell process with the appropriate shell for the platform
        /// </summary>
        /// <param name="workingDirectory">Starting directory for the shell</param>
        /// <param name="cols">Initial terminal columns</param>
        /// <param name="rows">Initial terminal rows</param>
        /// <param name="onDataReceived">Callback for output data</param>
        /// <param name="onExit">Callback when the process exits</param>
        public void Start(string workingDirectory, int cols, int rows, Action<ReadOnlyMemory<byte>> onDataReceived, Action onExit)
        {
            lock (this._stateLock)
            {
                if (this._disposed || this._isRunning)
                {
                    return;
                }
            }

            if (this._connection != null)
            {
                this.Stop();
            }

            string shellPath = this.DetectShell();
            string resolvedWorkingDirectory = workingDirectory;
            if (string.IsNullOrWhiteSpace(resolvedWorkingDirectory) || !Directory.Exists(resolvedWorkingDirectory))
            {
                resolvedWorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }

            string processPath = shellPath;
            string[] commandLine = this.GetShellArgs(shellPath);
            if (!this._isWindows)
            {
                string envPath = this.FindExecutable("env");
                if (!string.IsNullOrEmpty(envPath))
                {
                    string[] shellArgs = commandLine;
                    processPath = envPath;
                    commandLine = new string[shellArgs.Length + 5];
                    commandLine[0] = "-u";
                    commandLine[1] = "NO_COLOR";
                    commandLine[2] = "-u";
                    commandLine[3] = "ANSI_COLORS_DISABLED";
                    commandLine[4] = shellPath;
                    Array.Copy(shellArgs, 0, commandLine, 5, shellArgs.Length);
                }
            }

            int safeCols = Math.Clamp(cols, 20, 500);
            int safeRows = Math.Clamp(rows, 5, 200);
            Dictionary<string, string> environment = new Dictionary<string, string>();
            environment["TERM"] = "xterm-256color";
            environment["COLORTERM"] = "truecolor";
            environment["CLICOLOR"] = "1";
            environment["NO_COLOR"] = "";
            environment["ANSI_COLORS_DISABLED"] = "";

            PtyOptions options = new PtyOptions();
            options.Name = "Bivium";
            options.Cols = safeCols;
            options.Rows = safeRows;
            options.Cwd = resolvedWorkingDirectory;
            options.App = processPath;
            options.CommandLine = commandLine;
            options.Environment = environment;

            IPtyConnection connection = null;
            CancellationTokenSource readCancellation = new CancellationTokenSource();
            CancellationToken readCancellationToken = readCancellation.Token;
            EventHandler<PtyExitedEventArgs> processExitedHandler = null;

            try
            {
                connection = PtyProvider.SpawnAsync(options, CancellationToken.None).GetAwaiter().GetResult();
                processExitedHandler = (sender, e) => this.CompleteProcessExit(connection);
                lock (this._stateLock)
                {
                    if (this._disposed)
                    {
                        ((IDisposable)connection).Dispose();
                        readCancellation.Dispose();
                        return;
                    }

                    this._connection = connection;
                    this._readCancellation = readCancellation;
                    this._processExitedHandler = processExitedHandler;
                    this._onExit = onExit;
                    this._stopRequested = false;
                    this._isRunning = true;
                }

                connection.ProcessExited += processExitedHandler;
                Task readTask = Task.Run(() => this.ReadOutputAsync(connection, onDataReceived, readCancellationToken));
                lock (this._stateLock)
                {
                    if (this._connection == connection)
                    {
                        this._readTask = readTask;
                    }
                }
            }
            catch
            {
                if (connection != null)
                {
                    if (processExitedHandler != null)
                    {
                        connection.ProcessExited -= processExitedHandler;
                    }

                    ((IDisposable)connection).Dispose();
                }

                readCancellation.Dispose();
                lock (this._stateLock)
                {
                    if (this._connection == connection)
                    {
                        this._connection = null;
                        this._readCancellation = null;
                        this._processExitedHandler = null;
                        this._readTask = null;
                        this._onExit = null;
                        this._isRunning = false;
                    }
                }
                throw;
            }
        }

        /// <summary>
        /// Sends input data to the shell
        /// </summary>
        /// <param name="data">Input data string</param>
        public void SendInput(string data)
        {
            IPtyConnection connection;
            lock (this._stateLock)
            {
                connection = this._connection;
                if (!this._isRunning || connection == null || string.IsNullOrEmpty(data))
                {
                    return;
                }
            }

            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(data);
                connection.WriterStream.Write(bytes, 0, bytes.Length);
                connection.WriterStream.Flush();
            }
            catch (IOException)
            {
                this.CompleteProcessExit(connection);
            }
            catch (ObjectDisposedException)
            {
                this.CompleteProcessExit(connection);
            }
            catch (InvalidOperationException)
            {
                this.CompleteProcessExit(connection);
            }
        }

        /// <summary>
        /// Resizes the terminal
        /// </summary>
        /// <param name="cols">Number of columns</param>
        /// <param name="rows">Number of rows</param>
        public void Resize(int cols, int rows)
        {
            IPtyConnection connection;
            lock (this._stateLock)
            {
                connection = this._connection;
                if (!this._isRunning || connection == null)
                {
                    return;
                }
            }

            try
            {
                connection.Resize(Math.Clamp(cols, 20, 500), Math.Clamp(rows, 5, 200));
            }
            catch (IOException)
            {
                this.CompleteProcessExit(connection);
            }
            catch (ObjectDisposedException)
            {
                this.CompleteProcessExit(connection);
            }
            catch (InvalidOperationException)
            {
                this.CompleteProcessExit(connection);
            }
        }

        /// <summary>
        /// Stops the shell process
        /// </summary>
        public void Stop()
        {
            IPtyConnection connection;
            CancellationTokenSource readCancellation;
            Task readTask;
            EventHandler<PtyExitedEventArgs> processExitedHandler;

            lock (this._stateLock)
            {
                this._stopRequested = true;
                this._isRunning = false;
                connection = this._connection;
                readCancellation = this._readCancellation;
                readTask = this._readTask;
                processExitedHandler = this._processExitedHandler;
                this._connection = null;
                this._readCancellation = null;
                this._readTask = null;
                this._processExitedHandler = null;
                this._onExit = null;
            }

            if (readCancellation != null)
            {
                readCancellation.Cancel();
            }

            if (connection != null)
            {
                if (processExitedHandler != null)
                {
                    connection.ProcessExited -= processExitedHandler;
                }

                try
                {
                    connection.Kill();
                }
                catch (ObjectDisposedException)
                {
                    // Connection already disposed
                }
                catch (InvalidOperationException)
                {
                    // Process already exited
                }
                catch (IOException)
                {
                    // PTY already closed
                }

                try
                {
                    ((IDisposable)connection).Dispose();
                }
                catch (ObjectDisposedException)
                {
                    // Connection already disposed
                }
            }

            this.ReleaseReadCancellation(readCancellation, readTask);
        }

        /// <summary>
        /// Disposes all resources
        /// </summary>
        public void Dispose()
        {
            lock (this._stateLock)
            {
                this._disposed = true;
            }

            this.Stop();
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Reads output from the active PTY
        /// </summary>
        /// <param name="onDataReceived">Callback captured for this connection generation</param>
        /// <param name="cancellationToken">Cancellation token for stopping the reader</param>
        private async Task ReadOutputAsync(IPtyConnection connection, Action<ReadOnlyMemory<byte>> onDataReceived, CancellationToken cancellationToken)
        {
            if (connection == null)
            {
                return;
            }

            try
            {
                byte[] bytes = new byte[8192];

                while (!cancellationToken.IsCancellationRequested)
                {
                    int bytesRead = await connection.ReaderStream.ReadAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
                    if (bytesRead <= 0)
                    {
                        break;
                    }

                    onDataReceived?.Invoke(bytes.AsMemory(0, bytesRead));
                }
            }
            catch (OperationCanceledException)
            {
                // Reader stopped by Dispose/Stop
            }
            catch (IOException)
            {
                // PTY closed
            }
            catch (ObjectDisposedException)
            {
                // PTY disposed
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                this.CompleteProcessExit(connection);
            }
        }

        /// <summary>
        /// Marks the process as exited and notifies the component once
        /// </summary>
        /// <param name="expectedConnection">Connection that observed the exit</param>
        private void CompleteProcessExit(IPtyConnection expectedConnection)
        {
            IPtyConnection connection;
            CancellationTokenSource readCancellation;
            Task readTask;
            EventHandler<PtyExitedEventArgs> processExitedHandler;
            Action onExit;
            bool shouldNotify = false;

            lock (this._stateLock)
            {
                // A late reader or ProcessExited callback must not terminate a newer shell generation
                if (expectedConnection == null || this._connection != expectedConnection)
                {
                    return;
                }

                shouldNotify = this._isRunning && !this._stopRequested && !this._disposed;
                this._isRunning = false;
                connection = this._connection;
                readCancellation = this._readCancellation;
                readTask = this._readTask;
                processExitedHandler = this._processExitedHandler;
                onExit = this._onExit;
                this._connection = null;
                this._readCancellation = null;
                this._readTask = null;
                this._processExitedHandler = null;
                this._onExit = null;
            }

            if (readCancellation != null)
            {
                readCancellation.Cancel();
            }

            if (connection != null)
            {
                if (processExitedHandler != null)
                {
                    connection.ProcessExited -= processExitedHandler;
                }

                try
                {
                    ((IDisposable)connection).Dispose();
                }
                catch (ObjectDisposedException)
                {
                    // Connection already disposed
                }
            }

            this.ReleaseReadCancellation(readCancellation, readTask);

            if (shouldNotify)
            {
                onExit?.Invoke();
            }
        }

        /// <summary>
        /// Disposes the reader cancellation source after the reader task has stopped
        /// </summary>
        /// <param name="readCancellation">Cancellation source used by the reader</param>
        /// <param name="readTask">Reader task</param>
        private void ReleaseReadCancellation(CancellationTokenSource readCancellation, Task readTask)
        {
            if (readCancellation == null)
            {
                return;
            }

            if (readTask == null || readTask.IsCompleted)
            {
                readCancellation.Dispose();
                return;
            }

            _ = readTask.ContinueWith(task => readCancellation.Dispose(), TaskScheduler.Default);
        }

        /// <summary>
        /// Detects the best available shell for the current platform
        /// </summary>
        /// <returns>Path to the shell executable</returns>
        private string DetectShell()
        {
            string result = "";

            if (this._isWindows)
            {
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
        /// <returns>Argument array</returns>
        private string[] GetShellArgs(string shellPath)
        {
            string shellName = Path.GetFileNameWithoutExtension(shellPath).ToLowerInvariant();
            if (shellName == "pwsh" || shellName == "powershell")
            {
                return new string[] { "-NoLogo", "-NoExit" };
            }

            return new string[0];
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
            string[] extensions = this._isWindows ? new string[] { ".exe", ".cmd", ".bat" } : new string[] { "" };
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
