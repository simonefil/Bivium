using System;
using System.Collections.Immutable;

namespace Bivium.Models
{
    /// <summary>Editable format and name; the captured basename is not recomputed on mount</summary>
    public sealed record WorkspaceCompressDraft(ArchiveFormat Format, string OutputName, string BaseName);

    /// <summary>Editable permissions, serialized to avoid mutable references in the runtime</summary>
    public sealed record WorkspacePermissionsDraft(PermissionModel Model, bool Recursive);

    /// <summary>Original context to detect ownership changes without rereading at takeover</summary>
    public sealed record WorkspacePermissionsContext(string EntryName, bool IsDirectory, string OriginalOwner, string OriginalGroup, bool CanSave);

    /// <summary>Captured properties and calculation result owned by the workspace</summary>
    public sealed record WorkspacePropertiesDraft(FileSystemEntry Entry, PermissionModel Permissions, long Size = -1, int FileCount = 0, int DirectoryCount = 0);

    /// <summary>Information captured when About opens</summary>
    public sealed record WorkspaceAboutDraft(string Version, string Runtime, string Platform);

    /// <summary>Explicit allowlist: no password, code, QR, secret or arbitrary backend response</summary>
    public sealed record WorkspaceAuthenticationDraft(bool Enabled, bool Disabled, bool TwoFactorEnabled, bool HasUser, bool MfaPanelVisible, bool PasswordPanelVisible, string Username, string ConfiguredUsername, bool PendingMfaSetup = false);

    /// <summary>Captured terminal identities: no callback and no OS clipboard read during hydration</summary>
    public sealed record WorkspaceTerminalContext(ImmutableArray<int> SessionIds, bool CloseWindow = false, long ClipboardRequestId = 0, string ConfirmationText = "");
}
