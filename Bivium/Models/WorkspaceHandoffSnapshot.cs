using System;

namespace Bivium.Models
{
    /// <summary>Transient request, without desktop contents or credentials</summary>
    /// <param name="Id">Identity of the attempt</param>
    /// <param name="OwnerAttachmentId">Owner that must confirm the drain</param>
    /// <param name="RequesterAttachmentId">Sole authorized recipient</param>
    /// <param name="Generation">Generation to transfer</param>
    /// <param name="DeadlineUtc">Deadline that ends the wait, never authorization to revoke</param>
    /// <param name="Frozen">Freeze ack verified by the owner in the server process</param>
    public sealed record WorkspaceHandoffSnapshot(Guid Id, string OwnerAttachmentId, string RequesterAttachmentId, long Generation, DateTime DeadlineUtc, bool Frozen = false);

    /// <summary>Revisions confirmed by the publishers before the atomic commit</summary>
    /// <param name="WorkspaceRevision">Revision of panels, clipboard and windows</param>
    /// <param name="EditorId">Drained document</param>
    /// <param name="EditorRevision">Confirmed checkpoint</param>
    /// <param name="RenamerId">Drained draft</param>
    /// <param name="RenamerRevision">Revision of the materialized preview</param>
    public sealed record WorkspaceHandoffStamp(long WorkspaceRevision, Guid EditorId, long EditorRevision, Guid RenamerId, long RenamerRevision);
}
