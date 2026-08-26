using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Bivium.Components.Shared
{
    /// <summary>
    /// Top menu bar with dropdown menus (File, Edit, View, Settings, Help)
    /// </summary>
    public partial class MenuBar : ComponentBase
    {
        #region Injected Services

        /// <summary>
        /// JS runtime for theme interop
        /// </summary>
        [Inject]
        private IJSRuntime _jsRuntime { get; set; }

        #endregion

        #region Parameters

        /// <summary>
        /// Callback for New File action
        /// </summary>
        [Parameter]
        public EventCallback OnNewFile { get; set; }

        /// <summary>
        /// Callback for New Folder action
        /// </summary>
        [Parameter]
        public EventCallback OnNewFolder { get; set; }

        /// <summary>
        /// Callback for Download action
        /// </summary>
        [Parameter]
        public EventCallback OnDownload { get; set; }

        /// <summary>
        /// Callback for Upload action
        /// </summary>
        [Parameter]
        public EventCallback OnUpload { get; set; }

        /// <summary>
        /// Callback for Exit action
        /// </summary>
        [Parameter]
        public EventCallback OnExit { get; set; }

        /// <summary>
        /// Callback for the explicit destructive workspace reset
        /// </summary>
        [Parameter]
        public EventCallback OnResetWorkspace { get; set; }

        /// <summary>
        /// Callback for Copy action
        /// </summary>
        [Parameter]
        public EventCallback OnCopy { get; set; }

        /// <summary>
        /// Callback for Cut action
        /// </summary>
        [Parameter]
        public EventCallback OnCut { get; set; }

        /// <summary>
        /// Callback for Paste action
        /// </summary>
        [Parameter]
        public EventCallback OnPaste { get; set; }

        /// <summary>
        /// Callback for Delete action
        /// </summary>
        [Parameter]
        public EventCallback OnDelete { get; set; }

        /// <summary>
        /// Callback for Rename action
        /// </summary>
        [Parameter]
        public EventCallback OnRename { get; set; }

        /// <summary>
        /// Callback for Advanced Rename action
        /// </summary>
        [Parameter]
        public EventCallback OnAdvancedRename { get; set; }

        /// <summary>
        /// Callback for Select All action
        /// </summary>
        [Parameter]
        public EventCallback OnSelectAll { get; set; }

        /// <summary>
        /// Callback for Refresh action
        /// </summary>
        [Parameter]
        public EventCallback OnRefresh { get; set; }

        /// <summary>
        /// Callback for Terminal toggle action
        /// </summary>
        [Parameter]
        public EventCallback OnTerminal { get; set; }

        /// <summary>
        /// Callback for Editor Extensions action
        /// </summary>
        [Parameter]
        public EventCallback OnEditorExtensions { get; set; }

        /// <summary>
        /// Callback for default creation permissions action
        /// </summary>
        [Parameter]
        public EventCallback OnCreationPermissions { get; set; }

        /// <summary>
        /// Callback for Authentication Settings action
        /// </summary>
        [Parameter]
        public EventCallback OnAuthenticationSettings { get; set; }

        /// <summary>
        /// Callback for Logout action
        /// </summary>
        [Parameter]
        public EventCallback OnLogout { get; set; }

        /// <summary>
        /// Callback for About action
        /// </summary>
        [Parameter]
        public EventCallback OnAbout { get; set; }

        /// <summary>
        /// Callback for Extract action
        /// </summary>
        [Parameter]
        public EventCallback OnExtract { get; set; }

        /// <summary>
        /// Callback for Compress action
        /// </summary>
        [Parameter]
        public EventCallback OnCompress { get; set; }

        /// <summary>
        /// Callback for toggle single/dual panel mode
        /// </summary>
        [Parameter]
        public EventCallback OnToggleSinglePanel { get; set; }

        /// <summary>
        /// Whether single panel mode is active
        /// </summary>
        [Parameter]
        public bool SinglePanelMode { get; set; } = false;

        /// <summary>
        /// Whether the active panel has multiple selected entries
        /// </summary>
        [Parameter]
        public bool IsMultiSelection { get; set; } = false;

        /// <summary>
        /// Whether the active target can be extracted
        /// </summary>
        [Parameter]
        public bool CanExtract { get; set; } = false;

        /// <summary>
        /// Whether the active target can be compressed
        /// </summary>
        [Parameter]
        public bool CanCompress { get; set; } = false;

        /// <summary>
        /// Whether logout should be available
        /// </summary>
        [Parameter]
        public bool CanLogout { get; set; } = false;

        /// <summary>
        /// Whether authentication settings should be available
        /// </summary>
        [Parameter]
        public bool CanManageAuthenticationSettings { get; set; } = false;

        #endregion

        #region Class Variables

        /// <summary>
        /// Currently active (open) menu name, empty if none
        /// </summary>
        private string _activeMenu = "";

        /// <summary>
        /// JS module reference for theme switching
        /// </summary>
        private IJSObjectReference _jsThemeModule;

        /// <summary>
        /// Current theme name
        /// </summary>
        private string _currentTheme = "dark";

        #endregion

        #region Overrides

        /// <summary>
        /// Load saved theme on first render
        /// </summary>
        protected override void OnAfterRender(bool firstRender)
        {
            if (firstRender)
            {
                _ = this.LoadSavedTheme();
            }
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Loads the saved theme from localStorage via JS interop
        /// </summary>
        private async System.Threading.Tasks.Task LoadSavedTheme()
        {
            this._jsThemeModule = await this._jsRuntime.InvokeAsync<IJSObjectReference>("import", "./js/interop.js");
            string saved = await this._jsThemeModule.InvokeAsync<string>("loadSavedTheme");

            if (!string.IsNullOrEmpty(saved))
            {
                this._currentTheme = saved;
                this.StateHasChanged();
            }
        }

        /// <summary>
        /// Handles theme dropdown change
        /// </summary>
        /// <param name="args">Change event args</param>
        private void HandleThemeChange(ChangeEventArgs args)
        {
            string theme = args.Value.ToString();
            this._currentTheme = theme;

            if (this._jsThemeModule != null)
            {
                _ = this._jsThemeModule.InvokeVoidAsync("setTheme", theme);
            }
        }

        /// <summary>
        /// Toggles a dropdown menu open/closed
        /// </summary>
        /// <param name="menuName">Name of the menu to toggle</param>
        private void ToggleMenu(string menuName)
        {
            if (this._activeMenu == menuName)
            {
                this._activeMenu = "";
            }
            else
            {
                this._activeMenu = menuName;
            }
        }

        /// <summary>
        /// Switches to a different menu on hover (only if a menu is already open)
        /// </summary>
        /// <param name="menuName">Name of the menu being hovered</param>
        private void HoverMenu(string menuName)
        {
            // Only switch on hover if a menu is already open
            if (!string.IsNullOrEmpty(this._activeMenu))
            {
                this._activeMenu = menuName;
            }
        }

        /// <summary>
        /// Closes the active dropdown menu
        /// </summary>
        private void CloseMenu()
        {
            this._activeMenu = "";
        }

        /// <summary>
        /// Handles New File action
        /// </summary>
        private async System.Threading.Tasks.Task HandleNewFile()
        {
            await this.HandleAction(this.OnNewFile);
        }

        /// <summary>
        /// Handles New Folder action
        /// </summary>
        private async System.Threading.Tasks.Task HandleNewFolder()
        {
            await this.HandleAction(this.OnNewFolder);
        }

        /// <summary>
        /// Handles Download action
        /// </summary>
        private async System.Threading.Tasks.Task HandleDownload()
        {
            await this.HandleAction(this.OnDownload);
        }

        /// <summary>
        /// Handles Upload action
        /// </summary>
        private async System.Threading.Tasks.Task HandleUpload()
        {
            await this.HandleAction(this.OnUpload);
        }

        /// <summary>
        /// Handles Exit action
        /// </summary>
        private async System.Threading.Tasks.Task HandleExit()
        {
            await this.HandleAction(this.OnExit);
        }

        /// <summary>
        /// Handles the explicit workspace reset action
        /// </summary>
        private async System.Threading.Tasks.Task HandleResetWorkspace()
        {
            await this.HandleAction(this.OnResetWorkspace);
        }

        /// <summary>
        /// Handles Copy action
        /// </summary>
        private async System.Threading.Tasks.Task HandleCopy()
        {
            await this.HandleAction(this.OnCopy);
        }

        /// <summary>
        /// Handles Cut action
        /// </summary>
        private async System.Threading.Tasks.Task HandleCut()
        {
            await this.HandleAction(this.OnCut);
        }

        /// <summary>
        /// Handles Paste action
        /// </summary>
        private async System.Threading.Tasks.Task HandlePaste()
        {
            await this.HandleAction(this.OnPaste);
        }

        /// <summary>
        /// Handles Delete action
        /// </summary>
        private async System.Threading.Tasks.Task HandleDelete()
        {
            await this.HandleAction(this.OnDelete);
        }

        /// <summary>
        /// Handles Rename action
        /// </summary>
        private async System.Threading.Tasks.Task HandleRename()
        {
            await this.HandleAction(this.OnRename);
        }

        /// <summary>
        /// Handles Advanced Rename action
        /// </summary>
        private async System.Threading.Tasks.Task HandleAdvancedRename()
        {
            await this.HandleAction(this.OnAdvancedRename);
        }

        /// <summary>
        /// Handles Select All action
        /// </summary>
        private async System.Threading.Tasks.Task HandleSelectAll()
        {
            await this.HandleAction(this.OnSelectAll);
        }

        /// <summary>
        /// Handles Refresh action
        /// </summary>
        private async System.Threading.Tasks.Task HandleRefresh()
        {
            await this.HandleAction(this.OnRefresh);
        }

        /// <summary>
        /// Handles Terminal toggle action
        /// </summary>
        private async System.Threading.Tasks.Task HandleTerminal()
        {
            await this.HandleAction(this.OnTerminal);
        }

        /// <summary>
        /// Handles Editor Extensions action
        /// </summary>
        private async System.Threading.Tasks.Task HandleEditorExtensions()
        {
            await this.HandleAction(this.OnEditorExtensions);
        }

        /// <summary>
        /// Handles default creation permissions action
        /// </summary>
        private async System.Threading.Tasks.Task HandleCreationPermissions()
        {
            await this.HandleAction(this.OnCreationPermissions);
        }

        /// <summary>
        /// Handles Authentication Settings action
        /// </summary>
        private async System.Threading.Tasks.Task HandleAuthenticationSettings()
        {
            await this.HandleAction(this.OnAuthenticationSettings);
        }

        /// <summary>
        /// Handles Logout action
        /// </summary>
        private async System.Threading.Tasks.Task HandleLogout()
        {
            await this.HandleAction(this.OnLogout);
        }

        /// <summary>
        /// Handles About action
        /// </summary>
        private async System.Threading.Tasks.Task HandleAbout()
        {
            await this.HandleAction(this.OnAbout);
        }

        /// <summary>
        /// Handles Extract action
        /// </summary>
        private async System.Threading.Tasks.Task HandleExtract()
        {
            await this.HandleAction(this.OnExtract);
        }

        /// <summary>
        /// Handles Compress action
        /// </summary>
        private async System.Threading.Tasks.Task HandleCompress()
        {
            await this.HandleAction(this.OnCompress);
        }

        /// <summary>
        /// Handles toggle single/dual panel mode
        /// </summary>
        private async System.Threading.Tasks.Task HandleToggleSinglePanel()
        {
            this._activeMenu = "";
            await this.HandleAction(this.OnToggleSinglePanel);
        }

        /// <summary>
        /// Closes the active menu and invokes the selected action
        /// </summary>
        /// <param name="callback">Action callback</param>
        private async System.Threading.Tasks.Task HandleAction(EventCallback callback)
        {
            this._activeMenu = "";
            await callback.InvokeAsync();
        }

        #endregion
    }
}
