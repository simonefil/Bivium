using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Bivium.Services;
using System.Text;
using System.Threading;

namespace Bivium.Components.Shared
{
    /// <summary>
    /// Terminal window with embedded xterm.js and shell processes
    /// </summary>
    public partial class TerminalPanel : ComponentBase, IDisposable
    {
        #region Constants

        private const int OUTPUT_FLUSH_DELAY_MS = 16;

        private const int OUTPUT_FLUSH_THRESHOLD = 32768;

        private const int OUTPUT_BUFFER_LIMIT = 1048576;

        #endregion

        #region Parameters

        /// <summary>
        /// Working directory for new shell tabs
        /// </summary>
        [Parameter]
        public string WorkingDirectory { get; set; } = "";

        /// <summary>
        /// Callback when the terminal container is closed
        /// </summary>
        [Parameter]
        public EventCallback OnClose { get; set; }

        /// <summary>
        /// Callback when terminal visibility/minimized state changes
        /// </summary>
        [Parameter]
        public EventCallback OnStateChanged { get; set; }

        #endregion

        #region Class Variables

        private bool _isVisible = false;

        private bool _isMinimized = false;

        private bool _isDisposed = false;

        private bool _windowDragInitialized = false;

        private readonly List<TerminalSession> _sessions = new List<TerminalSession>();

        private int _activeSessionId = 0;

        private int _nextSessionId = 1;

        private int _nextSessionNumber = 1;

        private IJSObjectReference _jsModule;

        private IJSObjectReference _interopModule;

        private DotNetObjectReference<TerminalPanel> _dotNetRef;

        private ConfirmDialog _confirmDialog;

        private ConfirmAction _pendingConfirmAction = ConfirmAction.None;

        private int _pendingCloseSessionId = 0;

        private int _renamingSessionId = 0;

        private string _renameText = "";

        private ElementReference _renameInputElement;

        private int _windowLeft = 100;

        private int _windowTop = 80;

        private int _windowWidth = 800;

        private int _windowHeight = 400;

        #endregion

        #region Properties

        /// <summary>
        /// Active terminal session
        /// </summary>
        private TerminalSession ActiveSession
        {
            get
            {
                return this.FindSession(this._activeSessionId);
            }
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Shows the terminal window and creates a tab if needed
        /// </summary>
        public void Show()
        {
            if (this._isDisposed)
            {
                return;
            }

            this.FinishRename(true);
            this._isVisible = true;
            this._isMinimized = false;
            this.StateHasChanged();
            _ = this.OnStateChanged.InvokeAsync();

            if (this._sessions.Count == 0)
            {
                _ = this.CreateTab();
            }
            else
            {
                _ = this.ShowActiveSession(true);
            }
        }

        /// <summary>
        /// Minimizes the terminal window without stopping shell sessions
        /// </summary>
        public void Hide()
        {
            this.Minimize();
        }

        /// <summary>
        /// Minimizes the terminal window without stopping shell sessions
        /// </summary>
        public void Minimize()
        {
            if (this._isDisposed)
            {
                return;
            }

            this.FinishRename(true);
            this._isVisible = false;
            this._isMinimized = this._sessions.Count > 0;
            this.StateHasChanged();
            _ = this.OnStateChanged.InvokeAsync();
        }

        /// <summary>
        /// Restores a minimized terminal window
        /// </summary>
        public void Restore()
        {
            if (this._isDisposed)
            {
                return;
            }

            this.FinishRename(true);
            this._isVisible = true;
            this._isMinimized = false;
            this.StateHasChanged();
            _ = this.ShowActiveSession(true);
            _ = this.OnStateChanged.InvokeAsync();
        }

        /// <summary>
        /// Returns whether the terminal is currently visible
        /// </summary>
        /// <returns>True if visible</returns>
        public bool IsVisible()
        {
            return this._isVisible;
        }

        /// <summary>
        /// Returns whether the terminal is minimized
        /// </summary>
        /// <returns>True if minimized</returns>
        public bool IsMinimized()
        {
            return this._isMinimized;
        }

        /// <summary>
        /// Toggles terminal visibility
        /// </summary>
        public void Toggle()
        {
            if (this._isVisible)
            {
                this.Minimize();
            }
            else if (this._isMinimized)
            {
                this.Restore();
            }
            else
            {
                this.Show();
            }
        }

        #endregion

        #region JS Invokable Methods

        /// <summary>
        /// Receives terminal input from xterm.js
        /// </summary>
        /// <param name="sessionId">Terminal session id</param>
        /// <param name="data">Input data from the terminal</param>
        [JSInvokable]
        public void OnTerminalInput(int sessionId, string data)
        {
            TerminalSession session = this.FindSession(sessionId);
            if (session == null)
            {
                return;
            }

            if (session.ShellService != null && session.ShellService.IsRunning)
            {
                session.ShellService.SendInput(data);
            }
            else if (!this._isDisposed && !session.RestartPending && (session.ShellExited || session.ShellService == null))
            {
                session.RestartPending = true;
                _ = this.RestartShell(session);
            }
        }

        /// <summary>
        /// Receives terminal resize events from xterm.js
        /// </summary>
        /// <param name="sessionId">Terminal session id</param>
        /// <param name="cols">New column count</param>
        /// <param name="rows">New row count</param>
        [JSInvokable]
        public void OnTerminalResize(int sessionId, int cols, int rows)
        {
            TerminalSession session = this.FindSession(sessionId);
            if (session == null)
            {
                return;
            }

            session.Cols = Math.Max(20, cols);
            session.Rows = Math.Max(5, rows);
            if (session.ShellService != null && session.ShellService.IsRunning)
            {
                session.ShellService.Resize(session.Cols, session.Rows);
            }
        }

        #endregion

        #region Private Methods - UI

        /// <summary>
        /// Creates a new terminal tab
        /// </summary>
        private async System.Threading.Tasks.Task CreateTab()
        {
            if (this._isDisposed)
            {
                return;
            }

            this.FinishRename(true);
            TerminalSession session = new TerminalSession();
            session.Id = this._nextSessionId++;
            session.Label = "Term" + this._nextSessionNumber++;
            session.WorkingDirectory = string.IsNullOrWhiteSpace(this.WorkingDirectory) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : this.WorkingDirectory;
            this._sessions.Add(session);
            this._activeSessionId = session.Id;
            this._isVisible = true;
            this._isMinimized = false;
            this.StateHasChanged();
            await this.OnStateChanged.InvokeAsync();
            await this.InitializeSession(session, true, true);
        }

        /// <summary>
        /// Selects the active terminal tab
        /// </summary>
        /// <param name="sessionId">Terminal session id</param>
        private void SelectTab(int sessionId)
        {
            TerminalSession session = this.FindSession(sessionId);
            if (session == null)
            {
                return;
            }

            this.FinishRename(true);
            session.HasUnreadOutput = false;
            if (this._activeSessionId != sessionId)
            {
                this._activeSessionId = sessionId;
                this.StateHasChanged();
            }

            _ = this.ShowActiveSession(true);
        }

        /// <summary>
        /// Requests closing the terminal container
        /// </summary>
        private void RequestCloseContainer()
        {
            this.FinishRename(true);
            if (this.HasRunningSessions())
            {
                this._pendingConfirmAction = ConfirmAction.CloseAll;
                this._pendingCloseSessionId = 0;
                this._confirmDialog.Show("Close terminal sessions?", "Closing the terminal window will stop all running shell sessions.", "Close all", "Cancel");
            }
            else
            {
                _ = this.CloseAllTabs();
            }
        }

        /// <summary>
        /// Requests closing a single terminal tab
        /// </summary>
        /// <param name="sessionId">Terminal session id</param>
        private async System.Threading.Tasks.Task RequestCloseTab(int sessionId)
        {
            TerminalSession session = this.FindSession(sessionId);
            if (session == null)
            {
                return;
            }

            this.FinishRename(true);
            if (this.IsSessionRunning(session))
            {
                this._pendingConfirmAction = ConfirmAction.CloseTab;
                this._pendingCloseSessionId = sessionId;
                this._confirmDialog.Show("Close " + session.Label + "?", "Closing " + session.Label + " will stop its shell session.", "Close tab", "Cancel");
            }
            else
            {
                await this.CloseTab(sessionId);
            }
        }

        /// <summary>
        /// Handles confirmation dialog close
        /// </summary>
        /// <param name="confirmed">Whether the action was confirmed</param>
        private async System.Threading.Tasks.Task HandleConfirmClose(bool confirmed)
        {
            ConfirmAction action = this._pendingConfirmAction;
            int sessionId = this._pendingCloseSessionId;
            this._pendingConfirmAction = ConfirmAction.None;
            this._pendingCloseSessionId = 0;

            if (!confirmed)
            {
                return;
            }

            if (action == ConfirmAction.CloseAll)
            {
                await this.CloseAllTabs();
            }
            else if (action == ConfirmAction.CloseTab)
            {
                await this.CloseTab(sessionId);
            }
        }

        /// <summary>
        /// Starts inline rename for a tab
        /// </summary>
        /// <param name="session">Terminal session</param>
        private void BeginRename(TerminalSession session)
        {
            if (session == null)
            {
                return;
            }

            this._renamingSessionId = session.Id;
            this._renameText = session.Label;
            this.StateHasChanged();
            _ = this.FocusRenameInputAsync();
        }

        /// <summary>
        /// Focuses the inline rename input after render
        /// </summary>
        private async System.Threading.Tasks.Task FocusRenameInputAsync()
        {
            await System.Threading.Tasks.Task.Delay(50);
            if (this._renamingSessionId == 0 || this._isDisposed)
            {
                return;
            }

            try
            {
                await this._renameInputElement.FocusAsync();
            }
            catch (JSDisconnectedException)
            {
                // Circuit disconnected
            }
            catch (ObjectDisposedException)
            {
                // Component disposed
            }
            catch (InvalidOperationException)
            {
                // Element not available anymore
            }
        }

        /// <summary>
        /// Handles rename input changes
        /// </summary>
        /// <param name="args">Change event args</param>
        private void HandleRenameInput(ChangeEventArgs args)
        {
            this._renameText = args.Value == null ? "" : args.Value.ToString();
        }

        /// <summary>
        /// Handles rename keyboard actions
        /// </summary>
        /// <param name="args">Keyboard event args</param>
        private void HandleRenameKeyDown(KeyboardEventArgs args)
        {
            if (args.Key == "Enter")
            {
                this.FinishRename(true);
            }
            else if (args.Key == "Escape")
            {
                this.FinishRename(false);
            }
        }

        /// <summary>
        /// Finishes inline rename
        /// </summary>
        /// <param name="save">Whether to save the value</param>
        private void FinishRename(bool save)
        {
            if (this._renamingSessionId == 0)
            {
                return;
            }

            TerminalSession session = this.FindSession(this._renamingSessionId);
            if (session != null && save && !string.IsNullOrWhiteSpace(this._renameText))
            {
                session.Label = this._renameText.Trim();
            }

            this._renamingSessionId = 0;
            this._renameText = "";
            this.StateHasChanged();
        }

        /// <summary>
        /// Returns the CSS class for a tab
        /// </summary>
        /// <param name="session">Terminal session</param>
        /// <returns>CSS class</returns>
        private string GetTabCssClass(TerminalSession session)
        {
            string result = "terminal-tab";
            if (session.Id == this._activeSessionId)
            {
                result += " active";
            }
            if (session.ShellExited)
            {
                result += " exited";
            }
            if (session.HasUnreadOutput && session.Id != this._activeSessionId)
            {
                result += " unread";
            }

            return result;
        }

        #endregion

        #region Private Methods - Session Lifecycle

        /// <summary>
        /// Initializes JS and shell for the active session
        /// </summary>
        /// <param name="focus">Whether to focus the terminal</param>
        private async System.Threading.Tasks.Task ShowActiveSession(bool focus)
        {
            TerminalSession session = this.ActiveSession;
            if (session == null)
            {
                return;
            }

            await this.InitializeSession(session, true, focus);
        }

        /// <summary>
        /// Initializes JS and shell for a session
        /// </summary>
        /// <param name="session">Terminal session</param>
        /// <param name="startShell">Whether to start the shell</param>
        /// <param name="focus">Whether to focus the terminal</param>
        private async System.Threading.Tasks.Task InitializeSession(TerminalSession session, bool startShell, bool focus)
        {
            if (this._isDisposed || session == null)
            {
                return;
            }

            try
            {
                await this.EnsureJsModules();
                await System.Threading.Tasks.Task.Delay(50);
                if (!session.JsInitialized)
                {
                    int[] terminalSize = await this._jsModule.InvokeAsync<int[]>("initTerminal", session.Id, "terminal-container-" + session.Id, this._dotNetRef);
                    if (terminalSize != null && terminalSize.Length >= 2)
                    {
                        session.Cols = Math.Max(20, terminalSize[0]);
                        session.Rows = Math.Max(5, terminalSize[1]);
                    }

                    session.JsInitialized = true;
                }
                else if (session.Id == this._activeSessionId)
                {
                    int[] terminalSize = await this._jsModule.InvokeAsync<int[]>("fitTerminal", session.Id);
                    if (terminalSize != null && terminalSize.Length >= 2)
                    {
                        session.Cols = Math.Max(20, terminalSize[0]);
                        session.Rows = Math.Max(5, terminalSize[1]);
                    }
                }

                if (startShell)
                {
                    this.StartShell(session);
                }

                if (focus && session.Id == this._activeSessionId)
                {
                    await this._jsModule.InvokeVoidAsync("focusTerminal", session.Id);
                }
            }
            catch (JSDisconnectedException)
            {
                // Circuit disconnected
            }
            catch (ObjectDisposedException)
            {
                // Component disposed
            }
        }

        /// <summary>
        /// Ensures JS modules are loaded
        /// </summary>
        private async System.Threading.Tasks.Task EnsureJsModules()
        {
            if (this._jsModule == null)
            {
                this._jsModule = await this.JSRuntime.InvokeAsync<IJSObjectReference>("import", "./js/terminal.js?v=20260716-tab-focus");
            }

            if (this._interopModule == null)
            {
                this._interopModule = await this.JSRuntime.InvokeAsync<IJSObjectReference>("import", "./js/interop.js?v=20260612-tabs");
            }

            if (this._dotNetRef == null)
            {
                this._dotNetRef = DotNetObjectReference.Create(this);
            }

            if (!this._windowDragInitialized)
            {
                await this._interopModule.InvokeVoidAsync("initWindowDrag", "terminal-window", "terminal-titlebar", "terminal-resize-handle");
                this._windowDragInitialized = true;
            }
        }

        /// <summary>
        /// Starts the shell for a session
        /// </summary>
        /// <param name="session">Terminal session</param>
        private void StartShell(TerminalSession session)
        {
            if (this._isDisposed || session == null)
            {
                return;
            }
            if (session.ShellService != null && session.ShellService.IsRunning)
            {
                return;
            }
            if (session.ShellService != null)
            {
                session.ShellService.Dispose();
                session.ShellService = null;
            }

            session.ShellService = new ShellService();
            session.ShellExited = false;
            session.RestartPending = false;
            int shellGeneration = this.AdvanceShellGeneration(session);
            string workDir = string.IsNullOrWhiteSpace(session.WorkingDirectory) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : session.WorkingDirectory;
            try
            {
                session.ShellService.Start(workDir, session.Cols, session.Rows, data => this.HandleShellOutput(session, shellGeneration, data), () => this.HandleShellExit(session, shellGeneration));
            }
            catch (Exception ex)
            {
                session.ShellExited = true;
                session.ShellService.Dispose();
                session.ShellService = null;
                this.HandleShellOutput(session, shellGeneration, "\r\n[Failed to start terminal: " + ex.Message + "]\r\n");
            }
        }

        /// <summary>
        /// Restarts a shell after exit
        /// </summary>
        /// <param name="session">Terminal session</param>
        private async System.Threading.Tasks.Task RestartShell(TerminalSession session)
        {
            try
            {
                await this.InvokeAsync(async () =>
                {
                    if (this._jsModule != null && session.JsInitialized)
                    {
                        await this._jsModule.InvokeVoidAsync("resetTerminal", session.Id);
                    }

                    this.ClearPendingOutput(session);
                    this.StartShell(session);
                    if (this._jsModule != null && session.JsInitialized && session.Id == this._activeSessionId)
                    {
                        await this._jsModule.InvokeVoidAsync("focusTerminal", session.Id);
                    }
                });
            }
            catch (JSDisconnectedException)
            {
                // Circuit disconnected
            }
            catch (ObjectDisposedException)
            {
                // Component disposed
            }
            finally
            {
                session.RestartPending = false;
            }
        }

        /// <summary>
        /// Closes all terminal tabs
        /// </summary>
        private async System.Threading.Tasks.Task CloseAllTabs()
        {
            foreach (TerminalSession session in this._sessions)
            {
                this.StopSession(session);
            }

            if (this._jsModule != null)
            {
                try
                {
                    await this._jsModule.InvokeVoidAsync("disposeAllTerminals");
                }
                catch (JSDisconnectedException)
                {
                    // Circuit disconnected
                }
                catch (ObjectDisposedException)
                {
                    // Module disposed
                }
            }

            this._sessions.Clear();
            this._activeSessionId = 0;
            this._nextSessionId = 1;
            this._nextSessionNumber = 1;
            this._isVisible = false;
            this._isMinimized = false;
            this.StateHasChanged();
            await this.OnClose.InvokeAsync();
            await this.OnStateChanged.InvokeAsync();
        }

        /// <summary>
        /// Closes a single terminal tab
        /// </summary>
        /// <param name="sessionId">Terminal session id</param>
        private async System.Threading.Tasks.Task CloseTab(int sessionId)
        {
            int index = this._sessions.FindIndex(item => item.Id == sessionId);
            if (index < 0)
            {
                return;
            }

            bool wasActive = this._activeSessionId == sessionId;
            TerminalSession session = this._sessions[index];
            this.StopSession(session);
            if (this._jsModule != null && session.JsInitialized)
            {
                try
                {
                    await this._jsModule.InvokeVoidAsync("disposeTerminal", session.Id);
                }
                catch (JSDisconnectedException)
                {
                    // Circuit disconnected
                }
                catch (ObjectDisposedException)
                {
                    // Module disposed
                }
            }

            this._sessions.RemoveAt(index);
            if (this._sessions.Count == 0)
            {
                this._activeSessionId = 0;
                this._nextSessionId = 1;
                this._nextSessionNumber = 1;
                this._isVisible = false;
                this._isMinimized = false;
                this.StateHasChanged();
                await this.OnClose.InvokeAsync();
                await this.OnStateChanged.InvokeAsync();
                return;
            }

            if (wasActive)
            {
                int newIndex = Math.Min(index, this._sessions.Count - 1);
                this._activeSessionId = this._sessions[newIndex].Id;
                this._sessions[newIndex].HasUnreadOutput = false;
                this.StateHasChanged();
                await this.ShowActiveSession(true);
            }

            this.StateHasChanged();
            await this.OnStateChanged.InvokeAsync();
        }

        /// <summary>
        /// Stops a session shell
        /// </summary>
        /// <param name="session">Terminal session</param>
        private void StopSession(TerminalSession session)
        {
            if (session == null)
            {
                return;
            }

            this.AdvanceShellGeneration(session);
            if (session.ShellService != null)
            {
                session.ShellService.Dispose();
                session.ShellService = null;
            }

            session.ShellExited = false;
            session.RestartPending = false;
            this.ClearPendingOutput(session);
        }

        #endregion

        #region Private Methods - Output

        /// <summary>
        /// Handles output from a shell process
        /// </summary>
        /// <param name="session">Terminal session</param>
        /// <param name="shellGeneration">Shell generation that produced the output</param>
        /// <param name="data">Shell output data</param>
        private void HandleShellOutput(TerminalSession session, int shellGeneration, string data)
        {
            if (!this.IsCurrentShellGeneration(session, shellGeneration) || this._jsModule == null || !session.JsInitialized || string.IsNullOrEmpty(data) || this._isDisposed)
            {
                return;
            }

            if (session.Id != this._activeSessionId && !session.HasUnreadOutput)
            {
                _ = this.MarkSessionUnread(session, shellGeneration);
            }

            int offset = 0;
            while (offset < data.Length && !this._isDisposed)
            {
                bool flushNow = false;
                bool scheduleFlush = false;
                lock (session.OutputLock)
                {
                    while (session.PendingOutput.Length >= OUTPUT_BUFFER_LIMIT && session.ShellGeneration == shellGeneration && !this._isDisposed)
                    {
                        Monitor.Wait(session.OutputLock, OUTPUT_FLUSH_DELAY_MS);
                    }

                    if (this._isDisposed || session.ShellGeneration != shellGeneration)
                    {
                        return;
                    }

                    int available = Math.Max(1, OUTPUT_BUFFER_LIMIT - session.PendingOutput.Length);
                    int count = Math.Min(available, data.Length - offset);
                    session.PendingOutput.Append(data, offset, count);
                    offset += count;
                    if (session.PendingOutput.Length >= OUTPUT_FLUSH_THRESHOLD)
                    {
                        if (!session.OutputFlushScheduled)
                        {
                            session.OutputFlushScheduled = true;
                            flushNow = true;
                        }
                    }
                    else if (!session.OutputFlushScheduled)
                    {
                        session.OutputFlushScheduled = true;
                        scheduleFlush = true;
                    }
                }

                if (flushNow)
                {
                    _ = this.FlushTerminalOutput(session, shellGeneration);
                }
                else if (scheduleFlush)
                {
                    _ = this.ScheduleTerminalOutputFlush(session, shellGeneration);
                }
            }
        }

        /// <summary>
        /// Handles shell process exit
        /// </summary>
        /// <param name="session">Terminal session</param>
        /// <param name="shellGeneration">Shell generation that exited</param>
        private void HandleShellExit(TerminalSession session, int shellGeneration)
        {
            if (!this.IsCurrentShellGeneration(session, shellGeneration))
            {
                return;
            }

            _ = this.InvokeAsync(() =>
            {
                if (!this.IsCurrentShellGeneration(session, shellGeneration))
                {
                    return;
                }

                session.ShellExited = true;
                if (session.Id != this._activeSessionId)
                {
                    session.HasUnreadOutput = true;
                }

                this.StateHasChanged();
                _ = this.HandleShellExitAsync(session, shellGeneration);
            });
        }

        /// <summary>
        /// Writes the shell exit message after pending output has been flushed
        /// </summary>
        /// <param name="session">Terminal session</param>
        /// <param name="shellGeneration">Shell generation that exited</param>
        private async System.Threading.Tasks.Task HandleShellExitAsync(TerminalSession session, int shellGeneration)
        {
            await this.DrainTerminalOutput(session, shellGeneration);
            if (this.IsCurrentShellGeneration(session, shellGeneration) && this._jsModule != null && session.JsInitialized && !this._isDisposed)
            {
                try
                {
                    await this._jsModule.InvokeVoidAsync("writeTerminal", session.Id, "\r\n[Process exited. Press any key to restart]\r\n");
                }
                catch (JSDisconnectedException)
                {
                    // Circuit disconnected
                }
                catch (ObjectDisposedException)
                {
                    // Component disposed
                }
            }
        }

        /// <summary>
        /// Schedules a buffered output flush
        /// </summary>
        /// <param name="session">Terminal session</param>
        /// <param name="shellGeneration">Shell generation that scheduled the flush</param>
        private async System.Threading.Tasks.Task ScheduleTerminalOutputFlush(TerminalSession session, int shellGeneration)
        {
            await System.Threading.Tasks.Task.Delay(OUTPUT_FLUSH_DELAY_MS);
            await this.FlushTerminalOutput(session, shellGeneration);
        }

        /// <summary>
        /// Flushes buffered terminal output to xterm.js
        /// </summary>
        /// <param name="session">Terminal session</param>
        /// <param name="shellGeneration">Shell generation that produced the output</param>
        private async System.Threading.Tasks.Task FlushTerminalOutput(TerminalSession session, int shellGeneration)
        {
            string data;
            bool scheduleNextFlush = false;
            int nextShellGeneration = shellGeneration;
            lock (session.OutputLock)
            {
                if (session.ShellGeneration != shellGeneration)
                {
                    return;
                }
                if (session.OutputFlushInProgress)
                {
                    session.OutputFlushScheduled = false;
                    return;
                }
                if (session.PendingOutput.Length == 0)
                {
                    session.OutputFlushScheduled = false;
                    Monitor.PulseAll(session.OutputLock);
                    return;
                }

                session.OutputFlushInProgress = true;
                session.OutputFlushScheduled = false;
                data = session.PendingOutput.ToString();
                session.PendingOutput.Clear();
                Monitor.PulseAll(session.OutputLock);
            }

            if (this.IsCurrentShellGeneration(session, shellGeneration) && this._jsModule != null && session.JsInitialized && !this._isDisposed)
            {
                try
                {
                    await this.InvokeAsync(async () => await this._jsModule.InvokeVoidAsync("writeTerminal", session.Id, data));
                }
                catch (JSDisconnectedException)
                {
                    // Circuit disconnected
                }
                catch (ObjectDisposedException)
                {
                    // Component disposed
                }
            }

            lock (session.OutputLock)
            {
                session.OutputFlushInProgress = false;
                Monitor.PulseAll(session.OutputLock);
                if (session.PendingOutput.Length > 0 && !session.OutputFlushScheduled && !this._isDisposed)
                {
                    nextShellGeneration = session.ShellGeneration;
                    session.OutputFlushScheduled = true;
                    scheduleNextFlush = true;
                }
            }

            if (scheduleNextFlush)
            {
                _ = this.ScheduleTerminalOutputFlush(session, nextShellGeneration);
            }
        }

        /// <summary>
        /// Waits until buffered terminal output has been written
        /// </summary>
        /// <param name="session">Terminal session</param>
        /// <param name="shellGeneration">Shell generation whose output must be drained</param>
        private async System.Threading.Tasks.Task DrainTerminalOutput(TerminalSession session, int shellGeneration)
        {
            for (int i = 0; i < 200 && !this._isDisposed; i++)
            {
                bool done;
                bool flushNeeded = false;
                lock (session.OutputLock)
                {
                    if (session.ShellGeneration != shellGeneration)
                    {
                        return;
                    }

                    done = session.PendingOutput.Length == 0 && !session.OutputFlushInProgress;
                    if (!done && session.PendingOutput.Length > 0 && !session.OutputFlushScheduled)
                    {
                        session.OutputFlushScheduled = true;
                        flushNeeded = true;
                    }
                }

                if (done)
                {
                    return;
                }
                if (flushNeeded)
                {
                    await this.FlushTerminalOutput(session, shellGeneration);
                }

                await System.Threading.Tasks.Task.Delay(5);
            }
        }

        /// <summary>
        /// Clears buffered terminal output
        /// </summary>
        /// <param name="session">Terminal session</param>
        private void ClearPendingOutput(TerminalSession session)
        {
            lock (session.OutputLock)
            {
                session.PendingOutput.Clear();
                session.OutputFlushScheduled = false;
                Monitor.PulseAll(session.OutputLock);
            }
        }

        #endregion

        #region Private Methods - Helpers

        /// <summary>
        /// Advances the shell generation and wakes pending output callbacks
        /// </summary>
        /// <param name="session">Terminal session</param>
        /// <returns>New shell generation</returns>
        private int AdvanceShellGeneration(TerminalSession session)
        {
            lock (session.OutputLock)
            {
                session.ShellGeneration++;
                Monitor.PulseAll(session.OutputLock);
                return session.ShellGeneration;
            }
        }

        /// <summary>
        /// Returns whether a callback belongs to the current shell generation
        /// </summary>
        /// <param name="session">Terminal session</param>
        /// <param name="shellGeneration">Shell generation captured by the callback</param>
        /// <returns>True if the callback still belongs to the active shell</returns>
        private bool IsCurrentShellGeneration(TerminalSession session, int shellGeneration)
        {
            if (session == null || this._isDisposed)
            {
                return false;
            }

            lock (session.OutputLock)
            {
                return session.ShellGeneration == shellGeneration;
            }
        }

        /// <summary>
        /// Marks an inactive session as having unread output on the renderer thread
        /// </summary>
        /// <param name="session">Terminal session</param>
        /// <param name="shellGeneration">Shell generation that produced the output</param>
        private async System.Threading.Tasks.Task MarkSessionUnread(TerminalSession session, int shellGeneration)
        {
            try
            {
                await this.InvokeAsync(() =>
                {
                    if (!this.IsCurrentShellGeneration(session, shellGeneration) || session.Id == this._activeSessionId || session.HasUnreadOutput)
                    {
                        return;
                    }

                    session.HasUnreadOutput = true;
                    this.StateHasChanged();
                });
            }
            catch (ObjectDisposedException)
            {
                // Component disposed
            }
        }

        /// <summary>
        /// Finds a session by id
        /// </summary>
        /// <param name="sessionId">Terminal session id</param>
        /// <returns>Terminal session or null</returns>
        private TerminalSession FindSession(int sessionId)
        {
            return this._sessions.FirstOrDefault(item => item.Id == sessionId);
        }

        /// <summary>
        /// Returns whether a session is running
        /// </summary>
        /// <param name="session">Terminal session</param>
        /// <returns>True if running</returns>
        private bool IsSessionRunning(TerminalSession session)
        {
            return session != null && session.ShellService != null && session.ShellService.IsRunning;
        }

        /// <summary>
        /// Returns whether any session is running
        /// </summary>
        /// <returns>True if at least one shell is running</returns>
        private bool HasRunningSessions()
        {
            return this._sessions.Any(this.IsSessionRunning);
        }

        /// <summary>
        /// Disposes a JS module reference ignoring circuit shutdown races
        /// </summary>
        /// <param name="module">JS module reference</param>
        private async System.Threading.Tasks.Task DisposeJsModuleAsync(IJSObjectReference module)
        {
            if (module == null)
            {
                return;
            }

            try
            {
                await module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // Circuit disconnected
            }
            catch (ObjectDisposedException)
            {
                // Module already disposed
            }
        }

        /// <summary>
        /// Disposes all terminal JS instances and module ignoring circuit shutdown races
        /// </summary>
        /// <param name="module">Terminal JS module reference</param>
        private async System.Threading.Tasks.Task DisposeTerminalModuleAsync(IJSObjectReference module)
        {
            if (module == null)
            {
                return;
            }

            try
            {
                await module.InvokeVoidAsync("disposeAllTerminals");
                await module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // Circuit disconnected
            }
            catch (ObjectDisposedException)
            {
                // Module already disposed
            }
        }

        #endregion

        #region IDisposable

        /// <summary>
        /// Cleanup shell processes and JS references
        /// </summary>
        public void Dispose()
        {
            this._isDisposed = true;
            foreach (TerminalSession session in this._sessions)
            {
                this.StopSession(session);
            }

            IJSObjectReference jsModule = this._jsModule;
            IJSObjectReference interopModule = this._interopModule;
            this._jsModule = null;
            this._interopModule = null;

            if (jsModule != null)
            {
                _ = this.DisposeTerminalModuleAsync(jsModule);
            }
            if (interopModule != null)
            {
                _ = this.DisposeJsModuleAsync(interopModule);
            }
            if (this._dotNetRef != null)
            {
                this._dotNetRef.Dispose();
                this._dotNetRef = null;
            }
        }

        #endregion

        #region Nested Classes

        /// <summary>
        /// Pending confirmation action
        /// </summary>
        private enum ConfirmAction
        {
            None,
            CloseAll,
            CloseTab
        }

        /// <summary>
        /// Terminal tab state
        /// </summary>
        private class TerminalSession
        {
            public int Id { get; set; }

            public string Label { get; set; }

            public string WorkingDirectory { get; set; }

            public ShellService ShellService { get; set; }

            public bool ShellExited { get; set; }

            public bool RestartPending { get; set; }

            public bool JsInitialized { get; set; }

            public bool HasUnreadOutput { get; set; }

            public int ShellGeneration { get; set; }

            public int Cols { get; set; } = 120;

            public int Rows { get; set; } = 30;

            public object OutputLock { get; } = new object();

            public StringBuilder PendingOutput { get; } = new StringBuilder();

            public bool OutputFlushScheduled { get; set; }

            public bool OutputFlushInProgress { get; set; }
        }

        #endregion
    }
}
