using InvenAdClicker.Models;
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace InvenAdClicker.Utils
{
    public class SettingsManager
    {
        private readonly AppSettings _settings;
        private readonly IAppLogger _logger;
        private const string SettingsFileName = "appsettings.json";

        public SettingsManager(AppSettings settings, IAppLogger logger)
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

                    // 값 검증 및 보정
                    bool updated = false;
                    int EnsureMin(string name, int min, int value)
                    {
                        if (value < min)
                        {
                            _logger.Warn($"AppSettings.{name} 값 {value}가 최소값 {min}보다 작아 {min}으로 보정합니다.");
                            updated = true;
                            if (appSettingsNode != null) appSettingsNode[name] = min;
                            return min;
                        }
                        return value;
                    }

                    _settings.MaxDegreeOfParallelism = EnsureMin(nameof(_settings.MaxDegreeOfParallelism), 1, _settings.MaxDegreeOfParallelism);
                    _settings.IframeTimeoutMilliSeconds = EnsureMin(nameof(_settings.IframeTimeoutMilliSeconds), 100, _settings.IframeTimeoutMilliSeconds);
                    _settings.PageLoadTimeoutMilliseconds = EnsureMin(nameof(_settings.PageLoadTimeoutMilliseconds), 1000, _settings.PageLoadTimeoutMilliseconds);
                    _settings.CommandTimeoutMilliSeconds = EnsureMin(nameof(_settings.CommandTimeoutMilliSeconds), 1000, _settings.CommandTimeoutMilliSeconds);
                    _settings.CollectionAttempts = EnsureMin(nameof(_settings.CollectionAttempts), 1, _settings.CollectionAttempts);
                    _settings.MaxClickAttempts = EnsureMin(nameof(_settings.MaxClickAttempts), 1, _settings.MaxClickAttempts);
                    _settings.ClickDelayMilliseconds = EnsureMin(nameof(_settings.ClickDelayMilliseconds), 300, _settings.ClickDelayMilliseconds);

                    if ((_settings.TargetUrls == null) || _settings.TargetUrls.Length == 0)
                    {
                        _logger.Warn("AppSettings.TargetUrls가 비어 있습니다. 수행할 작업이 없습니다.");
                    }

                    if (updated && appSettingsNode != null)
                    {
                        var options = new JsonSerializerOptions { WriteIndented = true };
                        File.WriteAllText(SettingsFileName, jsonNode!.ToJsonString(options));
                        _logger.Info("유효성 검사 결과에 따라 appsettings.json을 보정했습니다.");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warn($"설정 파일을 자동으로 갱신하지 못했습니다: {ex.Message}");
            }
        }
    }
}
