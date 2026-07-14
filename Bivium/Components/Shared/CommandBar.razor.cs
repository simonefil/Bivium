using Microsoft.AspNetCore.Components;

namespace Bivium.Components.Shared
{
    /// <summary>
    /// Bottom command bar with clickable shortcut labels
    /// </summary>
    public partial class CommandBar : ComponentBase
    {
        #region Parameters

        /// <summary>
        /// Callback for About action
        /// </summary>
        [Parameter]
        public EventCallback OnAbout { get; set; }

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
        /// Callback for New Folder action
        /// </summary>
        [Parameter]
        public EventCallback OnNewFolder { get; set; }

        /// <summary>
        /// Callback for Refresh action
        /// </summary>
        [Parameter]
        public EventCallback OnRefresh { get; set; }

        /// <summary>
        /// Callback for Properties action
        /// </summary>
        [Parameter]
        public EventCallback OnProperties { get; set; }

        /// <summary>
        /// Callback for Terminal toggle action
        /// </summary>
        [Parameter]
        public EventCallback OnTerminal { get; set; }

        /// <summary>
        /// Whether the terminal window is visible
        /// </summary>
        [Parameter]
        public bool TerminalVisible { get; set; } = false;

        /// <summary>
        /// Whether the terminal window is minimized
        /// </summary>
        [Parameter]
        public bool TerminalMinimized { get; set; } = false;

        /// <summary>
        /// Whether the active panel has multiple selected entries
        /// </summary>
        [Parameter]
        public bool IsMultiSelection { get; set; } = false;

        #endregion

        #region Properties

        /// <summary>
        /// CSS class for terminal command state
        /// </summary>
        private string TerminalCssClass
        {
            get
            {
                string result = "cmd-key";
                if (this.TerminalVisible)
                {
                    result += " terminal-visible";
                }
                else if (this.TerminalMinimized)
                {
                    result += " terminal-minimized";
                }

                return result;
            }
        }

        #endregion
    }
}
