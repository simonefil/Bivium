using Microsoft.AspNetCore.Components;
using Radzen;
using System;
using System.Collections.Generic;

namespace Bivium.Components.Shared
{
    /// <summary>
    /// Top menu bar with dropdown menus (File, Edit, View, Settings, Help)
    /// </summary>
    public partial class MenuBar : ComponentBase
    {
        #region Parameters

        /// <summary>Availability and shortcuts computed exclusively by the Commander</summary>
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

        /// <summary>Visibility controlled by the Commander, without duplicating the terminal state</summary>
        [Parameter] public bool TerminalVisible { get; set; }
        /// <summary>Minimization controlled by the Commander</summary>
        [Parameter] public bool TerminalMinimized { get; set; }
        /// <summary>Attention request controlled by the terminal</summary>
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
        /// Themes controlled by the Commander in the Radzen variant
        /// </summary>
        [Parameter]
        public IReadOnlyList<string> ThemeOptions { get; set; } = Array.Empty<string>();

        /// <summary>
        /// Theme controlled by the Commander in the Radzen variant
        /// </summary>
        [Parameter]
        public string CurrentTheme { get; set; } = "software-dark";

        /// <summary>
        /// Callback controlled by the Commander for the Radzen theme change
        /// </summary>
        [Parameter]
        public EventCallback<string> OnThemeChanged { get; set; }

        /// <summary>
        /// Revalidates the authority before executing a Radzen command
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

        #region Constants

        /// <summary>
        /// Prefix of the menu item values that select a theme
        /// </summary>
        private const string ThemeValuePrefix = "theme:";

        #endregion

        #region Private Methods

        /// <summary>Consults the projection without introducing rules into the renderer</summary>
        /// <param name="id">Command identifier</param>
        /// <returns>Descriptor, or null</returns>
        private CommanderCommandState GetCommand(string id)
        {
            foreach (CommanderCommandState command in this.Commands)
            {
                if (command.Id == id)
                    return command;
            }
            return null;
        }

        /// <summary>Reads the availability already computed by the owner</summary>
        /// <param name="id">Command identifier</param>
        /// <returns>True if enabled</returns>
        private bool IsEnabled(string id) => this.GetCommand(id)?.Enabled == true;

        /// <summary>
        /// Returns the selected theme for the compiled variant
        /// </summary>
        /// <returns>Current theme name</returns>
        private string GetCurrentTheme()
        {
            return this.CurrentTheme;
        }

        /// <summary>
        /// Returns the themes available for the compiled variant
        /// </summary>
        /// <returns>Theme list</returns>
        private IReadOnlyList<string> GetThemeOptions()
        {
            return this.ThemeOptions;
        }

        /// <summary>
        /// Formats a kebab-case theme name for the menu
        /// </summary>
        /// <param name="theme">Technical theme name</param>
        /// <returns>Readable label</returns>
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
        /// Dispatches the clicked menu item to the action bound to its command identifier
        /// </summary>
        /// <param name="args">Clicked item; parent items carry no value</param>
        private async System.Threading.Tasks.Task HandleMenuClick(MenuItemEventArgs args)
        {
            if (args.Value is not string command || this.CanInvoke == null || !this.CanInvoke())
                return;

            if (command.StartsWith(ThemeValuePrefix, StringComparison.Ordinal))
            {
                await this.OnThemeChanged.InvokeAsync(command.Substring(ThemeValuePrefix.Length));
                return;
            }

            EventCallback callback = command switch
            {
                "new-file" => this.OnNewFile,
                "new-folder" => this.OnNewFolder,
                "download" => this.OnDownload,
                "upload" => this.OnUpload,
                "logout" => this.OnLogout,
                "extract" => this.OnExtract,
                "compress" => this.OnCompress,
                "reset" => this.OnResetWorkspace,
                "copy" => this.OnCopy,
                "cut" => this.OnCut,
                "paste" => this.OnPaste,
                "delete" => this.OnDelete,
                "rename" => this.OnRename,
                "advanced-rename" => this.OnAdvancedRename,
                "select-all" => this.OnSelectAll,
                "refresh" => this.OnRefresh,
                "panels" => this.OnToggleSinglePanel,
                "terminal" => this.OnTerminal,
                "editor-extensions" => this.OnEditorExtensions,
                "creation-permissions" => this.OnCreationPermissions,
                "authentication" => this.OnAuthenticationSettings,
                "about" => this.OnAbout,
                _ => default
            };
            await callback.InvokeAsync();
        }

        #endregion
    }
}
