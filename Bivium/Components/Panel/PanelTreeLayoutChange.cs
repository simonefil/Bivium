namespace Bivium.Components.Panel
{
    /// <summary>
    /// Atomic change of the vertical layout of a panel
    /// </summary>
    /// <param name="SizePercent">Percentage occupied by the tree</param>
    /// <param name="Collapsed">Indicates whether the tree is collapsed</param>
    public sealed record PanelTreeLayoutChange(double SizePercent, bool Collapsed);
}
