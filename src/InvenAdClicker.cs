using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using InvenAdClicker.Config;
using InvenAdClicker.Services.Interfaces;
using InvenAdClicker.Utils;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CustomLogger = InvenAdClicker.Utils.ILogger;
using Log4Net = InvenAdClicker.Utils.Log4NetLogger;

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
                .ConfigureServices((_, services) =>
                {
                    services.AddSingleton<CustomLogger, Log4Net>();
                    services.AddSingleton<SettingsManager>();
                    services.AddSingleton(provider =>
                        provider.GetRequiredService<SettingsManager>().Settings);
                    services.AddSingleton<Encryption>();
                    services.AddSingleton(provider => ProgressTracker.Instance);

                    services.AddSingleton<BrowserPool>();

                    services.AddSingleton<IAdCollector, SeleniumAdCollector>();
                    services.AddSingleton<IAdClicker, SeleniumAdClicker>();
                })
                .Build();

            var logger = host.Services.GetRequiredService<CustomLogger>();
            var settings = host.Services.GetRequiredService<AppSettings>();
            var encryption = host.Services.GetRequiredService<Encryption>();
            var progress = host.Services.GetRequiredService<ProgressTracker>();
            var browserPool = host.Services.GetRequiredService<BrowserPool>();
            var collector = host.Services.GetRequiredService<IAdCollector>();
            var clicker = host.Services.GetRequiredService<IAdClicker>();

            progress.Initialize(settings.TargetUrls ?? Array.Empty<string>());

            try
            {
                Console.WriteLine("계정 유효성 검증 중...");
                encryption.LoadAndValidateCredentials(out var id, out var pw);

                // 로그인 테스트용 브라우저 생성
                using (var testBrowser = await browserPool.AcquireAsync(cts.Token))
                {
                    Console.WriteLine("계정 유효성 검증 성공");
                    browserPool.Release(testBrowser);
                }

                Thread.Sleep(1000);

                Console.CursorVisible = false;
                Console.Clear();
                var progressTask = Task.Run(() => progress.PrintProgress(), CancellationToken.None);

                // 병렬 수집 및 클릭 실행
                var pageToLinks = await collector.CollectAsync(settings.TargetUrls ?? Array.Empty<string>(), cts.Token);
                await clicker.ClickAsync(pageToLinks, cts.Token);

                // 모든 작업 완료 후 진행 상황 출력 중지
                progress.StopProgress();
                await progressTask; // 진행 상황 출력 태스크가 완료될 때까지 대기

                logger.Info("All operations completed.");
            }
            catch (OperationCanceledException) // 취소 및 오류 발생 시에도 진행 상황 출력 중지
            {
                logger.Warn("Operation was canceled by user.");
                progress.StopProgress();
            }
            catch (Exception ex)
            {
                logger.Error("Fatal error occurred", ex);
                progress.StopProgress();
            }
            finally
            {
                // 리소스 정리
                browserPool.Dispose();
                await host.StopAsync();

                stopwatch.Stop();
                int minutes = (int)stopwatch.Elapsed.TotalMinutes;
                int seconds = stopwatch.Elapsed.Seconds;

                Console.CursorVisible = true;
                Console.WriteLine($"Total Execution Time: {minutes}Min {seconds}Sec");
                Console.WriteLine("작업 완료. 아무 키나 눌러 종료합니다.");
                Console.ReadKey();
            }
        }
    }
}