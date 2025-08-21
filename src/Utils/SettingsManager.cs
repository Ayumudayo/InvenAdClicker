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
                // 기본 설정 파일이 없으면 현재 런타임 기본값으로 생성
                JsonObject BuildDefaultAppNode()
                {
                    var appNode = JsonSerializer.SerializeToNode(_settings, typeof(AppSettings)) as JsonObject ?? new JsonObject();
                    // 배열 필드는 명시적 빈 배열로 초기화하여 사용자가 편집 가능
                    if (appNode["TargetUrls"] is null) appNode["TargetUrls"] = new JsonArray();
                    return appNode;
                }

                JsonObject root;
                bool changed = false;
                // 로컬 유틸: 파일 읽기/파싱 시도. 실패 시 백업하고 false 반환
                bool TryReadText(out string text)
                {
                    try
                    {
                        text = File.ReadAllText(SettingsFileName);
                        return true;
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn($"설정 파일을 읽지 못했습니다: {ex.Message}. 백업 후 기본으로 전환합니다.");
                        TryBackup(SettingsFileName);
                        text = string.Empty;
                        return false;
                    }
                }

                bool TryParseRoot(string text, out JsonObject parsedRoot)
                {
                    try
                    {
                        var node = JsonNode.Parse(text);
                        if (node is JsonObject obj)
                        {
                            parsedRoot = obj;
                            return true;
                        }
                        _logger.Warn("설정 파일의 루트가 객체가 아닙니다. 백업 후 기본으로 전환합니다.");
                        TryBackup(SettingsFileName);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn($"설정 파일 JSON 파싱 실패: {ex.Message}. 백업 후 기본으로 전환합니다.");
                        TryBackup(SettingsFileName);
                    }
                    parsedRoot = null!;
                    return false;
                }

                if (!File.Exists(SettingsFileName))
                {
                    _logger.Info($"설정 파일이 없어 기본 구성을 메모리에서 생성합니다: '{SettingsFileName}'");
                    root = new JsonObject { ["AppSettings"] = BuildDefaultAppNode() };
                    changed = true;
                }
                else if (TryReadText(out var raw) && TryParseRoot(raw, out var parsed))
                {
                    root = parsed;
                }
                else
                {
                    root = new JsonObject { ["AppSettings"] = BuildDefaultAppNode() };
                    changed = true;
                }

                var appSettingsNode = root["AppSettings"] as JsonObject;
                
                if (appSettingsNode is null)
                {
                    _logger.Info("AppSettings 섹션이 없어 기본 섹션을 생성합니다.");
                    appSettingsNode = BuildDefaultAppNode();
                    root["AppSettings"] = appSettingsNode;
                    changed = true;
                }
                if (appSettingsNode is null)
                {
                    _logger.Info("AppSettings 섹션이 없어 기본 섹션을 생성합니다.");
                    appSettingsNode = BuildDefaultAppNode();
                    root["AppSettings"] = appSettingsNode;
                    changed = true;
                }

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
                    File.WriteAllText(SettingsFileName, root.ToJsonString(options));
                    _logger.Info("appsettings.json 구성이 최신 스키마로 갱신되었습니다.");
                }
            }
            catch (Exception ex)
            {
                _logger.Warn($"설정 파일을 자동으로 갱신하지 못했습니다: {ex.Message}");
            }
        }

        private static void TryBackup(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                var backup = path + ".bak." + DateTime.Now.ToString("yyyyMMddHHmmss");
                File.Move(path, backup);
            }
            catch { }
        }
    }
}
