using System;

namespace Bivium.Models
{
    /// <summary>Richiesta transitoria, senza contenuti o credenziali del desktop</summary>
    /// <param name="Id">Identità del tentativo</param>
    /// <param name="OwnerAttachmentId">Owner che deve confermare il drain</param>
    /// <param name="RequesterAttachmentId">Unico destinatario autorizzato</param>
    /// <param name="Generation">Generazione da trasferire</param>
    /// <param name="DeadlineUtc">Termine che interrompe l'attesa, mai autorizzazione alla revoca</param>
    /// <param name="Frozen">Ack del freeze verificato dall'owner nel processo server</param>
    public sealed record WorkspaceHandoffSnapshot(Guid Id, string OwnerAttachmentId, string RequesterAttachmentId, long Generation, DateTime DeadlineUtc, bool Frozen = false);

    /// <summary>Revisioni confermate dai publisher prima del commit atomico</summary>
    /// <param name="WorkspaceRevision">Revisione di pannelli, clipboard e finestre</param>
    /// <param name="EditorId">Documento drenato</param>
    /// <param name="EditorRevision">Checkpoint confermato</param>
    /// <param name="RenamerId">Draft drenato</param>
    /// <param name="RenamerRevision">Revisione della preview materializzata</param>
    public sealed record WorkspaceHandoffStamp(long WorkspaceRevision, Guid EditorId, long EditorRevision, Guid RenamerId, long RenamerRevision);
}
