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

        #endregion

        #region Private Methods

        /// <summary>
        /// Closes the menu
        /// </summary>
        private void HandleClose()
        {
            this.OnClose.InvokeAsync();
        }

        /// <summary>
        /// Handles Open action
        /// </summary>
        private void HandleOpen()
        {
            this.OnClose.InvokeAsync();
            this.OnOpen.InvokeAsync();
        }

        /// <summary>
        /// Handles Copy action
        /// </summary>
        private void HandleCopy()
        {
            this.OnClose.InvokeAsync();
            this.OnCopy.InvokeAsync();
        }

        /// <summary>
        /// Handles Cut action
        /// </summary>
        private void HandleCut()
        {
            this.OnClose.InvokeAsync();
            this.OnCut.InvokeAsync();
        }

        /// <summary>
        /// Handles Paste action
        /// </summary>
        private void HandlePaste()
        {
            this.OnClose.InvokeAsync();
            this.OnPaste.InvokeAsync();
        }

        /// <summary>
        /// Handles New File action
        /// </summary>
        private void HandleNewFile()
        {
            this.OnClose.InvokeAsync();
            this.OnNewFile.InvokeAsync();
        }

        /// <summary>
        /// Handles New Folder action
        /// </summary>
        private void HandleNewFolder()
        {
            this.OnClose.InvokeAsync();
            this.OnNewFolder.InvokeAsync();
        }

        /// <summary>
        /// Handles Edit action
        /// </summary>
        private void HandleEdit()
        {
            this.OnClose.InvokeAsync();
            this.OnEdit.InvokeAsync();
        }

        /// <summary>
        /// Handles Download action
        /// </summary>
        private void HandleDownload()
        {
            this.OnClose.InvokeAsync();
            this.OnDownload.InvokeAsync();
        }

        /// <summary>
        /// Handles Upload action
        /// </summary>
        private void HandleUpload()
        {
            this.OnClose.InvokeAsync();
            this.OnUpload.InvokeAsync();
        }

        /// <summary>
        /// Handles Rename action
        /// </summary>
        private void HandleRename()
        {
            this.OnClose.InvokeAsync();
            this.OnRename.InvokeAsync();
        }

        /// <summary>
        /// Handles Delete action
        /// </summary>
        private void HandleDelete()
        {
            this.OnClose.InvokeAsync();
            this.OnDelete.InvokeAsync();
        }

        /// <summary>
        /// Handles Permissions action
        /// </summary>
        private void HandlePermissions()
        {
            this.OnClose.InvokeAsync();
            this.OnPermissions.InvokeAsync();
        }

        /// <summary>
        /// Handles Properties action
        /// </summary>
        private void HandleProperties()
        {
            this.OnClose.InvokeAsync();
            this.OnProperties.InvokeAsync();
        }

        /// <summary>
        /// Handles Extract Here action
        /// </summary>
        private void HandleExtract()
        {
            this.OnClose.InvokeAsync();
            this.OnExtract.InvokeAsync();
        }

        /// <summary>
        /// Handles Extract To Folder action
        /// </summary>
        private void HandleExtractToFolder()
        {
            this.OnClose.InvokeAsync();
            this.OnExtractToFolder.InvokeAsync();
        }

        /// <summary>
        /// Handles Compress To action
        /// </summary>
        private void HandleCompress()
        {
            this.OnClose.InvokeAsync();
            this.OnCompress.InvokeAsync();
        }

        #endregion
    }
}
