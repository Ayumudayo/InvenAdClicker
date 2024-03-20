using System.Text.Json;
using InvenAdClicker.@struct;

namespace InvenAdClicker.helper
{
    public static class SettingsManager
    {
        private static readonly string _settingsFilePath = "AppSettings.json";

        public static AppSettings LoadSettings()
        {
            if (!File.Exists(_settingsFilePath))
            {
                CreateDefaultSettingsFile();
            }

            string settingsFile = File.ReadAllText(_settingsFilePath);
            return JsonSerializer.Deserialize<AppSettings>(settingsFile) ?? new AppSettings();
        }

        private static void CreateDefaultSettingsFile()
        {
            var defaultSettings = new AppSettings
            {
                MaxIter = 40,
                MaxWorker = 2,
                MaxSet = 10,
                IterationInterval = 500,
                ClickIframeInterval = 500
            };

            var options = new JsonSerializerOptions { WriteIndented = true };
            string jsonString = JsonSerializer.Serialize(defaultSettings, options);
            File.WriteAllText(_settingsFilePath, jsonString);
        }
    }
}