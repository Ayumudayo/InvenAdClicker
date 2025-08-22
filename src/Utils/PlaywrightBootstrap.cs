using Microsoft.Playwright;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace InvenAdClicker.Utils
{
    public static class PlaywrightBootstrap
    {
        // 브라우저 미설치 시 playwright.ps1을 실행하고 true 반환. 이미 설치되어 있으면 false 반환.
        public static async Task<bool> EnsureInstalledIfMissingAsync(IAppLogger logger)
        {
            try
            {
                using var playwright = await Playwright.CreateAsync();
                // 설치 여부는 Launch 시점에 가장 확실히 검증됨
                await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
                return false; // 정상: 설치됨
            }
            catch (Exception ex)
            {
                var msg = ex.Message ?? string.Empty;
                bool looksMissing = msg.Contains("playwright.ps1", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("not found", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("was not found", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("install", StringComparison.OrdinalIgnoreCase);

                if (!looksMissing)
                    throw; // 다른 유형의 오류는 상위에서 처리

                // 설치 스크립트 실행 시도
                var scriptPath = Path.Combine(AppContext.BaseDirectory, "playwright.ps1");
                if (!File.Exists(scriptPath))
                {
                    logger.Error($"Playwright 설치 스크립트를 찾을 수 없습니다: {scriptPath}");
                    throw new ApplicationException("Playwright 설치 스크립트를 찾을 수 없어 자동 설치를 진행할 수 없습니다.");
                }

                logger.Warn("Playwright가 설치되어 있지 않습니다. 자동으로 설치를 진행합니다.");
                var ok = RunPowershell(scriptPath, "install chromium", out var so, out var se);
                if (!ok)
                {
                    logger.Error("Playwright 자동 설치에 실패했습니다. 수동으로 설치 스크립트를 실행해 주세요.");
                    logger.Debug($"stdout: {so}");
                    logger.Debug($"stderr: {se}");
                    throw new ApplicationException("Playwright 자동 설치 실패");
                }

                logger.Info("Playwright 브라우저 설치가 완료되었습니다.");
                return true; // 설치 진행함
            }
        }

        private static bool RunPowershell(string scriptPath, string args, out string stdout, out string stderr)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-ExecutionPolicy Bypass -File \"{scriptPath}\" {args}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            try
            {
                using var p = Process.Start(psi)!;
                stdout = p.StandardOutput.ReadToEnd();
                stderr = p.StandardError.ReadToEnd();
                p.WaitForExit();
                return p.ExitCode == 0;
            }
            catch (Exception ex)
            {
                stdout = string.Empty;
                stderr = ex.Message;
                return false;
            }
        }
    }
}

