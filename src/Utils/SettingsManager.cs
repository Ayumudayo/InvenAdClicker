using InvenAdClicker.Models;
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;

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
                var defaultTemplate = new AppSettings
                {
                    // Keep default URLs in the generated file as a starting point.
                    // (Runtime binding uses AppSettings.TargetUrls default empty to avoid unintended merging/duplication.)
                    TargetUrls = new[]
                    {
                        "https://www.inven.co.kr/",
                        "https://m.inven.co.kr/",
                        "https://it.inven.co.kr/"
                    }
                };

                JsonObject BuildDefaultAppNode() => BuildCanonicalObject(typeof(AppSettings), existing: null, template: defaultTemplate);

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

                // Schema sync: rebuild AppSettings in canonical order, keep existing values, add missing defaults,
                // remove obsolete keys, and deep-sync nested objects (e.g. Debug).
                var canonicalAppSettingsNode = BuildCanonicalObject(typeof(AppSettings), appSettingsNode, defaultTemplate);
                var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
                var beforeApp = appSettingsNode.ToJsonString(jsonOptions);
                var afterApp = canonicalAppSettingsNode.ToJsonString(jsonOptions);
                if (!string.Equals(beforeApp, afterApp, StringComparison.Ordinal))
                {
                    root["AppSettings"] = canonicalAppSettingsNode;
                    appSettingsNode = canonicalAppSettingsNode;
                    changed = true;
                    _logger.Info("appsettings.json AppSettings 스키마/정렬을 최신 상태로 갱신합니다.");
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
                EnsureMinRuntime(nameof(_settings.PostMessageBufferMilliseconds), 200,
                    () => _settings.PostMessageBufferMilliseconds, v => _settings.PostMessageBufferMilliseconds = v);
                EnsureMinRuntime(nameof(_settings.CollectionAttempts), 1,
                    () => _settings.CollectionAttempts, v => _settings.CollectionAttempts = v);
                EnsureMinRuntime(nameof(_settings.MaxClickAttempts), 1,
                    () => _settings.MaxClickAttempts, v => _settings.MaxClickAttempts = v);
                // 클릭 딜레이는 200ms 미만이면 실패로 간주
                if (_settings.ClickDelayMilliseconds < 200)
                {
                    var msg = $"AppSettings.ClickDelayMilliseconds 값 {_settings.ClickDelayMilliseconds}ms는 허용된 최소 200ms보다 작습니다. 클릭 간 딜레이는 200ms 이상이어야 합니다.";
                    throw new ApplicationException(msg);
                }

                if (_settings.MaxDegreeOfParallelism > 10)
                {
                    _logger.Warn($"AppSettings.MaxDegreeOfParallelism 값 {_settings.MaxDegreeOfParallelism}이 너무 커서 10으로 제한합니다.");
                    _settings.MaxDegreeOfParallelism = 10;
                }

                if ((_settings.TargetUrls == null) || _settings.TargetUrls.Length == 0)
                {
                    _logger.Warn("AppSettings.TargetUrls가 비어 있습니다. 수행할 작업이 없습니다.");
                }
                else
                {
                    var validUrls = new System.Collections.Generic.List<string>();
                    var seen = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
                    var duplicates = new System.Collections.Generic.List<string>();
                    foreach (var url in _settings.TargetUrls)
                    {
                        var trimmed = url?.Trim();
                        if (string.IsNullOrWhiteSpace(trimmed))
                        {
                            continue;
                        }

                        if (Uri.TryCreate(trimmed, UriKind.Absolute, out _))
                        {
                            if (seen.Add(trimmed))
                            {
                                validUrls.Add(trimmed);
                            }
                            else
                            {
                                duplicates.Add(trimmed);
                            }
                        }
                        else
                        {
                            _logger.Warn($"유효하지 않은 URL이 제외되었습니다: {trimmed}");
                        }
                    }

                    if (duplicates.Count > 0)
                    {
                        var uniqueDups = duplicates.Distinct(StringComparer.Ordinal).Take(5).ToArray();
                        var sample = uniqueDups.Length > 0 ? string.Join(", ", uniqueDups) : "-";
                        _logger.Warn($"중복 URL {duplicates.Count}개를 런타임에서 제외했습니다.(파일 미변경) 예: {sample}");
                    }
                    _settings.TargetUrls = validUrls.ToArray();
                }

                if (changed)
                {
                    AtomicWrite(SettingsFileName, root.ToJsonString(jsonOptions));
                    _logger.Info("appsettings.json 구성이 최신 스키마로 갱신되었습니다.");
                }
            }
            catch (Exception ex)
            {
                _logger.Warn($"설정 파일을 자동으로 갱신하지 못했습니다: {ex.Message}");
            }
        }

        private static JsonObject BuildCanonicalObject(Type type, JsonObject? existing, object template)
        {
            var obj = new JsonObject();
            var props = GetOrderedProperties(type);
            foreach (var prop in props)
            {
                JsonNode? existingValue = null;
                bool hasExisting = false;
                if (existing != null)
                {
                    hasExisting = existing.TryGetPropertyValue(prop.Name, out existingValue);
                }

                if (hasExisting && existingValue == null)
                {
                    // Preserve explicit nulls from the user's file.
                    obj[prop.Name] = null;
                    continue;
                }

                object? templateValue = null;
                try { templateValue = prop.GetValue(template); } catch { }

                obj[prop.Name] = SyncNode(prop.PropertyType, existingValue, templateValue);
            }
            return obj;
        }

        private static PropertyInfo[] GetOrderedProperties(Type type)
        {
            return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.GetMethod != null && p.GetIndexParameters().Length == 0)
                .OrderBy(p => p.MetadataToken)
                .ToArray();
        }

        private static bool IsComplexObject(Type type)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (type == typeof(string)) return false;
            if (type.IsPrimitive || type.IsEnum) return false;
            if (type == typeof(decimal)) return false;
            if (typeof(IEnumerable).IsAssignableFrom(type)) return false;
            return type.IsClass;
        }

        private static JsonNode? SyncNode(Type type, JsonNode? existingNode, object? templateValue)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;

            if (IsComplexObject(type))
            {
                var existingObj = existingNode as JsonObject;
                object? nestedTemplate = templateValue;
                if (nestedTemplate == null || nestedTemplate.GetType() != type)
                {
                    try { nestedTemplate = Activator.CreateInstance(type); } catch { }
                }

                if (nestedTemplate == null)
                {
                    // Best-effort: preserve existing object if possible.
                    return existingObj?.DeepClone() ?? new JsonObject();
                }

                return BuildCanonicalObject(type, existingObj, nestedTemplate);
            }

            // Preserve existing values as-is (do not coerce types), only fill in missing nodes.
            if (existingNode != null)
            {
                return existingNode.DeepClone();
            }

            return JsonSerializer.SerializeToNode(templateValue, type);
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

        // 설정 파일 원자적 기록: 같은 디렉토리에 임시 파일을 쓰고 Move로 교체
        private static void AtomicWrite(string path, string content)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(dir)) dir = ".";
                Directory.CreateDirectory(dir);
                var temp = Path.Combine(dir, Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllText(temp, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                File.Move(temp, path, overwrite: true);
            }
            catch
            {
                try { File.WriteAllText(path, content); } catch { }
            }
        }
    }
}
