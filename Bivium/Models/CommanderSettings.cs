namespace Bivium.Models
{
    /// <summary>
    /// Application configuration settings
    /// </summary>
    public class CommanderSettings
    {
        #region Properties

        /// <summary>
        /// Default WebTUI theme name
        /// </summary>
        public string DefaultTheme { get; set; } = "dark";

        /// <summary>
        /// File extensions that can be opened in the editor
        /// </summary>
        public List<string> EditableExtensions { get; set; } = new List<string>();

        #endregion
    }
}
