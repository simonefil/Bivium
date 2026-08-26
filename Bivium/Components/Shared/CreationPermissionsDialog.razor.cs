using System.Text.Json;
using Bivium.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Bivium.Components.Shared
{
    /// <summary>
    /// Dialog for configuring ownership and permissions of newly created entries
    /// </summary>
    public partial class CreationPermissionsDialog : ComponentBase
    {
        #region Parameters

        /// <summary>
        /// Callback when the dialog is closed
        /// </summary>
        [Parameter]
        public EventCallback OnClose { get; set; }

        /// <summary>
        /// Attachment authorized to update settings
        /// </summary>
        [Parameter]
        public string AttachmentId { get; set; } = "";

        /// <summary>
        /// Lease generation authorized to update settings
        /// </summary>
        [Parameter]
        public long LeaseGeneration { get; set; }

        #endregion

        #region Class Variables

        /// <summary>
        /// Whether the dialog is visible
        /// </summary>
        private bool _isVisible = false;

        /// <summary>
        /// Whether operating system defaults should remain untouched
        /// </summary>
        private bool _useSystemDefaults = true;

        /// <summary>
        /// Whether Unix permission controls should be displayed
        /// </summary>
        private readonly bool _isUnix = !OperatingSystem.IsWindows();

        /// <summary>
        /// Settings being edited
        /// </summary>
        private DefaultCreationPermissionsSettings _model = new DefaultCreationPermissionsSettings();

        /// <summary>
        /// Status or error text
        /// </summary>
        private string _statusText = "";

        /// <summary>
        /// JS module used for the settings request
        /// </summary>
        private IJSObjectReference _jsModule;

        /// <summary>
        /// Dialog element used for keyboard focus
        /// </summary>
        private ElementReference _dialogElement;

        #endregion

        #region Public Methods

        /// <summary>
        /// Shows the dialog with current settings
        /// </summary>
        /// <param name="currentSettings">Current creation permission settings</param>
        public void Show(DefaultCreationPermissionsSettings currentSettings)
        {
            DefaultCreationPermissionsSettings source = currentSettings ?? new DefaultCreationPermissionsSettings();
            this._model = this.CloneSettings(source);
            this._useSystemDefaults = !this._model.Enabled;
            this._statusText = "";
            this._isVisible = true;
            this.StateHasChanged();
            _ = this.FocusDialogAsync();
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Focuses the dialog after rendering
        /// </summary>
        private async System.Threading.Tasks.Task FocusDialogAsync()
        {
            await System.Threading.Tasks.Task.Delay(50);
            await this._dialogElement.FocusAsync();
        }

        /// <summary>
        /// Ensures the JS interop module is loaded
        /// </summary>
        private async System.Threading.Tasks.Task EnsureJsModule()
        {
            if (this._jsModule == null)
            {
                this._jsModule = await this.JSRuntime.InvokeAsync<IJSObjectReference>("import", "./js/interop.js");
            }
        }

        /// <summary>
        /// Saves settings to the server
        /// </summary>
        private async System.Threading.Tasks.Task HandleSave()
        {
            this._model.Enabled = !this._useSystemDefaults;
            string json = JsonSerializer.Serialize(this._model);
            await this.EnsureJsModule();
            JsFetchResult response = await this._jsModule.InvokeAsync<JsFetchResult>("putJsonResult", "/api/Settings/default-creation-permissions", json, this.AttachmentId, this.LeaseGeneration);

            if (response.Ok)
            {
                this._isVisible = false;
                await this.OnClose.InvokeAsync();
            }
            else
            {
                this._statusText = string.IsNullOrWhiteSpace(response.Text) ? "Failed to save default creation permissions" : response.Text;
            }
        }

        /// <summary>
        /// Closes the dialog without saving
        /// </summary>
        private async System.Threading.Tasks.Task HandleCancel()
        {
            this._isVisible = false;
            await this.OnClose.InvokeAsync();
        }

        /// <summary>
        /// Clones settings so editing does not mutate the active options instance
        /// </summary>
        /// <param name="source">Settings to clone</param>
        /// <returns>Independent settings copy</returns>
        private DefaultCreationPermissionsSettings CloneSettings(DefaultCreationPermissionsSettings source)
        {
            DefaultCreationPermissionsSettings result = new DefaultCreationPermissionsSettings();
            result.Enabled = source.Enabled;
            result.Owner = source.Owner ?? "";
            result.Group = source.Group ?? "";
            result.FilePermissions = this.ClonePermissions(source.FilePermissions ?? new CreationPermissionSettings());
            result.DirectoryPermissions = this.ClonePermissions(source.DirectoryPermissions ?? new CreationPermissionSettings());
            return result;
        }

        /// <summary>
        /// Clones one permission value set
        /// </summary>
        /// <param name="source">Permission values to clone</param>
        /// <returns>Independent permission copy</returns>
        private CreationPermissionSettings ClonePermissions(CreationPermissionSettings source)
        {
            CreationPermissionSettings result = new CreationPermissionSettings();
            result.OwnerRead = source.OwnerRead;
            result.OwnerWrite = source.OwnerWrite;
            result.OwnerExecute = source.OwnerExecute;
            result.GroupRead = source.GroupRead;
            result.GroupWrite = source.GroupWrite;
            result.GroupExecute = source.GroupExecute;
            result.OthersRead = source.OthersRead;
            result.OthersWrite = source.OthersWrite;
            result.OthersExecute = source.OthersExecute;
            result.WinReadOnly = source.WinReadOnly;
            result.WinHidden = source.WinHidden;
            result.WinSystem = source.WinSystem;
            result.WinArchive = source.WinArchive;
            return result;
        }

        /// <summary>
        /// Calculates the octal Unix permission string
        /// </summary>
        /// <param name="permissions">Permission values</param>
        /// <returns>Three-digit octal string</returns>
        private string GetOctalString(CreationPermissionSettings permissions)
        {
            int owner = (permissions.OwnerRead ? 4 : 0) + (permissions.OwnerWrite ? 2 : 0) + (permissions.OwnerExecute ? 1 : 0);
            int group = (permissions.GroupRead ? 4 : 0) + (permissions.GroupWrite ? 2 : 0) + (permissions.GroupExecute ? 1 : 0);
            int others = (permissions.OthersRead ? 4 : 0) + (permissions.OthersWrite ? 2 : 0) + (permissions.OthersExecute ? 1 : 0);
            return owner.ToString() + group.ToString() + others.ToString();
        }

        #endregion
    }
}
