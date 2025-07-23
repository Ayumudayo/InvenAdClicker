using Newtonsoft.Json;
using InvenAdClicker.Utils;
using System;
using System.IO;

namespace InvenAdClicker.Config
{
    public class SettingsManager
    {
        private const string SettingsFile = "Settings.json";
        private readonly AppSettings _settings;

        public SettingsManager(ILogger logger)
        {
            if (!File.Exists(SettingsFile))
            {
                logger.Warn($"{SettingsFile}이 존재하지 않습니다. 기본 템플릿을 생성합니다.");
                CreateDefaultSettingsFile(logger);
                throw new ApplicationException($"{SettingsFile} 파일이 생성되었습니다. 설정을 완료 후 재실행해주세요.");
            }

            try
            {
                logger.Info($"Loading configuration from {SettingsFile}...");
                var json = File.ReadAllText(SettingsFile);
                _settings = JsonConvert.DeserializeObject<AppSettings>(json)
                            ?? throw new ApplicationException("Settings.json 파싱 결과가 null입니다.");
                logger.Info("Configuration loaded successfully.");
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException)
            {
                logger.Error("Settings.json 로드/파싱 오류", ex);
                throw;
            }
        }

        public AppSettings Settings => _settings;

        private void CreateDefaultSettingsFile(ILogger logger)
        {
            var defaultSettings = new AppSettings
            {
                MaxDegreeOfParallelism = 4,
                IframeTimeoutSeconds = 5,
                RetryCount = 1,
                ClickDelayMilliseconds = 500,
                PageLoadTimeoutMilliseconds = 1000,
                CollectionAttempts = 1,
                DisableImages = true,
                DisableCss = true,
                DisableFonts = true,
                TargetUrls = new[] { "https://www.inven.co.kr/" }
            };

            var json = JsonConvert.SerializeObject(defaultSettings, Formatting.Indented);
            File.WriteAllText(SettingsFile, json);
            logger.Info("기본 Settings.json 템플릿을 생성했습니다.");
        }
    }
}
