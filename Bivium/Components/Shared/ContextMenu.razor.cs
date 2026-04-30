using Microsoft.AspNetCore.Components;
using Bivium.Models;

namespace Bivium.Components.Shared
{
    /// <summary>
    /// Right-click context menu positioned at mouse coordinates
    /// </summary>
    public partial class ContextMenu : ComponentBase
    {
        #region Parameters

        /// <summary>
        /// Whether the menu is visible
        /// </summary>
        [Parameter]
        public bool IsVisible { get; set; } = false;

        /// <summary>
        /// X coordinate for menu positioning
        /// </summary>
        [Parameter]
        public double X { get; set; } = 0;

        /// <summary>
        /// Y coordinate for menu positioning
        /// </summary>
        [Parameter]
        public double Y { get; set; } = 0;

        /// <summary>
        /// Callback to close the menu
        /// </summary>
        [Parameter]
        public EventCallback OnClose { get; set; }

        /// <summary>
        /// Callback for Open action
        /// </summary>
        [Parameter]
        public EventCallback OnOpen { get; set; }

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
        /// Callback for Edit action
        /// </summary>
        [Parameter]
        public EventCallback OnEdit { get; set; }

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
        /// Callback for Rename action
        /// </summary>
        [Parameter]
        public EventCallback OnRename { get; set; }

        /// <summary>
        /// Callback for Delete action
        /// </summary>
        [Parameter]
        public EventCallback OnDelete { get; set; }

        /// <summary>
        /// Callback for Permissions action
        /// </summary>
        [Parameter]
        public EventCallback OnPermissions { get; set; }

        /// <summary>
        /// Callback for Properties action
        /// </summary>
        [Parameter]
        public EventCallback OnProperties { get; set; }

        /// <summary>
        /// Callback for Extract Here action
        /// </summary>
        [Parameter]
        public EventCallback OnExtract { get; set; }

        /// <summary>
        /// Callback for Compress To action
        /// </summary>
        [Parameter]
        public EventCallback OnCompress { get; set; }

        /// <summary>
        /// Callback for Extract To Folder action
        /// </summary>
        [Parameter]
        public EventCallback OnExtractToFolder { get; set; }

        /// <summary>
        /// Callback for Advanced Rename action
        /// </summary>
        [Parameter]
        public EventCallback OnAdvancedRename { get; set; }

        /// <summary>
        /// Callback for Refresh action
        /// </summary>
        [Parameter]
        public EventCallback OnRefresh { get; set; }

        /// <summary>
        /// Whether the cursor is on a directory
        /// </summary>
        [Parameter]
        public bool IsDirectory { get; set; } = false;

        /// <summary>
        /// Whether the cursor is on an archive file
        /// </summary>
        [Parameter]
        public bool IsArchive { get; set; } = false;

        /// <summary>
        /// Base name of the archive file (without extension) for display
        /// </summary>
        [Parameter]
        public string ArchiveBaseName { get; set; } = "";

        /// <summary>
        /// Whether items are selected for compression
        /// </summary>
        [Parameter]
        public bool HasSelection { get; set; } = false;

        /// <summary>
        /// Whether multiple items are selected
        /// </summary>
        [Parameter]
        public bool IsMultiSelection { get; set; } = false;

        /// <summary>
        /// Whether the cursor file has an editable extension (Monaco editor)
        /// </summary>
        [Parameter]
        public bool IsEditable { get; set; } = false;

        #endregion

        #region Private Methods

        /// <summary>
        /// Closes the menu
        /// </summary>
        private async System.Threading.Tasks.Task HandleClose()
        {
            await this.OnClose.InvokeAsync();
        }

        /// <summary>
        /// Handles Open action
        /// </summary>
        private async System.Threading.Tasks.Task HandleOpen()
        {
            await this.HandleAction(this.OnOpen);
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
        /// Handles Edit action
        /// </summary>
        private async System.Threading.Tasks.Task HandleEdit()
        {
            await this.HandleAction(this.OnEdit);
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
        /// Handles Rename action
        /// </summary>
        private async System.Threading.Tasks.Task HandleRename()
        {
            await this.HandleAction(this.OnRename);
        }

        /// <summary>
        /// Handles Delete action
        /// </summary>
        private async System.Threading.Tasks.Task HandleDelete()
        {
            await this.HandleAction(this.OnDelete);
        }

        /// <summary>
        /// Handles Permissions action
        /// </summary>
        private async System.Threading.Tasks.Task HandlePermissions()
        {
            await this.HandleAction(this.OnPermissions);
        }

        /// <summary>
        /// Handles Properties action
        /// </summary>
        private async System.Threading.Tasks.Task HandleProperties()
        {
            await this.HandleAction(this.OnProperties);
        }

        /// <summary>
        /// Handles Extract Here action
        /// </summary>
        private async System.Threading.Tasks.Task HandleExtract()
        {
            await this.HandleAction(this.OnExtract);
        }

        /// <summary>
        /// Handles Extract To Folder action
        /// </summary>
        private async System.Threading.Tasks.Task HandleExtractToFolder()
        {
            await this.HandleAction(this.OnExtractToFolder);
        }

        /// <summary>
        /// Handles Compress To action
        /// </summary>
        private async System.Threading.Tasks.Task HandleCompress()
        {
            await this.HandleAction(this.OnCompress);
        }

        /// <summary>
        /// Handles Advanced Rename action
        /// </summary>
        private async System.Threading.Tasks.Task HandleAdvancedRename()
        {
            await this.HandleAction(this.OnAdvancedRename);
        }

        /// <summary>
        /// Handles Refresh action
        /// </summary>
        private async System.Threading.Tasks.Task HandleRefresh()
        {
            await this.HandleAction(this.OnRefresh);
        }

        /// <summary>
        /// Closes the menu and invokes the selected action in order
        /// </summary>
        /// <param name="callback">Action callback to invoke</param>
        private async System.Threading.Tasks.Task HandleAction(EventCallback callback)
        {
            await this.OnClose.InvokeAsync();
            await callback.InvokeAsync();
        }

        #endregion
    }
}
