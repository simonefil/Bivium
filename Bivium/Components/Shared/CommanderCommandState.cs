namespace Bivium.Components.Shared
{
    /// <summary>UI projection of a command owned by the Commander</summary>
    /// <param name="Id">Local identifier of the action</param>
    /// <param name="Label">Informational label</param>
    /// <param name="Shortcut">Already supported combination, or an empty string</param>
    /// <param name="Enabled">Action availability, independent of keyboard focus</param>
    /// <param name="ShortcutEnabled">Effective availability of the combination in the current context</param>
    public sealed record CommanderCommandState(string Id, string Label, string Shortcut, bool Enabled, bool ShortcutEnabled);
}
