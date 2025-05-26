using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using InvenAdClicker.Config;
using InvenAdClicker.Services.Interfaces;
using InvenAdClicker.Services.Selenium;
using InvenAdClicker.Utils;
using CustomLogger = InvenAdClicker.Utils.ILogger;
using Log4Net = InvenAdClicker.Utils.Log4NetLogger;

namespace InvenAdClicker
{
    class InvenAdClicker
    {
        public static async Task Main(string[] args)
        {
            var stopwatch = Stopwatch.StartNew();

            // Ctrl+C 시그널 처리
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };
            var token = cts.Token;

            // Host 빌드
            using IHost host = Host.CreateDefaultBuilder(args)
                .ConfigureLogging(logging =>
                {
                    logging.ClearProviders();
                })
                .ConfigureServices((_, services) =>
                {
                    services.AddSingleton<CustomLogger, Log4Net>();
                    services.AddSingleton<SettingsManager>();
                    services.AddSingleton(provider =>
                        provider.GetRequiredService<SettingsManager>().Settings);

                    services.AddSingleton<Encryption>();
                    services.AddSingleton(provider => ProgressTracker.Instance);

                    services.AddSingleton<IAdCollector, SeleniumAdCollector>();
                    services.AddSingleton<IAdClicker, SeleniumAdClicker>();
                })
                .Build();

            // 주요 인스턴스 획득
            var logger = host.Services.GetRequiredService<CustomLogger>();
            var settings = host.Services.GetRequiredService<AppSettings>();
            var encryption = host.Services.GetRequiredService<Encryption>();
            var progress = host.Services.GetRequiredService<ProgressTracker>();
            var collector = host.Services.GetRequiredService<IAdCollector>();
            var clicker = host.Services.GetRequiredService<IAdClicker>();

            // ProgressTracker 초기화 및 화면 출력
            progress.Initialize(settings.TargetUrls);

            try
            {
                Console.WriteLine("계정 유효성 검증 중...");
                // 로그인 정보 유효성 검증
                encryption.LoadAndValidateCredentials(out var id, out var pw);

                // 로그인 시도: 실제 브라우저로 로그인 확인
                using (var browser = new SeleniumWebBrowser(settings, logger, encryption))
                {
                    // 생성자 내에서 로그인 판정
                }
                Console.WriteLine("계정 유효성 검증 성공");

                Thread.Sleep(1000);
                Task.Run(() => progress.PrintProgress(), CancellationToken.None);

                // 수집 및 클릭 수행
                var pageToLinks = await collector.CollectAsync(settings.TargetUrls, token);
                await clicker.ClickAsync(pageToLinks, token);
                logger.Info("Completed all ad clicks.");
            }
            catch (OperationCanceledException)
            {
                logger.Warn("Operation was canceled by user.");
            }
            catch (Exception ex)
            {
                logger.Error("Fatal error occurred", ex);
            }
            finally
            {
                // Host 중지
                await host.StopAsync();

                // 마무리
                stopwatch.Stop();
                int minutes = (int)stopwatch.Elapsed.TotalMinutes;
                int seconds = stopwatch.Elapsed.Seconds;
                Console.WriteLine($"Total Execution Time: {minutes}Min {seconds}Sec");

                Console.WriteLine("작업이 완료되었습니다. 아무 키나 누르면 프로그램이 종료됩니다.");
                Console.ReadKey();

            }
        }
    }
}
