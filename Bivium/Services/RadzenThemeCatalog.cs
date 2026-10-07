using System;
using System.Collections.Generic;

namespace Bivium.Services
{
    /// <summary>
    /// Catalog of the themes shipped by Radzen.Blazor 11.4.2
    /// </summary>
    public static class RadzenThemeCatalog
    {
        #region Costanti

        /// <summary>
        /// Theme used when the configuration is not valid
        /// </summary>
        public const string DEFAULT_THEME = "software-dark";

        #endregion

        #region Variabili statiche

        /// <summary>
        /// Names of the official themes actually included in the package
        /// </summary>
        private static readonly string[] s_themes =
        {
            "default",
            "dark",
            "humanistic",
            "humanistic-dark",
            "material",
            "material-dark",
            "software",
            "software-dark",
            "standard",
            "standard-dark"
        };

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Returns the available themes in the order shown by the UI
        /// </summary>
        public static IReadOnlyList<string> GetThemes()
        {
            return s_themes;
        }

        /// <summary>
        /// Normalizes a supported theme
        /// </summary>
        /// <param name="theme">Received theme name</param>
        /// <param name="normalizedTheme">Canonical theme name</param>
        /// <returns>True when the theme is supported</returns>
        public static bool TryNormalize(string theme, out string normalizedTheme)
        {
            string candidate = theme?.Trim() ?? "";
            for (int i = 0; i < s_themes.Length; i++)
            {
                if (string.Equals(s_themes[i], candidate, StringComparison.OrdinalIgnoreCase))
                {
                    normalizedTheme = s_themes[i];
                    return true;
                }
            }

            normalizedTheme = DEFAULT_THEME;
            return false;
        }

        /// <summary>
        /// Returns a supported theme or the default fallback
        /// </summary>
        /// <param name="theme">Configured theme name</param>
        /// <returns>Canonical theme name</returns>
        public static string NormalizeOrDefault(string theme)
        {
            string normalizedTheme;
            if (TryNormalize(theme, out normalizedTheme))
                return normalizedTheme;
            return DEFAULT_THEME;
        }

        #endregion
    }
}
