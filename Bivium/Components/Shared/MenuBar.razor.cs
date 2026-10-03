using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using System;
using System.Collections.Generic;

namespace Bivium.Components.Shared
{
    /// <summary>
    /// Top menu bar with dropdown menus (File, Edit, View, Settings, Help)
    /// </summary>
    public partial class MenuBar : ComponentBase, IAsyncDisposable
    {
        #region Injected Services

        /// <summary>
        /// JS runtime for theme interop
        /// </summary>
        [Inject]
        private IJSRuntime _jsRuntime { get; set; }

        #endregion

        #region Parameters

        /// <summary>Disponibilità e shortcut calcolati esclusivamente dal Commander</summary>
        [Parameter]
        public IReadOnlyList<CommanderCommandState> Commands { get; set; } = Array.Empty<CommanderCommandState>();

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

        /// <summary>Visibilità controllata dal Commander, senza duplicare lo stato terminale</summary>
        [Parameter] public bool TerminalVisible { get; set; }
        /// <summary>Minimizzazione controllata dal Commander</summary>
        [Parameter] public bool TerminalMinimized { get; set; }
        /// <summary>Richiesta di attenzione controllata dal terminale</summary>
        [Parameter] public bool TerminalNeedsAttention { get; set; }

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
        /// Temi controllati dal Commander nella variante Radzen
        /// </summary>
        [Parameter]
        public IReadOnlyList<string> ThemeOptions { get; set; } = Array.Empty<string>();

        /// <summary>
        /// Tema controllato dal Commander nella variante Radzen
        /// </summary>
        [Parameter]
        public string CurrentTheme { get; set; } = "software-dark";

        /// <summary>
        /// Callback controllato dal Commander per il cambio tema Radzen
        /// </summary>
        [Parameter]
        public EventCallback<string> OnThemeChanged { get; set; }

        /// <summary>
        /// Rivalida l'autorità prima di eseguire un comando Radzen
        /// </summary>
        [Parameter]
        public Func<bool> CanInvoke { get; set; }

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
        /// Host del menu Radzen usato dall'adapter hover
        /// </summary>
        private ElementReference _radzenMenuHost;

        /// <summary>
        /// Modulo dell'adapter menu Radzen
        /// </summary>
        private IJSObjectReference _jsRadzenModule;

        /// <summary>Impedisce registrazioni dopo il rilascio del menu</summary>
        private bool _isDisposed;

        #endregion

        #region Overrides

        /// <summary>
        /// Load saved theme on first render
        /// </summary>
        protected override async System.Threading.Tasks.Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender && !this._isDisposed)
                await this.InitializeRadzenMenuAsync();
        }

        #endregion

        #region Private Methods

        /// <summary>Consulta la proiezione senza introdurre regole nel renderer</summary>
        /// <param name="id">Identificatore del comando</param>
        /// <returns>Descriptor oppure null</returns>
        private CommanderCommandState GetCommand(string id)
        {
            foreach (CommanderCommandState command in this.Commands)
            {
                if (command.Id == id)
                    return command;
            }
            return null;
        }

        /// <summary>Legge la disponibilità già calcolata dal proprietario</summary>
        /// <param name="id">Identificatore del comando</param>
        /// <returns>True se abilitato</returns>
        private bool IsEnabled(string id) => this.GetCommand(id)?.Enabled == true;

        /// <summary>
        /// Collega il passaggio hover tra menu dopo la prima apertura a click
        /// </summary>
        private async System.Threading.Tasks.Task InitializeRadzenMenuAsync()
        {
            this._jsRadzenModule = await this._jsRuntime.InvokeAsync<IJSObjectReference>("import", "./js/radzen-startup.js");
            if (this._isDisposed)
            {
                await this._jsRadzenModule.DisposeAsync();
                this._jsRadzenModule = null;
                return;
            }
            await this._jsRadzenModule.InvokeVoidAsync("initializeRadzenMenuHover", this._radzenMenuHost);
        }

        /// <summary>Rilascia listener hover e modulo posseduti dall'istanza</summary>
        /// <returns>Operazione asincrona di rilascio</returns>
        public async System.Threading.Tasks.ValueTask DisposeAsync()
        {
            if (this._isDisposed)
                return;
            this._isDisposed = true;
            if (this._jsRadzenModule == null)
                return;

            try
            {
                try
                {
                    await this._jsRadzenModule.InvokeVoidAsync("disposeRadzenMenuHover", this._radzenMenuHost);
                }
                finally
                {
                    await this._jsRadzenModule.DisposeAsync();
                }
            }
            catch (Exception ex) when (ex is JSDisconnectedException || ex is OperationCanceledException)
            {
            }
            this._jsRadzenModule = null;
        }

        /// <summary>
        /// Handles theme dropdown change
        /// </summary>
        /// <param name="args">Change event args</param>
        private async System.Threading.Tasks.Task HandleThemeChange(ChangeEventArgs args)
        {
            if (this.CanInvoke != null && !this.CanInvoke())
                return;

            string theme = args.Value?.ToString() ?? "";
            await this.OnThemeChanged.InvokeAsync(theme);
        }

        /// <summary>
        /// Restituisce il tema selezionato per la variante compilata
        /// </summary>
        /// <returns>Nome tema corrente</returns>
        private string GetCurrentTheme()
        {
            return this.CurrentTheme;
        }

        /// <summary>
        /// Restituisce i temi disponibili per la variante compilata
        /// </summary>
        /// <returns>Elenco temi</returns>
        private IReadOnlyList<string> GetThemeOptions()
        {
            return this.ThemeOptions;
        }

        /// <summary>
        /// Formatta un nome tema kebab-case per il menu
        /// </summary>
        /// <param name="theme">Nome tecnico del tema</param>
        /// <returns>Etichetta leggibile</returns>
        private string FormatThemeName(string theme)
        {
            if (string.IsNullOrEmpty(theme))
                return "";

            string[] words = theme.Split('-', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < words.Length; i++)
            {
                if (string.Equals(words[i], "dos", StringComparison.OrdinalIgnoreCase))
                    words[i] = "DOS";
                else
                    words[i] = char.ToUpperInvariant(words[i][0]) + words[i].Substring(1);
            }

            return string.Join(" ", words);
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
            if (this.CanInvoke == null || !this.CanInvoke())
                return;
            this._activeMenu = "";
            await callback.InvokeAsync();
        }

        #endregion
    }
}
