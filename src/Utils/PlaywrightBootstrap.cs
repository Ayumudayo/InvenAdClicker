using Microsoft.Playwright;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace InvenAdClicker.Utils
{
    public static class PlaywrightBootstrap
    {
        // 브라우저 미설치 시 playwright.ps1을 실행하고 true 반환. 이미 설치되어 있으면 false 반환.
        public static async Task<bool> EnsureInstalledIfMissingAsync(IAppLogger logger, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                PlaywrightDeployment.VerifyAssets(AppContext.BaseDirectory);
            }
            catch (FileNotFoundException ex)
            {
                throw new ApplicationException($"Playwright 배포 파일이 누락되었습니다. 배포 폴더 전체를 다시 복사해 주세요: {ex.FileName}", ex);
            }
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
                var (exitCode, so, se) = await RunPowershellAsync(scriptPath, "install chromium", cancellationToken);
                if (exitCode != 0)
                {
                    logger.Error($"Playwright 자동 설치에 실패했습니다. PowerShell 종료 코드: {exitCode}");
                    if (!string.IsNullOrWhiteSpace(so)) logger.Error($"Playwright 설치 stdout: {so.Trim()}");
                    if (!string.IsNullOrWhiteSpace(se)) logger.Error($"Playwright 설치 stderr: {se.Trim()}");

                    var details = !string.IsNullOrWhiteSpace(se) ? se.Trim() : so.Trim();
                    throw new ApplicationException(string.IsNullOrWhiteSpace(details)
                        ? $"Playwright 자동 설치 실패 (PowerShell 종료 코드: {exitCode})"
                        : $"Playwright 자동 설치 실패: {details}");
                }

                logger.Info("Playwright 브라우저 설치가 완료되었습니다.");
                return true; // 설치 진행함
            }
        }

        private static async Task<(int ExitCode, string Stdout, string Stderr)> RunPowershellAsync(
            string scriptPath, string args, CancellationToken cancellationToken)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\" {args}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            try
            {
                using var p = Process.Start(psi)!;
                var stdoutTask = p.StandardOutput.ReadToEndAsync();
                var stderrTask = p.StandardError.ReadToEndAsync();
                try
                {
                    await p.WaitForExitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    if (!p.HasExited) p.Kill(entireProcessTree: true);
                    await p.WaitForExitAsync();
                    await Task.WhenAll(stdoutTask, stderrTask);
                    throw;
                }
                return (p.ExitCode, await stdoutTask, await stderrTask);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return (-1, string.Empty, ex.Message);
            }
        }
    }
}

