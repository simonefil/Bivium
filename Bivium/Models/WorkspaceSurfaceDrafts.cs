using System;
using System.Collections.Immutable;

namespace Bivium.Models
{
    /// <summary>Lease e identità dei soli owner visuali del render corrente</summary>
    public sealed record WorkspaceSurfaceOwner(string AttachmentId, long Generation, Guid WorkflowId, Guid QuestionId, WorkspaceWorkflowPhase Phase, Guid UploadId);

    /// <summary>Menu app-owned: identità aperte, burger e contesto navigazione, senza comandi</summary>
    public sealed record WorkspaceMenuDraft(long Revision, ImmutableArray<string> OpenIds, bool ResponsiveOpen = false, string NavigationParent = "", string ActiveItem = "", bool Focused = false);

    /// <summary>Entry immutabile catturata all'apertura del context menu</summary>
    public sealed record WorkspaceContextEntry(string Path, string Name, bool IsDirectory);

    /// <summary>Apertura context menu, indipendente dai pannelli del mount successivo</summary>
    public sealed record WorkspaceContextMenuDraft(long Revision, Guid Id, bool Visible, int PanelIndex, string BasePath, string FocusedPath, ImmutableArray<string> SelectedPaths, ImmutableArray<WorkspaceContextEntry> Entries, double X, double Y, bool IsDirectory, bool IsArchive, bool IsEditable, bool HasSelection, bool IsMultiSelection, string ArchiveBaseName, double ViewportWidth = 0, double ViewportHeight = 0, string ActiveItem = "", bool Focused = false);

    /// <summary>Scroll di una superficie identificata dall'app, non da un percorso DOM</summary>
    public sealed record WorkspaceSurfaceScroll(string Key, double Top, double Left);

    /// <summary>Selezione standard di un campo identificato dall'app, senza il suo valore</summary>
    public sealed record WorkspaceSurfaceSelection(string Key, int Start, int End, string Direction);

    /// <summary>Popup Format ufficiale: apertura, indice visuale e scroll, non il valore confermato</summary>
    public sealed record WorkspaceFormatPopupVisual(bool Open, int HighlightIndex, double ScrollTop, double ScrollLeft, bool Focused = false);

    /// <summary>Misure della textarea ridimensionabile, senza serializzare style o contenuto</summary>
    public sealed record WorkspaceTextareaGeometry(string Key, double Width, double Height, string Resize);

    /// <summary>Focus, selezione campi, scroll, popup Format e resize textarea; nessun valore dei campi, HTML o delegate</summary>
    public sealed record WorkspaceDialogVisualDraft(long Revision, Guid OwnerId, Guid QuestionId, WorkspaceWorkflowPhase Phase, string Surface, string FocusKey, int SelectionStart, int SelectionEnd, string SelectionDirection, ImmutableArray<WorkspaceSurfaceScroll> Scrolls, WorkspaceFormatPopupVisual FormatPopup = null, WorkspaceTextareaGeometry[] TextareaGeometries = null);
}
