using System;
using System.Collections.Immutable;

namespace Bivium.Models
{
    /// <summary>Selection endpoint on the existing timeline, or row of the alternate buffer</summary>
    public sealed record WorkspaceTerminalPosition(long Row, int Column);

    /// <summary>Lightweight view of a single session; no text, screen or history in the checkpoint</summary>
    public sealed record WorkspaceTerminalViewState(long Revision, int SessionId, long ObservedRevision, bool AlternateBuffer, long TopRow, double RowFraction, double HorizontalCells, bool Following, long PromptRow = -1, WorkspaceTerminalPosition SelectionAnchor = null, WorkspaceTerminalPosition SelectionFocus = null);

    /// <summary>Scroll strip anchored to the session represented by the header, not to the tab index</summary>
    public sealed record WorkspaceTerminalStripViewState(long Revision, int AnchorSessionId = 0, double AnchorFraction = 0, double Left = 0, double Top = 0);

    /// <summary>Official popup identified by the application control</summary>
    public sealed record WorkspaceDropDownViewState(string ControlId, WorkspaceFormatPopupVisual Popup);

    /// <summary>Single view of the Renamer session: popup, scroll, focus and selections, never form values</summary>
    public sealed record WorkspaceRenamerViewState(long Revision, Guid SessionId, ImmutableArray<WorkspaceDropDownViewState> Popups, string FocusKey = "", bool Focused = false, ImmutableArray<WorkspaceSurfaceScroll> Scrolls = default, ImmutableArray<WorkspaceSurfaceSelection> Selections = default);
}
