using System.Text.Json;

namespace Bivium.Models
{
    /// <summary>
    /// Result envelope returned by JS fetch helpers
    /// </summary>
    public class JsFetchResult
    {
        #region Properties

        public bool Ok { get; set; } = false;

        public int Status { get; set; } = 0;

        public string Text { get; set; } = "";

        public JsonElement Data { get; set; }

        #endregion
    }
}
