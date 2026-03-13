using Microsoft.AspNetCore.Components;
using Bivium.Models;
using Bivium.Services;

namespace Bivium.Components.Shared
{
    /// <summary>
    /// Dialog for editing file and directory permissions
    /// </summary>
    public partial class PermissionsDialog : ComponentBase
    {
        #region Injected Services

        /// <summary>
        /// Permission service for reading and writing permissions
        /// </summary>
        [Inject]
        private IPermissionService _permissionService { get; set; }

        #endregion

        #region Parameters

        /// <summary>
        /// Callback when dialog is closed (true if permissions were saved)
        /// </summary>
        [Parameter]
        public EventCallback<bool> OnClose { get; set; }

        #endregion

        #region Class Variables

        /// <summary>
        /// Whether the dialog is visible
        /// </summary>
        private bool _isVisible = false;

        /// <summary>
        /// Name of the entry being edited
        /// </summary>
        private string _entryName = "";

        /// <summary>
        /// Full path of the entry being edited
        /// </summary>
        private string _entryPath = "";

        /// <summary>
        /// Whether the entry is a directory
        /// </summary>
        private bool _isDirectory = false;

        /// <summary>
        /// Permission model being edited
        /// </summary>
        private PermissionModel _model = new PermissionModel();

        /// <summary>
        /// Original owner name for change detection
        /// </summary>
        private string _originalOwner = "";

        /// <summary>
        /// Original group name for change detection
        /// </summary>
        private string _originalGroup = "";

        /// <summary>
        /// Whether to apply permissions recursively
        /// </summary>
        private bool _recursive = false;

        /// <summary>
        /// Error message to display
        /// </summary>
        private string _errorMessage = "";

        /// <summary>
        /// Reference to the dialog element for focus
        /// </summary>
        private ElementReference _dialogElement;

        #endregion

        #region Public Methods

        /// <summary>
        /// Shows the dialog for the specified entry
        /// </summary>
        /// <param name="entry">File system entry to edit permissions for</param>
        public void Show(FileSystemEntry entry)
        {
            this._entryName = entry.Name;
            this._entryPath = entry.FullPath;
            this._isDirectory = entry.IsDirectory;
            this._recursive = false;
            this._errorMessage = "";

            // Load current permissions
            try
            {
                this._model = this._permissionService.GetPermissions(entry.FullPath);
                this._originalOwner = this._model.Owner;
                this._originalGroup = this._model.Group;
            }
            catch
            {
                this._model = new PermissionModel();
                this._originalOwner = "";
                this._originalGroup = "";
                this._errorMessage = "Could not read permissions";
            }

            this._isVisible = true;
            this.StateHasChanged();

            // Focus the dialog after render
            _ = this.FocusDialogAsync();
        }

        /// <summary>
        /// Focuses the dialog element after render
        /// </summary>
        private async System.Threading.Tasks.Task FocusDialogAsync()
        {
            await System.Threading.Tasks.Task.Delay(50);
            await this._dialogElement.FocusAsync();
        }

        /// <summary>
        /// Hides the dialog
        /// </summary>
        public void Hide()
        {
            this._isVisible = false;
            this.StateHasChanged();
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Handles save button click - applies permissions and ownership changes
        /// </summary>
        private void HandleSave()
        {
            this._errorMessage = "";

            // Apply permission changes
            FileOperationResult permResult = this._permissionService.SetPermissions(this._entryPath, this._model, this._recursive);
            if (!permResult.Success)
            {
                this._errorMessage = permResult.ErrorMessage;
                return;
            }

            // Apply ownership changes if owner or group changed
            bool ownerChanged = this._model.Owner != this._originalOwner;
            bool groupChanged = this._model.IsUnix && this._model.Group != this._originalGroup;

            if (ownerChanged || groupChanged)
            {
                FileOperationResult ownResult = this._permissionService.SetOwner(this._entryPath, this._model.Owner, this._model.Group, this._recursive);
                if (!ownResult.Success)
                {
                    this._errorMessage = ownResult.ErrorMessage;
                    return;
                }
            }

            this._isVisible = false;
            this.OnClose.InvokeAsync(true);
        }

        /// <summary>
        /// Handles cancel button click
        /// </summary>
        private void HandleCancel()
        {
            this._isVisible = false;
            this.OnClose.InvokeAsync(false);
        }

        /// <summary>
        /// Calculates the octal permission string for Unix permissions
        /// </summary>
        /// <returns>Octal string (e.g. 755)</returns>
        private string GetOctalString()
        {
            int owner = 0;
            int group = 0;
            int others = 0;

            // Owner bits
            if (this._model.OwnerRead) owner += 4;
            if (this._model.OwnerWrite) owner += 2;
            if (this._model.OwnerExecute) owner += 1;

            // Group bits
            if (this._model.GroupRead) group += 4;
            if (this._model.GroupWrite) group += 2;
            if (this._model.GroupExecute) group += 1;

            // Others bits
            if (this._model.OthersRead) others += 4;
            if (this._model.OthersWrite) others += 2;
            if (this._model.OthersExecute) others += 1;

            string result = owner.ToString() + group.ToString() + others.ToString();
            return result;
        }

        #endregion
    }
}
