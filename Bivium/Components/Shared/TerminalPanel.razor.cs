using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Bivium.Services;

namespace Bivium.Components.Shared
{
    /// <summary>
    /// Terminal window with embedded xterm.js and shell process
    /// </summary>
    public partial class TerminalPanel : ComponentBase, IDisposable
    {
        #region Parameters

        /// <summary>
        /// Working directory for the shell
        /// </summary>
        [Parameter]
        public string WorkingDirectory { get; set; } = "";

        /// <summary>
        /// Callback when the terminal is closed
        /// </summary>
        [Parameter]
        public EventCallback OnClose { get; set; }

        #endregion

        #region Class Variables

        /// <summary>
        /// Whether the terminal window is visible
        /// </summary>
        private bool _isVisible = false;

        /// <summary>
        /// Shell process service
        /// </summary>
        private ShellService _shellService;

        /// <summary>
        /// JS module reference for terminal interop
        /// </summary>
        private IJSObjectReference _jsModule;

        /// <summary>
        /// JS module reference for drag/resize interop
        /// </summary>
        private IJSObjectReference _interopModule;

        /// <summary>
        /// .NET object reference for JS callbacks
        /// </summary>
        private DotNetObjectReference<TerminalPanel> _dotNetRef;

        /// <summary>
        /// Whether JS terminal has been initialized
        /// </summary>
        private bool _jsInitialized = false;

        /// <summary>
        /// Display name of the detected shell
        /// </summary>
        private string _shellName = "Shell";

        /// <summary>
        /// Window left position in pixels
        /// </summary>
        private int _windowLeft = 100;

        /// <summary>
        /// Window top position in pixels
        /// </summary>
        private int _windowTop = 80;

        /// <summary>
        /// Window width in pixels
        /// </summary>
        private int _windowWidth = 800;

        /// <summary>
        /// Window height in pixels
        /// </summary>
        private int _windowHeight = 400;

        #endregion

        #region Public Methods

        /// <summary>
        /// Shows the terminal window and starts the shell if not running
        /// </summary>
        public void Show()
        {
            this._isVisible = true;
            this.StateHasChanged();

            // Initialize after render
            _ = this.InitializeTerminal();
        }

        /// <summary>
        /// Hides the terminal window
        /// </summary>
        public void Hide()
        {
            this._isVisible = false;
            this.StateHasChanged();
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
        /// Toggles terminal visibility
        /// </summary>
        public void Toggle()
        {
            if (this._isVisible)
            {
                this.Hide();
            }
            else
            {
                this.Show();
            }
        }

        #endregion

        #region JS Invokable Methods

        /// <summary>
        /// Receives terminal input from xterm.js (user keystrokes)
        /// </summary>
        /// <param name="data">Input data from the terminal</param>
        [JSInvokable]
        public void OnTerminalInput(string data)
        {
            if (this._shellService != null && this._shellService.IsRunning)
            {
                this._shellService.SendInput(data);
            }
        }

        /// <summary>
        /// Receives terminal resize events from xterm.js
        /// </summary>
        /// <param name="cols">New column count</param>
        /// <param name="rows">New row count</param>
        [JSInvokable]
        public void OnTerminalResize(int cols, int rows)
        {
            if (this._shellService != null && this._shellService.IsRunning)
            {
                this._shellService.Resize(cols, rows);
            }
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Initializes the xterm.js terminal and starts the shell process
        /// </summary>
        private async System.Threading.Tasks.Task InitializeTerminal()
        {
            // Wait for the DOM to be ready
            await System.Threading.Tasks.Task.Delay(100);

            // Initialize JS modules
            if (this._jsModule == null)
            {
                this._jsModule = await this.JSRuntime.InvokeAsync<IJSObjectReference>("import", "./js/terminal.js");
            }

            if (this._interopModule == null)
            {
                this._interopModule = await this.JSRuntime.InvokeAsync<IJSObjectReference>("import", "./js/interop.js");
            }

            // Initialize xterm.js
            if (!this._jsInitialized)
            {
                this._dotNetRef = DotNetObjectReference.Create(this);
                await this._jsModule.InvokeVoidAsync("initTerminal", "terminal-container", this._dotNetRef);

                // Initialize drag and resize for the window
                await this._interopModule.InvokeVoidAsync("initWindowDrag", "terminal-window", "terminal-titlebar", "terminal-resize-handle");

                this._jsInitialized = true;
            }

            // Start shell process if not running
            if (this._shellService == null || !this._shellService.IsRunning)
            {
                this._shellService = new ShellService();

                string workDir = this.WorkingDirectory;
                if (string.IsNullOrEmpty(workDir))
                {
                    workDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                }

                this._shellService.Start(workDir, this.HandleShellOutput, this.HandleShellExit);
                this._shellName = "Shell";
            }

            // Focus the terminal
            await this._jsModule.InvokeVoidAsync("focusTerminal");
        }

        /// <summary>
        /// Handles output from the shell process - writes to xterm.js
        /// </summary>
        /// <param name="data">Shell output data</param>
        private void HandleShellOutput(string data)
        {
            if (this._jsModule != null && this._jsInitialized)
            {
                // Must use InvokeVoidAsync from non-UI thread
                _ = this._jsModule.InvokeVoidAsync("writeTerminal", data);
            }
        }

        /// <summary>
        /// Handles shell process exit
        /// </summary>
        private void HandleShellExit()
        {
            if (this._jsModule != null && this._jsInitialized)
            {
                _ = this._jsModule.InvokeVoidAsync("writeTerminal", "\r\n[Process exited. Press any key to restart]\r\n");
            }
        }

        /// <summary>
        /// Handles the close button click
        /// </summary>
        private void HandleClose()
        {
            this._isVisible = false;
            this.OnClose.InvokeAsync();
        }

        #endregion

        #region IDisposable

        /// <summary>
        /// Cleanup shell process and JS references
        /// </summary>
        public void Dispose()
        {
            if (this._shellService != null)
            {
                this._shellService.Dispose();
                this._shellService = null;
            }

            if (this._jsModule != null && this._jsInitialized)
            {
                _ = this._jsModule.InvokeVoidAsync("disposeTerminal");
            }

            if (this._dotNetRef != null)
            {
                this._dotNetRef.Dispose();
                this._dotNetRef = null;
            }
        }

        #endregion
    }
}
