using System;
using System.Collections.Immutable;

namespace Bivium.Models
{
    /// <summary>Formato e nome modificabili; il basename catturato non viene ricalcolato al mount</summary>
    public sealed record WorkspaceCompressDraft(ArchiveFormat Format, string OutputName, string BaseName);

    /// <summary>Permessi modificabili, serializzati per evitare riferimenti mutabili nel runtime</summary>
    public sealed record WorkspacePermissionsDraft(PermissionModel Model, bool Recursive);

    /// <summary>Contesto originale per rilevare i cambi ownership senza rileggere al takeover</summary>
    public sealed record WorkspacePermissionsContext(string EntryName, bool IsDirectory, string OriginalOwner, string OriginalGroup, bool CanSave);

    /// <summary>Proprietà catturate e risultato del calcolo posseduto dal workspace</summary>
    public sealed record WorkspacePropertiesDraft(FileSystemEntry Entry, PermissionModel Permissions, long Size = -1, int FileCount = 0, int DirectoryCount = 0);

    /// <summary>Informazioni catturate all'apertura di About</summary>
    public sealed record WorkspaceAboutDraft(string Version, string Runtime, string Platform);

    /// <summary>Allowlist esplicita: nessuna password, codice, QR, segreto o risposta backend arbitraria</summary>
    public sealed record WorkspaceAuthenticationDraft(bool Enabled, bool Disabled, bool TwoFactorEnabled, bool HasUser, bool MfaPanelVisible, bool PasswordPanelVisible, string Username, string ConfiguredUsername, bool PendingMfaSetup = false);

    /// <summary>Identità terminali catturate: nessun callback e nessun clipboard OS letto durante hydration</summary>
    public sealed record WorkspaceTerminalContext(ImmutableArray<int> SessionIds, bool CloseWindow = false, long ClipboardRequestId = 0, string ConfirmationText = "");
}
