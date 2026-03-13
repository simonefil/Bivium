using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Bivium.Models;

namespace Bivium.Controllers
{
    /// <summary>
    /// API controller for reading and writing application settings
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class SettingsController : ControllerBase
    {
        #region Class Variables

        /// <summary>
        /// Application settings monitor for hot-reload
        /// </summary>
        private readonly IOptionsMonitor<CommanderSettings> _settingsMonitor;

        /// <summary>
        /// Hosting environment for resolving appsettings.json path
        /// </summary>
        private readonly IWebHostEnvironment _environment;

        #endregion

        #region Constructor

        /// <summary>
        /// Creates a new SettingsController
        /// </summary>
        /// <param name="settingsMonitor">Settings monitor for hot-reload</param>
        /// <param name="environment">Hosting environment</param>
        public SettingsController(IOptionsMonitor<CommanderSettings> settingsMonitor, IWebHostEnvironment environment)
        {
            this._settingsMonitor = settingsMonitor;
            this._environment = environment;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Gets the current editable extensions list
        /// </summary>
        /// <returns>List of extensions</returns>
        [HttpGet("extensions")]
        public IActionResult GetExtensions()
        {
            List<string> extensions = this._settingsMonitor.CurrentValue.EditableExtensions;
            IActionResult result = this.Ok(extensions);
            return result;
        }

        /// <summary>
        /// Updates the editable extensions list and writes back to appsettings.json
        /// </summary>
        /// <param name="extensions">New list of extensions</param>
        /// <returns>Result</returns>
        [HttpPut("extensions")]
        public IActionResult UpdateExtensions([FromBody] List<string> extensions)
        {
            IActionResult result;

            try
            {
                // Read current appsettings.json
                string settingsPath = Path.Combine(this._environment.ContentRootPath, "appsettings.json");
                string json = System.IO.File.ReadAllText(settingsPath);

                // Parse and update
                JsonDocumentOptions docOptions = new JsonDocumentOptions();
                docOptions.CommentHandling = JsonCommentHandling.Skip;
                JsonDocument doc = JsonDocument.Parse(json, docOptions);

                // Rebuild JSON with updated extensions
                Dictionary<string, object> root = this.JsonElementToDict(doc.RootElement);
                doc.Dispose();

                // Ensure CommanderSettings section exists
                if (!root.ContainsKey("CommanderSettings"))
                {
                    root["CommanderSettings"] = new Dictionary<string, object>();
                }

                Dictionary<string, object> settings = (Dictionary<string, object>)root["CommanderSettings"];
                settings["EditableExtensions"] = extensions;

                // Write back with indentation
                JsonSerializerOptions writeOptions = new JsonSerializerOptions();
                writeOptions.WriteIndented = true;
                string updatedJson = JsonSerializer.Serialize(root, writeOptions);
                System.IO.File.WriteAllText(settingsPath, updatedJson);

                result = this.Ok(new { success = true });
            }
            catch (IOException ex)
            {
                result = this.StatusCode(500, "Failed to write settings: " + ex.Message);
            }

            return result;
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Converts a JsonElement tree into a Dictionary for re-serialization
        /// </summary>
        /// <param name="element">JSON element to convert</param>
        /// <returns>Dictionary representation</returns>
        private Dictionary<string, object> JsonElementToDict(JsonElement element)
        {
            Dictionary<string, object> dict = new Dictionary<string, object>();

            foreach (JsonProperty prop in element.EnumerateObject())
            {
                dict[prop.Name] = this.JsonElementToObject(prop.Value);
            }

            return dict;
        }

        /// <summary>
        /// Converts a JsonElement value to the appropriate .NET type
        /// </summary>
        /// <param name="element">JSON element</param>
        /// <returns>.NET object</returns>
        private object JsonElementToObject(JsonElement element)
        {
            object result;

            if (element.ValueKind == JsonValueKind.Object)
            {
                result = this.JsonElementToDict(element);
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                List<object> list = new List<object>();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    list.Add(this.JsonElementToObject(item));
                }
                result = list;
            }
            else if (element.ValueKind == JsonValueKind.String)
            {
                result = element.GetString();
            }
            else if (element.ValueKind == JsonValueKind.Number)
            {
                if (element.TryGetInt64(out long l))
                {
                    result = l;
                }
                else
                {
                    result = element.GetDouble();
                }
            }
            else if (element.ValueKind == JsonValueKind.True)
            {
                result = true;
            }
            else if (element.ValueKind == JsonValueKind.False)
            {
                result = false;
            }
            else
            {
                result = null;
            }

            return result;
        }

        #endregion
    }
}
