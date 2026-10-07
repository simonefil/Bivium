using System;
using System.Collections.Immutable;

namespace Bivium.Models
{
    /// <summary>Lease and identity of only the visual owners of the current render</summary>
    public sealed record WorkspaceSurfaceOwner(string AttachmentId, long Generation, Guid WorkflowId, Guid QuestionId, WorkspaceWorkflowPhase Phase, Guid UploadId);

    /// <summary>App-owned menu: open identities, burger and navigation context, without commands</summary>
    public sealed record WorkspaceMenuDraft(long Revision, ImmutableArray<string> OpenIds, bool ResponsiveOpen = false, string NavigationParent = "", string ActiveItem = "", bool Focused = false);

    /// <summary>Immutable entry captured when the context menu opens</summary>
    public sealed record WorkspaceContextEntry(string Path, string Name, bool IsDirectory);

    /// <summary>Context menu opening, independent of the panels of the following mount</summary>
    public sealed record WorkspaceContextMenuDraft(long Revision, Guid Id, bool Visible, int PanelIndex, string BasePath, string FocusedPath, ImmutableArray<string> SelectedPaths, ImmutableArray<WorkspaceContextEntry> Entries, double X, double Y, bool IsDirectory, bool IsArchive, bool IsEditable, bool HasSelection, bool IsMultiSelection, string ArchiveBaseName, double ViewportWidth = 0, double ViewportHeight = 0, string ActiveItem = "", bool Focused = false);

    /// <summary>Scroll of a surface identified by the app, not by a DOM path</summary>
    public sealed record WorkspaceSurfaceScroll(string Key, double Top, double Left);

    /// <summary>Standard selection of a field identified by the app, without its value</summary>
    public sealed record WorkspaceSurfaceSelection(string Key, int Start, int End, string Direction);

    /// <summary>Official Format popup: opening, visual index and scroll, not the confirmed value</summary>
    public sealed record WorkspaceFormatPopupVisual(bool Open, int HighlightIndex, double ScrollTop, double ScrollLeft, bool Focused = false);

    /// <summary>Measurements of the resizable textarea, without serializing style or content</summary>
    public sealed record WorkspaceTextareaGeometry(string Key, double Width, double Height, string Resize);

    /// <summary>Focus, field selection, scroll, Format popup and textarea resize; no field values, HTML or delegates</summary>
    public sealed record WorkspaceDialogVisualDraft(long Revision, Guid OwnerId, Guid QuestionId, WorkspaceWorkflowPhase Phase, string Surface, string FocusKey, int SelectionStart, int SelectionEnd, string SelectionDirection, ImmutableArray<WorkspaceSurfaceScroll> Scrolls, WorkspaceFormatPopupVisual FormatPopup = null, WorkspaceTextareaGeometry[] TextareaGeometries = null);
}
