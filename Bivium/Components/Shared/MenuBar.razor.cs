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
        private void HandleNewFile()
        {
            this._activeMenu = "";
            this.OnNewFile.InvokeAsync();
        }

        /// <summary>
        /// Handles New Folder action
        /// </summary>
        private void HandleNewFolder()
        {
            this._activeMenu = "";
            this.OnNewFolder.InvokeAsync();
        }

        /// <summary>
        /// Handles Download action
        /// </summary>
        private void HandleDownload()
        {
            this._activeMenu = "";
            this.OnDownload.InvokeAsync();
        }

        /// <summary>
        /// Handles Upload action
        /// </summary>
        private void HandleUpload()
        {
            this._activeMenu = "";
            this.OnUpload.InvokeAsync();
        }

        /// <summary>
        /// Handles Exit action
        /// </summary>
        private void HandleExit()
        {
            this._activeMenu = "";
            this.OnExit.InvokeAsync();
        }

        /// <summary>
        /// Handles Copy action
        /// </summary>
        private void HandleCopy()
        {
            this._activeMenu = "";
            this.OnCopy.InvokeAsync();
        }

        /// <summary>
        /// Handles Cut action
        /// </summary>
        private void HandleCut()
        {
            this._activeMenu = "";
            this.OnCut.InvokeAsync();
        }

        /// <summary>
        /// Handles Paste action
        /// </summary>
        private void HandlePaste()
        {
            this._activeMenu = "";
            this.OnPaste.InvokeAsync();
        }

        /// <summary>
        /// Handles Delete action
        /// </summary>
        private void HandleDelete()
        {
            this._activeMenu = "";
            this.OnDelete.InvokeAsync();
        }

        /// <summary>
        /// Handles Rename action
        /// </summary>
        private void HandleRename()
        {
            this._activeMenu = "";
            this.OnRename.InvokeAsync();
        }

        /// <summary>
        /// Handles Select All action
        /// </summary>
        private void HandleSelectAll()
        {
            this._activeMenu = "";
            this.OnSelectAll.InvokeAsync();
        }

        /// <summary>
        /// Handles Refresh action
        /// </summary>
        private void HandleRefresh()
        {
            this._activeMenu = "";
            this.OnRefresh.InvokeAsync();
        }

        /// <summary>
        /// Handles Terminal toggle action
        /// </summary>
        private void HandleTerminal()
        {
            this._activeMenu = "";
            this.OnTerminal.InvokeAsync();
        }

        /// <summary>
        /// Handles Editor Extensions action
        /// </summary>
        private void HandleEditorExtensions()
        {
            this._activeMenu = "";
            this.OnEditorExtensions.InvokeAsync();
        }

        /// <summary>
        /// Handles About action
        /// </summary>
        private void HandleAbout()
        {
            this._activeMenu = "";
            this.OnAbout.InvokeAsync();
        }

        /// <summary>
        /// Handles Extract action
        /// </summary>
        private void HandleExtract()
        {
            this._activeMenu = "";
            this.OnExtract.InvokeAsync();
        }

        /// <summary>
        /// Handles Compress action
        /// </summary>
        private void HandleCompress()
        {
            this._activeMenu = "";
            this.OnCompress.InvokeAsync();
        }

        #endregion
    }
}
