using System;
using System.IO;
using System.Linq;

namespace InvenAdClicker.Utils
{
    public static class AppDataMigration
    {
        public static void Prepare(AppPaths paths, string workingDirectory, IAppLogger logger)
        {
            try
            {
                Directory.CreateDirectory(paths.DataDirectory);
                Directory.CreateDirectory(paths.LogDirectory);
                foreach (var name in new[] { "appsettings.json", "credentials.dat" })
                {
                    var target = Path.Combine(paths.DataDirectory, name);
                    var candidates = new[] { Path.Combine(paths.DeploymentDirectory, name), Path.Combine(Path.GetFullPath(workingDirectory), name) }
                        .Distinct(StringComparer.OrdinalIgnoreCase).Where(path => !string.Equals(path, target, StringComparison.OrdinalIgnoreCase))
                        .Where(path => File.Exists(path) || Directory.Exists(path)).ToArray();
                    if (File.Exists(target))
                    {
                        if (candidates.Length > 0)
                            logger.Info($"기존 사용자 파일을 우선 사용합니다: '{target}'. 이전 후보 {string.Join(", ", candidates.Select(path => "'" + path + "'"))}은 보존합니다.");
                        continue;
                    }
                    if (Directory.Exists(target)) throw new IOException($"사용자 파일 경로가 디렉터리입니다: '{target}'");
                    if (candidates.Length == 0) continue;
                    if (candidates.Length > 1)
                        logger.Warn($"이전 후보 충돌: {string.Join(", ", candidates.Select(path => "'" + path + "'"))}. 실행 파일 위치를 우선하여 '{candidates[0]}'을 사용합니다.");
                    // Copy the original ciphertext/config bytes, retaining the source. Never log contents.
                    AtomicFile.Write(target, File.ReadAllBytes(candidates[0]), overwrite: false);
                    logger.Info($"사용자 파일 복사 완료: '{candidates[0]}' -> '{target}' (원본 보존)");
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new IOException($"사용자 데이터 준비 실패: '{paths.DataDirectory}'. {ex.Message}", ex);
            }
        }
    }
}
