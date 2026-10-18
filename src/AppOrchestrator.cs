using InvenAdClicker.Models;
using InvenAdClicker.Services.Interfaces;
using InvenAdClicker.Services.Pipeline;
using InvenAdClicker.Services.Playwright;
using InvenAdClicker.Utils;
using Microsoft.Playwright;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace InvenAdClicker
{
    public class AppOrchestrator
    {
        private readonly AppSettings _settings;
        private readonly IAppLogger _logger;
        private readonly Encryption _encryption;
        private readonly ProgressTracker _progress;
        private readonly SettingsManager _settingsManager;

        public AppOrchestrator(
            AppSettings settings,
            IAppLogger logger,
            Encryption encryption,
            ProgressTracker progress,
            SettingsManager settingsManager)
        {
            _settings = settings;
            _logger = logger;
            _encryption = encryption;
            _progress = progress;
            _settingsManager = settingsManager;
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        public async Task<int> RunAsync(CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();

            bool runtimeInstalledNeedsRestart = false;

            _settingsManager.ValidateAndUpdateSettings();
            _progress.Initialize(_settings.TargetUrls ?? Array.Empty<string>());

            try
            {
                Console.WriteLine("계정 유효성 검증 및 브라우저 서비스 초기화 중...");

                if (await PlaywrightBootstrap.EnsureInstalledIfMissingAsync(_logger))
                {
                    runtimeInstalledNeedsRestart = true;
                    Console.WriteLine();
                    Console.WriteLine("Playwright 런타임을 설치했습니다. 프로그램을 다시 시작해 주세요.");
                    return 0;
                }

                using var playwright = await Playwright.CreateAsync();
                var launchOptions = new BrowserTypeLaunchOptions
                {
                    Headless = _settings.Debug.Enabled ? _settings.Debug.Headless : true
                };

                if (!_settings.Debug.Enabled)
                {
                    launchOptions.Args = new[]
                    {
                        "--blink-settings=imagesEnabled=false",
                        "--disable-remote-fonts"
                    };
                }

                Task<IBrowser> LaunchBrowserAsync() => playwright.Chromium.LaunchAsync(launchOptions);

                await using var browser = await LaunchBrowserAsync();
                await LoginVerifier.VerifyPlaywrightAsync(browser, _settings, _logger, _encryption, cancellationToken);
                await using var playwrightPool = new PlaywrightBrowserPool(browser, _settings, _logger, _encryption, LaunchBrowserAsync);
                await playwrightPool.InitializePoolAsync(cancellationToken);

                IAdCollector<IPage> adCollector = new PlaywrightAdCollector(_settings, _logger, _progress);
                IAdClicker<IPage> adClicker = new PlaywrightAdClicker(_settings, _logger, playwrightPool);
                var runner = new GenericPipelineRunner<IPage>(_settings, _logger, playwrightPool, _progress, adCollector, adClicker);

                Console.WriteLine("초기화 성공. 프로세스를 시작합니다...");
                await Task.Delay(1000, cancellationToken);

                SafeSetCursorVisible(false);
                SafeClearConsole();

                using var progressCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var progressTask = _progress.PrintProgressAsync(progressCts.Token);

                try
                {
                    await runner.RunAsync(_settings.TargetUrls ?? Array.Empty<string>(), cancellationToken);
                }
                finally
                {
                    _progress.StopProgress();
                    progressCts.Cancel();
                    try { await progressTask; } catch { }
                }

                _logger.Info("모든 작업이 완료되었습니다.");
                return 0;
            }
            catch (OperationCanceledException)
            {
                _logger.Warn("사용자에 의해 작업이 취소되었습니다.");
                return 1;
            }
            catch (ApplicationException ex)
            {
                Console.WriteLine(ex.Message);
                _logger.Warn(ex.Message);
                return 1;
            }
            catch (Exception ex)
            {
                var errorMessage = "예기치 못한 오류가 발생했습니다. 자세한 내용은 로그를 확인하세요.";
                Console.WriteLine(errorMessage);
                _logger.Fatal(errorMessage, ex);
                return 2;
            }
            finally
            {
                _progress.StopProgress();
                SafeSetCursorVisible(true);

                stopwatch.Stop();
                if (!runtimeInstalledNeedsRestart)
                {
                    int minutes = (int)stopwatch.Elapsed.TotalMinutes;
                    int seconds = stopwatch.Elapsed.Seconds;

                    Console.WriteLine();
                    Console.WriteLine($"총 실행 시간: {minutes}분 {seconds}초");
                    Console.WriteLine("작업 완료.");
                }
                WaitForKeyPressIfInteractive();
            }
        }

        private static void SafeSetCursorVisible(bool visible)
        {
            if (!Console.IsOutputRedirected)
            {
                try { Console.CursorVisible = visible; } catch { }
            }
        }

        private static void SafeClearConsole()
        {
            if (!Console.IsOutputRedirected)
            {
                try { Console.Clear(); } catch { }
            }
        }

        private static void WaitForKeyPressIfInteractive()
        {
            if (!Console.IsInputRedirected && Environment.UserInteractive)
            {
                Console.WriteLine("아무 키나 눌러 종료합니다...");
                try { Console.ReadKey(); } catch { }
            }
        }
    }
}
