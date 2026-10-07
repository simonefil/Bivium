using System;
using System.Collections.Immutable;

namespace Bivium.Models
{
    /// <summary>Endpoint della selezione sulla timeline esistente, oppure riga del buffer alternativo</summary>
    public sealed record WorkspaceTerminalPosition(long Row, int Column);

    /// <summary>Vista leggera della singola sessione; nessun testo, screen o history nel checkpoint</summary>
    public sealed record WorkspaceTerminalViewState(long Revision, int SessionId, long ObservedRevision, bool AlternateBuffer, long TopRow, double RowFraction, double HorizontalCells, bool Following, long PromptRow = -1, WorkspaceTerminalPosition SelectionAnchor = null, WorkspaceTerminalPosition SelectionFocus = null);

    /// <summary>Scroll strip ancorato alla sessione rappresentata dall'header, non all'indice del tab</summary>
    public sealed record WorkspaceTerminalStripViewState(long Revision, int AnchorSessionId = 0, double AnchorFraction = 0, double Left = 0, double Top = 0);

    /// <summary>Popup ufficiale identificato dal controllo applicativo</summary>
    public sealed record WorkspaceDropDownViewState(string ControlId, WorkspaceFormatPopupVisual Popup);

    /// <summary>Vista unica della sessione Renamer: popup, scroll, focus e selezioni, mai valori della form</summary>
    public sealed record WorkspaceRenamerViewState(long Revision, Guid SessionId, ImmutableArray<WorkspaceDropDownViewState> Popups, string FocusKey = "", bool Focused = false, ImmutableArray<WorkspaceSurfaceScroll> Scrolls = default, ImmutableArray<WorkspaceSurfaceSelection> Selections = default);
}
