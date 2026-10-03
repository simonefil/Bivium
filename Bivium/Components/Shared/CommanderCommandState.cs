namespace Bivium.Components.Shared
{
    /// <summary>Proiezione UI di un comando posseduta dal Commander</summary>
    /// <param name="Id">Identificatore locale dell'azione</param>
    /// <param name="Label">Etichetta informativa</param>
    /// <param name="Shortcut">Combinazione già supportata, oppure stringa vuota</param>
    /// <param name="Enabled">Disponibilità dell'azione indipendente dal focus tastiera</param>
    /// <param name="ShortcutEnabled">Disponibilità effettiva della combinazione nel contesto corrente</param>
    public sealed record CommanderCommandState(string Id, string Label, string Shortcut, bool Enabled, bool ShortcutEnabled);
}
