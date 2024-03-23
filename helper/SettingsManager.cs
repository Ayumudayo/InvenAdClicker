using System.Text.Json;
using System.IO;
using InvenAdClicker.@struct;

namespace InvenAdClicker.helper
{
    public static class SettingsManager
    {
        private static readonly string _settingsFilePath = "Settings.json";

        private static RootSettings LoadSettings()
        {
            if (!File.Exists(_settingsFilePath))
            {
                CreateDefaultSettingsFile();
            }

            string settingsFile = File.ReadAllText(_settingsFilePath);
            return JsonSerializer.Deserialize<RootSettings>(settingsFile) ?? new RootSettings();
        }

        public static AppSettings LoadAppSettings()
        {
            RootSettings settings = LoadSettings();
            return settings.AppSettings;
        }

        public static List<string> LoadURL()
        {
            RootSettings settings = LoadSettings();
            return settings.URL.Concat(settings.GoodsURL).Concat(settings.MobileURL).ToList();
        }

        private static void CreateDefaultSettingsFile()
        {
            var defaultSettings = new RootSettings
            {
                AppSettings = new AppSettings
                {
                    MaxIter = 40,
                    MaxWorker = 2,
                    MaxSet = 10,
                    IterationInterval = 500,
                    ClickIframeInterval = 500
                },

                URL = new List<string>()
                {
                    "https://sc2.inven.co.kr/",
                    "https://www.inven.co.kr/board/sc2/2279",
                    "https://www.inven.co.kr/board/sc2/2279/12367",
                },
                GoodsURL = new List<string>()
                {
                    "https://diablo2.inven.co.kr/",
                    "https://www.inven.co.kr/board/diablo2/5735",
                    "https://ro.inven.co.kr/",
                },
                MobileURL = new List<string>()
                {
                    "https://m.inven.co.kr/diablo2/",
                    "https://m.inven.co.kr/wow/",
                    "https://m.inven.co.kr/fconline/",
                }
            };

            var options = new JsonSerializerOptions { WriteIndented = true };
            string jsonString = JsonSerializer.Serialize(defaultSettings, options);
            File.WriteAllText(_settingsFilePath, jsonString);
        }
    }
}