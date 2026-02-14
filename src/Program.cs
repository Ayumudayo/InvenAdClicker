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
using Microsoft.Playwright;
using InvenAdClicker.Services.Playwright;
using InvenAdClicker.Services.Pipeline;

namespace InvenAdClicker
{
    class Program
    {
        public static async Task Main(string[] args)
        {
            var stopwatch = Stopwatch.StartNew();

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
                Console.WriteLine("\n종료 요청을 감지했습니다. 안전하게 종료합니다...");
            };

            using IHost host = Host.CreateDefaultBuilder(args)
                .ConfigureLogging(logging =>
                {
                    logging.ClearProviders();
                    logging.AddProvider(new RollingFileLoggerProvider());
                    // Remove hardcoded Debug level to respect appsettings.json or default
                })
                .ConfigureServices((hostContext, services) =>
                {
                    var appSettings = hostContext.Configuration.GetSection("AppSettings").Get<AppSettings>()
                        ?? new AppSettings();
                    services.AddSingleton(appSettings);

                    services.AddSingleton<IAppLogger, MsLoggerAdapter>();
                    services.AddSingleton<Encryption>();
                    services.AddSingleton(provider => ProgressTracker.Instance);
                    services.AddSingleton<SettingsManager>();
                })
                .Build();

            var logger = host.Services.GetRequiredService<IAppLogger>();
            var settings = host.Services.GetRequiredService<AppSettings>();
            var encryption = host.Services.GetRequiredService<Encryption>();
            var progress = host.Services.GetRequiredService<ProgressTracker>();
            var settingsManager = host.Services.GetRequiredService<SettingsManager>();

            settingsManager.ValidateAndUpdateSettings();

            progress.Initialize(settings.TargetUrls ?? Array.Empty<string>());

            try
            {
                Console.WriteLine("계정 유효성 검증 및 브라우저 서비스 초기화 중...");

                // 로그인 전 검증: 자격증명이 없거나 로그인 실패 시 이후 단계로 진행하지 않음
                // Playwright 엔진을 기준으로 한 번만 검증한 뒤 풀/파이프라인을 구성
                if (await PlaywrightBootstrap.EnsureInstalledIfMissingAsync(logger))
                {
                    Console.WriteLine();
                    Console.WriteLine("Playwright 런타임을 설치했습니다. 프로그램을 종료한 뒤 다시 시작해 주세요.");
                    Console.WriteLine("아무 키나 누르면 종료합니다...");
                    Console.ReadKey();
                    return;
                }

                using var playwright = await Playwright.CreateAsync();
                var launchOptions = new BrowserTypeLaunchOptions
                {
                    Headless = settings.Debug.Enabled ? settings.Debug.Headless : true
                };
                await using var browser = await playwright.Chromium.LaunchAsync(launchOptions);
                await LoginVerifier.VerifyPlaywrightAsync(browser, settings, logger, encryption, cts.Token);
                await using var playwrightPool = new PlaywrightBrowserPool(browser, settings, logger, encryption);
                await playwrightPool.InitializePoolAsync(cts.Token);

                IAdCollector<IPage> adCollector = new PlaywrightAdCollector(settings, logger);
                IAdClicker<IPage> adClicker = new PlaywrightAdClicker(settings, logger, playwrightPool);
                var runner = new GenericPipelineRunner<IPage>(settings, logger, playwrightPool, progress, adCollector, adClicker);
                
                Console.WriteLine("초기화 성공. 프로세스를 시작합니다...");
                await Task.Delay(1000, cts.Token); // 안내 메시지를 읽을 시간을 잠시 부여

                Console.CursorVisible = false;
                Console.Clear();
                var progressTask = Task.Run(() => progress.PrintProgress(), CancellationToken.None);

                // 선택된 파이프라인 실행
                await runner.RunAsync(settings.TargetUrls ?? Array.Empty<string>(), cts.Token);

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
                logger.Fatal(errorMessage, ex);
            }
            finally
            {
                progress.StopProgress();

                Console.WriteLine(); // 다음 출력을 새 줄에서 시작하도록 보장
                Console.CursorVisible = true;

                await host.StopAsync();

                stopwatch.Stop();
                int minutes = (int)stopwatch.Elapsed.TotalMinutes;
                int seconds = stopwatch.Elapsed.Seconds;

                Console.WriteLine($"총 실행 시간: {minutes}분 {seconds}초");
                Console.WriteLine("작업 완료. 아무 키나 눌러 종료합니다.");
                Console.ReadKey();
            }
        }
    }
}
