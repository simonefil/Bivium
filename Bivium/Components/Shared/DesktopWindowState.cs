namespace Bivium.Components.Shared
{
    /// <summary>Proiezione transitoria della finestra desktop, non persistita</summary>
    public sealed record DesktopWindowState(string Id, string Title, string Icon, bool IsMinimized, bool IsActive, bool NeedsAttention);
}
