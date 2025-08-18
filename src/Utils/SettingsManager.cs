using InvenAdClicker.Models;
using InvenAdClicker.Utils;
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace InvenAdClicker.Utils
{
    public class SettingsManager
    {
        private readonly AppSettings _settings;
        private readonly ILogger _logger;
        private const string SettingsFileName = "appsettings.json";

        public SettingsManager(AppSettings settings, ILogger logger)
        {
            _settings = settings;
            _logger = logger;
        }

        public void ValidateAndUpdateSettings()
        {
            try
            {
                var jsonString = File.ReadAllText(SettingsFileName);
                var jsonNode = JsonNode.Parse(jsonString);

                if (jsonNode != null)
                {
                    var appSettingsNode = jsonNode["AppSettings"];
                    if (appSettingsNode != null && appSettingsNode.AsObject().TryGetPropertyValue("MaxClickAttempts", out var _) == false)
                    {
                        _logger.Info("Old settings file detected. Updating with 'MaxClickAttempts'.");
                        appSettingsNode["MaxClickAttempts"] = _settings.MaxClickAttempts;
                        
                        var options = new JsonSerializerOptions { WriteIndented = true };
                        File.WriteAllText(SettingsFileName, jsonNode.ToJsonString(options));
                        _logger.Info("'appsettings.json' has been updated successfully.");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warn($"Could not automatically update settings file: {ex.Message}");
            }
        }
    }
}
