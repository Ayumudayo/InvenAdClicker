using InvenAdClicker.Models;
using InvenAdClicker.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
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
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly PipelineShutdownOptions _shutdownOptions;

        public AppOrchestrator(
            AppSettings settings,
            IAppLogger logger,
            Encryption encryption,
            ProgressTracker progress,
            SettingsManager settingsManager,
            IServiceScopeFactory scopeFactory,
            PipelineShutdownOptions? shutdownOptions = null)
        {
            _settings = settings;
            _logger = logger;
            _encryption = encryption;
            _progress = progress;
            _settingsManager = settingsManager;
            _scopeFactory = scopeFactory;
            _shutdownOptions = shutdownOptions ?? new PipelineShutdownOptions();
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        public async Task<int> RunAsync(CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();

            try
            {
                _settingsManager.ValidateAndUpdateSettings();
                _progress.Initialize(_settings.TargetUrls ?? Array.Empty<string>());
                Console.WriteLine("계정 유효성 검증 및 브라우저 서비스 초기화 중...");

                if (await PlaywrightBootstrap.EnsureInstalledIfMissingAsync(_logger, cancellationToken))
                {
                    Console.WriteLine();
                    Console.WriteLine("Playwright 런타임을 설치했습니다. 프로그램을 다시 시작해 주세요.");
                    return 0;
                }

                var (result, started) = await ExecuteSessionAsync(cancellationToken);
                ReportResult(result, started);
                return result.ExitCode;
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("사용자에 의해 작업이 취소되었습니다.");
                _logger.Warn("사용자에 의해 작업이 취소되었습니다.");
                return 1;
            }
            catch (SettingsValidationException ex)
            {
                Console.WriteLine(ex.Message);
                _logger.Warn(ex.Message);
                return 1;
            }
            catch (ApplicationException ex)
            {
                Console.WriteLine($"초기화 실패: {ex.Message}");
                _logger.Error("프로그램 초기화에 실패했습니다.", ex);
                return 2;
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
                Console.WriteLine();
                Console.WriteLine($"총 실행 시간: {(int)stopwatch.Elapsed.TotalMinutes}분 {stopwatch.Elapsed.Seconds}초");
                WaitForKeyPressIfInteractive();
            }
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private async Task<(PipelineRunResult Result, bool Started)> ExecuteSessionAsync(CancellationToken cancellationToken)
        {
            var scope = _scopeFactory.CreateAsyncScope();
            using var progressCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task? progressTask = null;
            PlaywrightSession? session = null;
            bool started = false;
            var result = new PipelineRunResult { TotalUrls = _settings.TargetUrls?.Length ?? 0 };
            try
            {
                session = scope.ServiceProvider.GetRequiredService<PlaywrightSession>();
                await session.InitializeAsync(smokeTest: false, cancellationToken);
                await LoginVerifier.VerifyPlaywrightAsync(session.Browser, _settings, _logger, _encryption, cancellationToken);
                var pool = scope.ServiceProvider.GetRequiredService<IBrowserPool<IPage>>();
                await pool.InitializePoolAsync(cancellationToken);
                var runner = scope.ServiceProvider.GetRequiredService<IPipelineRunner>();
                Console.WriteLine("초기화 성공. 프로세스를 시작합니다...");
                await Task.Delay(1000, cancellationToken);
                SafeSetCursorVisible(false);
                SafeClearConsole();
                progressTask = _progress.PrintProgressAsync(progressCts.Token);
                started = true;
                result = await runner.RunAsync(_settings.TargetUrls ?? Array.Empty<string>(), cancellationToken);
            }
            catch (Exception ex)
            {
                result = result with
                {
                    Status = ex is OperationCanceledException && cancellationToken.IsCancellationRequested
                        ? PipelineRunStatus.Cancelled : PipelineRunStatus.FatalFailure,
                    PrimaryException = ex
                };
                foreach (var error in ExceptionCleanup.Read(ex)) result = result.WithCleanupError(error);
            }
            finally
            {
                _progress.StopProgress();
                progressCts.Cancel();
                if (progressTask != null)
                {
                    try { await progressTask; }
                    catch (OperationCanceledException) when (progressCts.IsCancellationRequested) { }
                    catch (Exception ex) { result = result.WithCleanupError(ex); }
                }
                result = await RunCleanup.DisposeAsync(result, () => scope.DisposeAsync(), _shutdownOptions.ResourceDisposalTimeout);
                // A failing scoped disposal may stop DI before it reaches the session.
                // Always terminate our driver transport, including after a cleanup deadline.
                try { session?.Abort(); }
                catch (Exception ex) { result = result.WithCleanupError(ex); }
                SafeSetCursorVisible(true);
            }
            return (result, started);
        }

        private void ReportResult(PipelineRunResult result, bool started)
        {
            string status = result.Status switch
            {
                PipelineRunStatus.Success => "작업이 성공적으로 완료되었습니다.",
                PipelineRunStatus.PartialFailure => "일부 작업이 실패했습니다.",
                PipelineRunStatus.TotalFailure => "시도한 수집 또는 클릭 작업이 모두 실패했습니다.",
                PipelineRunStatus.Cancelled => "사용자에 의해 작업이 취소되었습니다.",
                _ => started ? "실행 또는 종료 처리에 실패했습니다." : "초기화 또는 종료 처리에 실패했습니다."
            };
            string summary = $"수집: 성공 {result.CollectionSucceeded}, 실패 {result.CollectionFailed}, 광고 없음 {result.NoAdsUrls}, 미처리 {result.UnprocessedUrls}\n"
                + $"클릭: 성공 {result.ClickSucceeded}, 실패 {result.ClickFailed}, 시뮬레이션 {result.SimulatedClicks}, 미처리 {result.UnprocessedLinks}";
            Console.WriteLine(status);
            Console.WriteLine(summary);
            if (result.Status == PipelineRunStatus.Success) _logger.Info(status + " " + summary);
            else _logger.Warn(status + " " + summary);
            if (result.PrimaryException != null)
            {
                Console.WriteLine($"원인: {result.PrimaryException.Message}");
                _logger.Error("실행의 최초 오류", result.PrimaryException);
            }
            foreach (var error in result.CleanupErrors)
            {
                Console.WriteLine($"정리 오류: {error.Message}");
                _logger.Error("자원 정리 오류", error);
            }
            if (result.ShutdownTimedOut)
            {
                var message = $"종료 제한 시간을 초과했습니다. 미종료 워커: {result.UnfinishedTaskCount}";
                Console.WriteLine(message);
                _logger.Error(message);
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
