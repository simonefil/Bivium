namespace Bivium.Components.Panel
{
    /// <summary>
    /// Modifica atomica del layout verticale di un pannello
    /// </summary>
    /// <param name="SizePercent">Percentuale occupata dall'albero</param>
    /// <param name="Collapsed">Indica se l'albero è compresso</param>
    public sealed record PanelTreeLayoutChange(double SizePercent, bool Collapsed);
}
