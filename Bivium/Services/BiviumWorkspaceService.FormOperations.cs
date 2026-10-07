using Bivium.Controllers;
using Bivium.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Bivium.Services
{
    public sealed partial class BiviumWorkspaceService
    {
        #region Metodi privati

        /// <summary>Usa il commit delle impostazioni condiviso con gli endpoint esistenti</summary>
        private FileOperationResult RunWorkspaceSettings(WorkspaceOperationRuntime operation)
        {
            WorkspaceOperationPlan plan = operation.Plan;
            if (plan.Kind == WorkspaceWorkflowKind.EditorExtensions)
            {
                List<string> extensions = plan.FormDraft.Split('\n').Select(line => line.Trim()).Where(line => !string.IsNullOrEmpty(line)).ToList();
                SettingsController.CommitEditableSettings(this._workflowEnvironment, "EditableExtensions", extensions, operation.Cancellation.Token);
            }
            else
            {
                DefaultCreationPermissionsSettings settings = JsonSerializer.Deserialize<DefaultCreationPermissionsSettings>(plan.FormDraft);
                SettingsController.CommitEditableSettings(this._workflowEnvironment, "DefaultCreationPermissions", settings, operation.Cancellation.Token);
            }
            return FileOperationResult.Ok(1);
        }

        /// <summary>Preserva ordine permission poi ownership e condizione recursive preesistenti</summary>
        private FileOperationResult RunWorkspacePermissions(WorkspaceOperationRuntime operation)
        {
            WorkspaceOperationPlan plan = operation.Plan;
            WorkspacePermissionsDraft draft = JsonSerializer.Deserialize<WorkspacePermissionsDraft>(plan.FormDraft);
            WorkspacePermissionsContext context = JsonSerializer.Deserialize<WorkspacePermissionsContext>(plan.FormContext);
            this.PublishWorkspaceExecutionState(operation, 0, 2, "Permissions");
            FileOperationResult result = this._workflowPermissions.SetPermissions(plan.SourcePath, draft.Model, draft.Recursive, operation.Cancellation.Token);
            if (!result.Success)
                return result;
            this.PublishWorkspaceExecutionState(operation, 1, 2, "Ownership");
            bool ownerChanged = draft.Model.Owner != context.OriginalOwner;
            bool groupChanged = draft.Model.IsUnix && draft.Model.Group != context.OriginalGroup;
            if (ownerChanged || groupChanged || (context.IsDirectory && draft.Recursive))
                result = this._workflowPermissions.SetOwner(plan.SourcePath, draft.Model.Owner, draft.Model.Group, draft.Recursive, operation.Cancellation.Token);
            this.PublishWorkspaceExecutionState(operation, 2, 2, "Permissions");
            return result;
        }

        /// <summary>Esegue le stesse API archive con destinazioni catturate e lifetime workspace</summary>
        private FileOperationResult RunWorkspaceArchive(WorkspaceOperationRuntime operation)
        {
            WorkspaceOperationPlan plan = operation.Plan;
            if (plan.Kind == WorkspaceWorkflowKind.Compress)
            {
                WorkspaceCompressDraft draft = JsonSerializer.Deserialize<WorkspaceCompressDraft>(plan.FormDraft);
                return this._workflowArchives.CreateArchive(Path.Combine(plan.ParentPath, draft.OutputName.Trim()), new List<string>(plan.SourcePaths), draft.Format, (current, total, name) => this.PublishWorkspaceExecutionState(operation, current, total, "Compressing"), operation.Cancellation.Token);
            }
            int processed = 0;
            foreach (string archivePath in plan.SourcePaths)
            {
                operation.Cancellation.Token.ThrowIfCancellationRequested();
                string destination = plan.ParentPath;
                if (plan.ExtractToOwnFolder)
                {
                    string name = Path.GetFileName(archivePath);
                    string baseName = Path.GetFileNameWithoutExtension(name);
                    foreach (string suffix in new[] { ".tar.gz", ".tar.bz2", ".tar.xz", ".tar.zst" })
                        if (name.ToLowerInvariant().EndsWith(suffix))
                            baseName = name.Substring(0, name.Length - suffix.Length);
                    destination = Path.Combine(destination, baseName);
                    try
                    {
                        Directory.CreateDirectory(destination);
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        return FileOperationResult.Fail("Access denied: " + ex.Message);
                    }
                    catch (IOException ex)
                    {
                        return FileOperationResult.Fail("I/O error: " + ex.Message);
                    }
                }
                FileOperationResult result = this._workflowArchives.ExtractArchive(archivePath, destination, (current, total, name) => this.PublishWorkspaceExecutionState(operation, current, total, "Extracting"), operation.Cancellation.Token);
                if (!result.Success)
                    return result;
                processed++;
                this.PublishWorkspaceOperationProgress(operation, processed, 0);
            }
            return FileOperationResult.Ok(processed);
        }

        /// <summary>Il risultato del calcolo appartiene alla form, non al browser che lo avvia</summary>
        private FileOperationResult RunWorkspaceProperties(WorkspaceOperationRuntime operation)
        {
            WorkspacePropertiesDraft draft = JsonSerializer.Deserialize<WorkspacePropertiesDraft>(operation.Plan.FormDraft);
            long size = this._workflowFileSystem.CalculateDirectorySize(operation.Plan.SourcePath, out int fileCount, out int directoryCount, operation.Cancellation.Token, (files, directories) => this.PublishWorkspaceExecutionState(operation, files, 0, "Calculating size"));
            operation.ResultDraft = JsonSerializer.Serialize(draft with { Size = size, FileCount = fileCount, DirectoryCount = directoryCount });
            return FileOperationResult.Ok(fileCount);
        }

        #endregion
    }
}
