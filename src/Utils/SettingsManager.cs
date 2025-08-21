using InvenAdClicker.Models;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
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
                if (jsonNode is null)
                    return;

                var appSettingsNode = jsonNode["AppSettings"] as JsonObject;
                if (appSettingsNode is null)
                    return;

                bool changed = false;

                // 새 필드 추가: AppSettings 클래스의 공개 속성 중 JSON에 없는 항목은 기본값으로 추가
                var props = typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.CanRead && p.GetMethod != null);
                var propNames = props.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

                foreach (var prop in props)
                {
                    if (!appSettingsNode.ContainsKey(prop.Name))
                    {
                        var value = prop.GetValue(_settings);
                        var node = JsonSerializer.SerializeToNode(value, prop.PropertyType);
                        appSettingsNode[prop.Name] = node;
                        changed = true;
                        _logger.Info($"구성에 누락된 필드 추가: AppSettings.{prop.Name}");
                    }
                }

                // 사용하지 않게 된 필드 제거: AppSettings에 존재하지 않는 키는 제거
                var existingKeys = appSettingsNode.Select(kvp => kvp.Key).ToList();
                foreach (var key in existingKeys)
                {
                    if (!propNames.Contains(key))
                    {
                        appSettingsNode.Remove(key);
                        changed = true;
                        _logger.Info($"사용하지 않는 필드 제거: AppSettings.{key}");
                    }
                }

                // 사용자 값은 파일에 쓰지 않고, 런타임에서만 최소치 보정
                void EnsureMinRuntime(string name, int min, Func<int> getter, Action<int> setter)
                {
                    var v = getter();
                    if (v < min)
                    {
                        _logger.Warn($"AppSettings.{name} 값 {v}가 최소값 {min}보다 작아 런타임에서 {min}으로 보정합니다.(파일 미변경)");
                        setter(min);
                    }
                }

                EnsureMinRuntime(nameof(_settings.MaxDegreeOfParallelism), 1,
                    () => _settings.MaxDegreeOfParallelism, v => _settings.MaxDegreeOfParallelism = v);
                EnsureMinRuntime(nameof(_settings.IframeTimeoutMilliSeconds), 100,
                    () => _settings.IframeTimeoutMilliSeconds, v => _settings.IframeTimeoutMilliSeconds = v);
                EnsureMinRuntime(nameof(_settings.PageLoadTimeoutMilliseconds), 1000,
                    () => _settings.PageLoadTimeoutMilliseconds, v => _settings.PageLoadTimeoutMilliseconds = v);
                EnsureMinRuntime(nameof(_settings.CommandTimeoutMilliSeconds), 1000,
                    () => _settings.CommandTimeoutMilliSeconds, v => _settings.CommandTimeoutMilliSeconds = v);
                EnsureMinRuntime(nameof(_settings.CollectionAttempts), 1,
                    () => _settings.CollectionAttempts, v => _settings.CollectionAttempts = v);
                EnsureMinRuntime(nameof(_settings.MaxClickAttempts), 1,
                    () => _settings.MaxClickAttempts, v => _settings.MaxClickAttempts = v);
                // 클릭 딜레이는 100ms 미만이면 실패로 간주
                if (_settings.ClickDelayMilliseconds < 100)
                {
                    var msg = $"AppSettings.ClickDelayMilliseconds 값 {_settings.ClickDelayMilliseconds}ms는 허용된 최소 100ms보다 작습니다. 클릭 간 딜레이는 100ms 이상이어야 합니다.";
                    throw new ApplicationException(msg);
                }

                if ((_settings.TargetUrls == null) || _settings.TargetUrls.Length == 0)
                {
                    _logger.Warn("AppSettings.TargetUrls가 비어 있습니다. 수행할 작업이 없습니다.");
                }

                if (changed)
                {
                    var options = new JsonSerializerOptions { WriteIndented = true };
                    File.WriteAllText(SettingsFileName, jsonNode.ToJsonString(options));
                    _logger.Info("appsettings.json 구성이 최신 스키마로 갱신되었습니다.");
                }
            }
            catch (Exception ex)
            {
                _logger.Warn($"설정 파일을 자동으로 갱신하지 못했습니다: {ex.Message}");
            }
        }
    }
}
