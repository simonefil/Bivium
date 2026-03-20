using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Bivium.Models;
using Bivium.Services;

namespace Bivium.Components.Shared
{
    /// <summary>
    /// Floating window for advanced batch file renaming
    /// </summary>
    public partial class RenamerDialog : ComponentBase
    {
        #region Parameters

        /// <summary>
        /// Callback when the dialog is closed (true if files were renamed)
        /// </summary>
        [Parameter]
        public EventCallback<bool> OnClose { get; set; }

        /// <summary>
        /// File operation service for renaming files
        /// </summary>
        [Parameter]
        public IFileOperationService FileOperationService { get; set; }

        #endregion

        #region Class Variables

        /// <summary>
        /// Whether the dialog is visible
        /// </summary>
        private bool _isVisible = false;

        /// <summary>
        /// Source file entries to rename
        /// </summary>
        private List<FileSystemEntry> _entries = new List<FileSystemEntry>();

        /// <summary>
        /// Ordered stack of rename methods to apply
        /// </summary>
        private List<RenameMethod> _methodStack = new List<RenameMethod>();

        /// <summary>
        /// Preview items with computed new names
        /// </summary>
        private List<RenamePreviewItem> _previewItems = new List<RenamePreviewItem>();

        /// <summary>
        /// Current method being configured before adding to stack
        /// </summary>
        private RenameMethod _editingMethod = new RenameMethod();

        /// <summary>
        /// Currently selected method type in the dropdown
        /// </summary>
        private RenameMethodType _selectedMethodType = RenameMethodType.Replace;

        /// <summary>
        /// Error message for invalid parameters
        /// </summary>
        private string _paramsError = "";

        /// <summary>
        /// Status bar text
        /// </summary>
        private string _statusText = "";

        /// <summary>
        /// JS module reference for drag/resize interop
        /// </summary>
        private IJSObjectReference _interopModule = null;

        /// <summary>
        /// Whether a rename operation was performed
        /// </summary>
        private bool _didRename = false;

        #endregion

        #region Public Methods

        /// <summary>
        /// Shows the renamer dialog with the specified file entries
        /// </summary>
        /// <param name="entries">File entries to rename (files only, no directories)</param>
        public void Show(List<FileSystemEntry> entries)
        {
            this._entries = entries;
            this._methodStack.Clear();
            this._editingMethod = new RenameMethod();
            this._selectedMethodType = RenameMethodType.Replace;
            this._paramsError = "";
            this._didRename = false;
            this._isVisible = true;

            // Generate initial preview (no methods, names unchanged)
            this.RecalculatePreview();
            this.StateHasChanged();

            // Initialize drag/resize after render
            _ = this.InitializeDragResize();
        }

        /// <summary>
        /// Returns whether the dialog is currently visible
        /// </summary>
        /// <returns>True if visible</returns>
        public bool IsVisible()
        {
            return this._isVisible;
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Initializes drag and resize via JS interop
        /// </summary>
        private async System.Threading.Tasks.Task InitializeDragResize()
        {
            // Wait for DOM to be ready
            await System.Threading.Tasks.Task.Delay(100);

            if (this._interopModule == null)
            {
                this._interopModule = await this.JSRuntime.InvokeAsync<IJSObjectReference>("import", "./js/interop.js");
            }

            await this._interopModule.InvokeVoidAsync("initWindowDrag", "renamer-window", "renamer-titlebar", "renamer-resize-handle");
        }

        /// <summary>
        /// Handles method type dropdown change - resets editing method and recalculates preview
        /// </summary>
        private void HandleMethodTypeChanged()
        {
            // Reset editing method when switching type
            this._editingMethod = new RenameMethod();
            this._paramsError = "";
            this.RecalculatePreview();
        }

        /// <summary>
        /// Recalculates the preview by applying all stack methods to entries
        /// </summary>
        private void RecalculatePreview()
        {
            // Build a temporary stack including the editing method for live preview
            List<RenameMethod> previewStack = new List<RenameMethod>(this._methodStack);

            // Add the editing method to preview if it has meaningful input
            if (this.HasEditingMethodInput())
            {
                RenameMethod editCopy = this._editingMethod.Clone();
                editCopy.MethodType = this._selectedMethodType;
                previewStack.Add(editCopy);
            }

            this._previewItems = RenameEngine.GeneratePreview(this._entries, previewStack);

            // Validate regex if applicable
            this._paramsError = this.ValidateEditingMethod();

            // Update status text
            this.UpdateStatusText();
        }

        /// <summary>
        /// Checks if the editing method has meaningful input for preview
        /// </summary>
        /// <returns>True if the editing method has input worth previewing</returns>
        private bool HasEditingMethodInput()
        {
            bool result = false;

            if (this._selectedMethodType == RenameMethodType.Replace)
            {
                result = !string.IsNullOrEmpty(this._editingMethod.SearchText);
            }
            else if (this._selectedMethodType == RenameMethodType.Add)
            {
                result = !string.IsNullOrEmpty(this._editingMethod.InsertText);
            }
            else if (this._selectedMethodType == RenameMethodType.Remove)
            {
                if (this._editingMethod.RemoveByPattern)
                {
                    result = !string.IsNullOrEmpty(this._editingMethod.RemovePattern);
                }
                else
                {
                    result = this._editingMethod.RemoveCount > 0;
                }
            }
            else if (this._selectedMethodType == RenameMethodType.NewCase)
            {
                result = true;
            }
            else if (this._selectedMethodType == RenameMethodType.NewName)
            {
                result = !string.IsNullOrEmpty(this._editingMethod.NamePattern);
            }
            else if (this._selectedMethodType == RenameMethodType.Trim)
            {
                result = !string.IsNullOrEmpty(this._editingMethod.TrimCharacters);
            }

            return result;
        }

        /// <summary>
        /// Validates the editing method for regex errors
        /// </summary>
        /// <returns>Error message or empty string</returns>
        private string ValidateEditingMethod()
        {
            string error = "";

            if (this._selectedMethodType == RenameMethodType.Replace && this._editingMethod.UseRegex && !string.IsNullOrEmpty(this._editingMethod.SearchText))
            {
                try
                {
                    System.Text.RegularExpressions.Regex.Match("", this._editingMethod.SearchText);
                }
                catch (System.ArgumentException ex)
                {
                    error = "Invalid regex: " + ex.Message;
                }
            }
            else if (this._selectedMethodType == RenameMethodType.Remove && this._editingMethod.RemoveByPattern && this._editingMethod.RemovePatternUseRegex && !string.IsNullOrEmpty(this._editingMethod.RemovePattern))
            {
                try
                {
                    System.Text.RegularExpressions.Regex.Match("", this._editingMethod.RemovePattern);
                }
                catch (System.ArgumentException ex)
                {
                    error = "Invalid regex: " + ex.Message;
                }
            }

            return error;
        }

        /// <summary>
        /// Updates the status bar text with file count and conflict info
        /// </summary>
        private void UpdateStatusText()
        {
            int fileCount = this._previewItems.Count;
            int conflictCount = 0;
            int errorCount = 0;
            int changedCount = 0;

            for (int i = 0; i < this._previewItems.Count; i++)
            {
                if (this._previewItems[i].HasConflict)
                {
                    conflictCount++;
                }
                if (this._previewItems[i].HasError)
                {
                    errorCount++;
                }
                if (this._previewItems[i].OriginalName != this._previewItems[i].NewName)
                {
                    changedCount++;
                }
            }

            this._statusText = fileCount + " files, " + changedCount + " changed";

            if (conflictCount > 0)
            {
                this._statusText = this._statusText + ", " + conflictCount + " conflicts";
            }

            if (errorCount > 0)
            {
                this._statusText = this._statusText + ", " + errorCount + " errors";
            }
        }

        /// <summary>
        /// Checks if the rename button should be enabled
        /// </summary>
        /// <returns>True if rename can be executed</returns>
        private bool CanRename()
        {
            // Must have methods in the stack
            if (this._methodStack.Count == 0)
            {
                return false;
            }

            // Must have no conflicts or errors
            for (int i = 0; i < this._previewItems.Count; i++)
            {
                if (this._previewItems[i].HasConflict || this._previewItems[i].HasError)
                {
                    return false;
                }
            }

            // Must have at least one changed name
            bool hasChanged = false;
            for (int i = 0; i < this._previewItems.Count; i++)
            {
                if (this._previewItems[i].OriginalName != this._previewItems[i].NewName)
                {
                    hasChanged = true;
                    break;
                }
            }

            return hasChanged;
        }

        /// <summary>
        /// Adds the current editing method to the stack
        /// </summary>
        private void HandleAddMethod()
        {
            // Don't add empty methods to the stack
            if (!this.HasEditingMethodInput())
            {
                return;
            }

            // Validate before adding
            string error = this.ValidateEditingMethod();
            if (!string.IsNullOrEmpty(error))
            {
                this._paramsError = error;
                return;
            }

            // Clone the editing method and set its type
            RenameMethod method = this._editingMethod.Clone();
            method.MethodType = this._selectedMethodType;
            this._methodStack.Add(method);

            // Reset editing method
            this._editingMethod = new RenameMethod();
            this._paramsError = "";

            // Recalculate preview with stack only (no editing method input)
            this._previewItems = RenameEngine.GeneratePreview(this._entries, this._methodStack);
            this.UpdateStatusText();
        }

        /// <summary>
        /// Removes a method from the stack by index
        /// </summary>
        /// <param name="index">Index of the method to remove</param>
        private void HandleRemoveMethod(int index)
        {
            if (index >= 0 && index < this._methodStack.Count)
            {
                this._methodStack.RemoveAt(index);
                this.RecalculatePreview();
            }
        }

        /// <summary>
        /// Moves a method up in the stack
        /// </summary>
        /// <param name="index">Index of the method to move up</param>
        private void HandleMoveUp(int index)
        {
            if (index > 0 && index < this._methodStack.Count)
            {
                RenameMethod temp = this._methodStack[index];
                this._methodStack[index] = this._methodStack[index - 1];
                this._methodStack[index - 1] = temp;
                this.RecalculatePreview();
            }
        }

        /// <summary>
        /// Moves a method down in the stack
        /// </summary>
        /// <param name="index">Index of the method to move down</param>
        private void HandleMoveDown(int index)
        {
            if (index >= 0 && index < this._methodStack.Count - 1)
            {
                RenameMethod temp = this._methodStack[index];
                this._methodStack[index] = this._methodStack[index + 1];
                this._methodStack[index + 1] = temp;
                this.RecalculatePreview();
            }
        }

        /// <summary>
        /// Executes the rename operation using two-pass strategy
        /// </summary>
        private void HandleRename()
        {
            if (!this.CanRename())
            {
                return;
            }

            // Regenerate preview with stack only (not editing method)
            this._previewItems = RenameEngine.GeneratePreview(this._entries, this._methodStack);

            // Collect items that actually changed
            List<RenamePreviewItem> toRename = new List<RenamePreviewItem>();
            for (int i = 0; i < this._previewItems.Count; i++)
            {
                if (this._previewItems[i].OriginalName != this._previewItems[i].NewName)
                {
                    toRename.Add(this._previewItems[i]);
                }
            }

            if (toRename.Count == 0)
            {
                return;
            }

            // Two-pass rename to avoid circular conflicts
            string tempSuffix = ".bivium_rename_temp_" + Guid.NewGuid().ToString("N");
            List<string> tempPaths = new List<string>();
            bool pass1Success = true;
            string errorMessage = "";

            // Pass 1: rename all to temporary names
            for (int i = 0; i < toRename.Count; i++)
            {
                string originalPath = toRename[i].OriginalFullPath;
                string tempName = toRename[i].NewName + tempSuffix;
                FileOperationResult result = this.FileOperationService.RenameEntry(originalPath, tempName);

                if (result.Success)
                {
                    // Store the temp path for pass 2
                    string dir = Path.GetDirectoryName(originalPath);
                    string tempPath = Path.Combine(dir, tempName);
                    tempPaths.Add(tempPath);
                }
                else
                {
                    pass1Success = false;
                    errorMessage = "Failed to rename '" + toRename[i].OriginalName + "': " + result.ErrorMessage;

                    // Rollback: rename temp files back to original names
                    for (int r = 0; r < tempPaths.Count; r++)
                    {
                        string rollbackName = toRename[r].OriginalName;
                        this.FileOperationService.RenameEntry(tempPaths[r], rollbackName);
                    }
                    break;
                }
            }

            if (pass1Success)
            {
                // Pass 2: rename from temp to final names
                int successCount = 0;
                int failCount = 0;

                for (int i = 0; i < tempPaths.Count; i++)
                {
                    FileOperationResult result = this.FileOperationService.RenameEntry(tempPaths[i], toRename[i].NewName);

                    if (result.Success)
                    {
                        successCount++;
                    }
                    else
                    {
                        failCount++;
                        errorMessage = errorMessage + "Failed to finalize '" + toRename[i].NewName + "': " + result.ErrorMessage + "\n";

                        // Rollback this file to its original name
                        this.FileOperationService.RenameEntry(tempPaths[i], toRename[i].OriginalName);
                    }
                }

                this._didRename = successCount > 0;
                this._statusText = "Renamed " + successCount + "/" + toRename.Count + " files";

                if (failCount > 0)
                {
                    this._statusText = this._statusText + " (" + failCount + " errors, rolled back)";
                }
            }
            else
            {
                this._statusText = "Error: " + errorMessage;
            }

            // Close the dialog only if all renames succeeded
            if (this._didRename && string.IsNullOrEmpty(errorMessage))
            {
                this._isVisible = false;
                this.OnClose.InvokeAsync(true);
            }
        }

        /// <summary>
        /// Closes the dialog without renaming
        /// </summary>
        private void HandleClose()
        {
            this._isVisible = false;
            this.OnClose.InvokeAsync(this._didRename);
        }

        #endregion
    }
}
