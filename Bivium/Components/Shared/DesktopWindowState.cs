namespace Bivium.Components.Shared
{
    /// <summary>Transient projection of the desktop window, not persisted</summary>
    public sealed record DesktopWindowState(string Id, string Title, string Icon, bool IsMinimized, bool IsActive, bool NeedsAttention);
}
