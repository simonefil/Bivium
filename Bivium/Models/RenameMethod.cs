namespace Bivium.Models
{
    /// <summary>
    /// Holds all parameters for a single rename method instance
    /// </summary>
    public class RenameMethod
    {
        #region Common

        /// <summary>
        /// The type of rename method
        /// </summary>
        public RenameMethodType MethodType { get; set; } = RenameMethodType.Replace;

        #endregion

        #region Replace Parameters

        /// <summary>
        /// Text to search for
        /// </summary>
        public string SearchText { get; set; } = "";

        /// <summary>
        /// Text to replace with
        /// </summary>
        public string ReplaceText { get; set; } = "";

        /// <summary>
        /// Whether the search is case sensitive
        /// </summary>
        public bool CaseSensitive { get; set; } = false;

        /// <summary>
        /// Whether the search text is a regular expression
        /// </summary>
        public bool UseRegex { get; set; } = false;

        #endregion

        #region Add Parameters

        /// <summary>
        /// Text to insert
        /// </summary>
        public string InsertText { get; set; } = "";

        /// <summary>
        /// Position index for insertion
        /// </summary>
        public int InsertPosition { get; set; } = 0;

        /// <summary>
        /// Whether to count position from the end
        /// </summary>
        public bool FromEnd { get; set; } = false;

        #endregion

        #region Remove Parameters

        /// <summary>
        /// True to remove by pattern, false to remove by position
        /// </summary>
        public bool RemoveByPattern { get; set; } = false;

        /// <summary>
        /// Start index for position-based removal
        /// </summary>
        public int RemoveStartIndex { get; set; } = 0;

        /// <summary>
        /// Number of characters to remove
        /// </summary>
        public int RemoveCount { get; set; } = 0;

        /// <summary>
        /// Whether to count position from the end
        /// </summary>
        public bool RemoveFromEnd { get; set; } = false;

        /// <summary>
        /// Pattern text for pattern-based removal
        /// </summary>
        public string RemovePattern { get; set; } = "";

        /// <summary>
        /// Whether the remove pattern search is case sensitive
        /// </summary>
        public bool RemovePatternCaseSensitive { get; set; } = false;

        /// <summary>
        /// Whether the remove pattern is a regular expression
        /// </summary>
        public bool RemovePatternUseRegex { get; set; } = false;

        #endregion

        #region NewCase Parameters

        /// <summary>
        /// Case mode: 0=lowercase, 1=UPPERCASE, 2=Title Case
        /// </summary>
        public int CaseMode { get; set; } = 0;

        /// <summary>
        /// Case scope: 0=name only, 1=extension only, 2=full name
        /// </summary>
        public int CaseScope { get; set; } = 0;

        #endregion

        #region NewName Parameters

        /// <summary>
        /// Name pattern template with tags
        /// </summary>
        public string NamePattern { get; set; } = "<Name>.<Ext>";

        #endregion

        #region Trim Parameters

        /// <summary>
        /// Characters to trim (default: space)
        /// </summary>
        public string TrimCharacters { get; set; } = " ";

        /// <summary>
        /// Trim location: 0=start, 1=end, 2=both
        /// </summary>
        public int TrimLocation { get; set; } = 2;

        /// <summary>
        /// Trim scope: 0=name only, 1=extension only, 2=full name
        /// </summary>
        public int TrimScope { get; set; } = 0;

        #endregion

        #region Public Methods

        /// <summary>
        /// Creates a deep copy of this rename method
        /// </summary>
        /// <returns>New RenameMethod with same values</returns>
        public RenameMethod Clone()
        {
            RenameMethod copy = new RenameMethod();
            copy.MethodType = this.MethodType;
            copy.SearchText = this.SearchText;
            copy.ReplaceText = this.ReplaceText;
            copy.CaseSensitive = this.CaseSensitive;
            copy.UseRegex = this.UseRegex;
            copy.InsertText = this.InsertText;
            copy.InsertPosition = this.InsertPosition;
            copy.FromEnd = this.FromEnd;
            copy.RemoveByPattern = this.RemoveByPattern;
            copy.RemoveStartIndex = this.RemoveStartIndex;
            copy.RemoveCount = this.RemoveCount;
            copy.RemoveFromEnd = this.RemoveFromEnd;
            copy.RemovePattern = this.RemovePattern;
            copy.RemovePatternCaseSensitive = this.RemovePatternCaseSensitive;
            copy.RemovePatternUseRegex = this.RemovePatternUseRegex;
            copy.CaseMode = this.CaseMode;
            copy.CaseScope = this.CaseScope;
            copy.NamePattern = this.NamePattern;
            copy.TrimCharacters = this.TrimCharacters;
            copy.TrimLocation = this.TrimLocation;
            copy.TrimScope = this.TrimScope;
            return copy;
        }

        /// <summary>
        /// Returns a short display name for this method configuration
        /// </summary>
        /// <returns>Human-readable summary string</returns>
        public string GetDisplayName()
        {
            string result = "";

            if (this.MethodType == RenameMethodType.Replace)
            {
                result = "Replace: " + this.SearchText + " -> " + this.ReplaceText;
            }
            else if (this.MethodType == RenameMethodType.Add)
            {
                result = "Add: \"" + this.InsertText + "\" at " + this.InsertPosition;
            }
            else if (this.MethodType == RenameMethodType.Remove)
            {
                if (this.RemoveByPattern)
                {
                    result = "Remove: " + this.RemovePattern;
                }
                else
                {
                    result = "Remove: " + this.RemoveCount + " chars at " + this.RemoveStartIndex;
                }
            }
            else if (this.MethodType == RenameMethodType.NewCase)
            {
                string[] modes = { "lowercase", "UPPERCASE", "Title Case" };
                int modeIndex = this.CaseMode >= 0 && this.CaseMode < modes.Length ? this.CaseMode : 0;
                result = "Case: " + modes[modeIndex];
            }
            else if (this.MethodType == RenameMethodType.NewName)
            {
                result = "Name: " + this.NamePattern;
            }
            else if (this.MethodType == RenameMethodType.Trim)
            {
                string[] locations = { "start", "end", "both" };
                int locIndex = this.TrimLocation >= 0 && this.TrimLocation < locations.Length ? this.TrimLocation : 2;
                result = "Trim: " + locations[locIndex];
            }

            return result;
        }

        #endregion
    }
}
