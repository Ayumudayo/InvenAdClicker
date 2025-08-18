using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using InvenAdClicker.Models;
using InvenAdClicker.Services.Interfaces;
using InvenAdClicker.Utils;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CustomLogger = InvenAdClicker.Utils.ILogger;
using Log4Net = InvenAdClicker.Utils.Log4NetLogger;
using Microsoft.Playwright;
using InvenAdClicker.Services.Playwright;
using InvenAdClicker.Services.Selenium;
using System.IO;
using System.Text.Json.Nodes;

namespace InvenAdClicker
{
    class InvenAdClicker
    {
        public static async Task Main(string[] args)
        {
            var stopwatch = Stopwatch.StartNew();

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
                Console.WriteLine("\nShutdown requested...");
            };

            using IHost host = Host.CreateDefaultBuilder(args)
                .ConfigureLogging(logging => logging.ClearProviders())
                .ConfigureServices((hostContext, services) =>
                {
                    var appSettings = hostContext.Configuration.GetSection("AppSettings").Get<AppSettings>()
                        ?? throw new ApplicationException("appsettings.json 파일에서 AppSettings 섹션을 찾을 수 없거나 비어있습니다.");
                    services.AddSingleton(appSettings);

                    services.AddSingleton<CustomLogger, Log4Net>();
                    services.AddSingleton<Encryption>();
                    services.AddSingleton(provider => ProgressTracker.Instance);
                    services.AddSingleton<SettingsManager>();
                    services.AddSingleton<PlaywrightAdCollector>();
                })
                .Build();

            var logger = host.Services.GetRequiredService<CustomLogger>();
            var settings = host.Services.GetRequiredService<AppSettings>();
            var encryption = host.Services.GetRequiredService<Encryption>();
            var progress = host.Services.GetRequiredService<ProgressTracker>();
            var settingsManager = host.Services.GetRequiredService<SettingsManager>();

            settingsManager.ValidateAndUpdateSettings();

            progress.Initialize(settings.TargetUrls ?? Array.Empty<string>());

            // 리소스 수명 관리
            IDisposable? disposableResource = null;             // Selenium BrowserPool 등 동기 자원
            IAsyncDisposable? asyncResourceA = null;            // Playwright BrowserPool 등 비동기 자원(1)
            IPlaywright? playwrightRuntime = null;             // Playwright Runtime (IAsyncDisposable)

            try
            {
                Console.WriteLine("계정 유효성 검증 및 브라우저 서비스 초기화 중...");

                Func<Task> runnerTaskFactory;

                if (settings.BrowserType.Equals("Playwright", StringComparison.OrdinalIgnoreCase))
                {
                    var playwright = await Playwright.CreateAsync();
                    var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
                    var playwrightPool = new PlaywrightBrowserPool(browser, settings, logger, encryption);
                    await playwrightPool.InitializePoolAsync(cts.Token);

                    asyncResourceA = playwrightPool;
                    playwrightRuntime = playwright;

                    runnerTaskFactory = () => {
                        var adCollector = host.Services.GetRequiredService<PlaywrightAdCollector>();
                        var adClicker = new PlaywrightAdClicker(settings, logger, playwrightPool);
                        var runner = new PlaywrightPipelineRunner(settings, logger, playwrightPool, progress, adCollector, adClicker);
                        return runner.RunAsync(settings.TargetUrls ?? Array.Empty<string>(), cts.Token);
                    };
                }
                else // Default to Selenium
                {
                    var browserPool = new BrowserPool(settings, logger, encryption);
                    await browserPool.InitializePoolAsync(cts.Token);
                    disposableResource = browserPool;

                    runnerTaskFactory = () => {
                        var runner = new SeleniumPipelineRunner(settings, logger, browserPool, progress);
                        return runner.RunAsync(settings.TargetUrls ?? Array.Empty<string>(), cts.Token);
                    };
                }
                
                Console.WriteLine("초기화 성공. 프로세스를 시작합니다...");
                Thread.Sleep(1000); // Optional: give user time to read the message

                Console.CursorVisible = false;
                Console.Clear();
                var progressTask = Task.Run(() => progress.PrintProgress(), CancellationToken.None);

                // Run the selected pipeline
                await runnerTaskFactory();

                // 모든 작업 완료 후 진행 상황 출력 중지
                progress.StopProgress();
                await progressTask; // 진행 상황 출력 태스크가 완료될 때까지 대기

                logger.Info("모든 작업이 완료되었습니다.");
            }
            catch (OperationCanceledException)
            {
                logger.Warn("사용자에 의해 작업이 취소되었습니다.");
            }
            catch (ApplicationException ex)
            {
                // 예측된 종료 상황
                Console.WriteLine(ex.Message);
                logger.Warn(ex.Message);
            }
            catch (Exception ex)
            {
                // 그 외 모든 예외 처리
                var errorMessage = "예기치 못한 오류가 발생했습니다. 자세한 내용은 로그를 확인하세요.";
                Console.WriteLine(errorMessage);
                logger.Error(errorMessage, ex);
            }
            finally
            {
                progress.StopProgress();

                Console.WriteLine(); // Ensure we start on a new line.
                Console.CursorVisible = true;

                // 리소스 정리
                // Playwright의 경우: Pool -> Runtime 순서로 DisposeAsync
                if (asyncResourceA != null)
                    await asyncResourceA.DisposeAsync();
                if (playwrightRuntime is IAsyncDisposable asyncDisp)
                    await asyncDisp.DisposeAsync();
                else if (playwrightRuntime is IDisposable disp)
                    disp.Dispose();
                if (disposableResource != null)
                    disposableResource.Dispose();

                await host.StopAsync();

                stopwatch.Stop();
                int minutes = (int)stopwatch.Elapsed.TotalMinutes;
                int seconds = stopwatch.Elapsed.Seconds;

                Console.WriteLine($"총 실행 시간: {minutes}분 {seconds}초");
                Console.WriteLine("작업 완료. 리소스 정리 중...");

                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

                Console.WriteLine("정리 완료. 아무 키나 눌러 종료합니다.");
                Console.ReadKey();
            }
        }
    }
}
