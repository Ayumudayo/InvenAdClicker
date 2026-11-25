using Microsoft.Playwright;
using InvenAdClicker.Models;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using InvenAdClicker.Services.Interfaces;
using InvenAdClicker.Utils;

namespace InvenAdClicker.Services.Playwright
{
    public class PlaywrightAdClicker : IAdClicker<IPage>
    {
        private readonly AppSettings _settings;
        private readonly IAppLogger _logger;
        private readonly IBrowserPool<IPage> _browserPool;

        public PlaywrightAdClicker(AppSettings settings, IAppLogger logger, IBrowserPool<IPage> browserPool)
        {
            _settings = settings;
            _logger = logger;
            _browserPool = browserPool;
        }

        public async Task<IPage> ClickAdAsync(IPage page, string link, int clickerId, CancellationToken cancellationToken)
        {
            if (_settings.DryRun)
            {
                _logger.Info($"[DryRun] 클릭 시뮬레이션 (실제 클릭 안 함): {link}");
                await Task.Delay(100, cancellationToken);
                return page;
            }

            if (_settings.MaxClickAttempts <= 0)
            {
                throw new ArgumentException("MaxClickAttempts는 1 이상이어야 합니다.");
            }

            var originalPage = page;
            try
            {
                return await AsyncRetry.ExecuteAsync(async attempt =>
                {
                    try
                    {
                        PlaywrightPageTelemetry.ResetRouteStats(page);
                        var swTotal = Stopwatch.StartNew();
                        var sw = Stopwatch.StartNew();
                        await page.GotoAsync(link, new PageGotoOptions
                        {
                            WaitUntil = WaitUntilState.Commit, // Commit으로 변경: 페이지 로딩 완료 대기 안 함
                            Timeout = _settings.PageLoadTimeoutMilliseconds
                        });
                        var gotoMs = sw.ElapsedMilliseconds;

                        sw.Restart();
                        await Task.Delay(_settings.ClickDelayMilliseconds, cancellationToken);
                        var delayMs = sw.ElapsedMilliseconds;
                        swTotal.Stop();

                        var routeStats = PlaywrightPageTelemetry.SnapshotRouteStats(page);
                        _logger.Info(
                            $"[perf][click] clicker={clickerId} link='{link}' attempt={attempt}/{_settings.MaxClickAttempts} " +
                            $"goto={gotoMs}ms delay={delayMs}ms route={routeStats.FormatCompact()} total={swTotal.ElapsedMilliseconds}ms");
                        return page; // 성공
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn($"Playwright 클릭 {attempt}회차 실패('{link}'): {ex.Message}");
                        if (attempt == _settings.MaxClickAttempts)
                        {
                            _logger.Error($"Playwright 최종 클릭 시도 실패('{link}')");
                        }
                        throw;
                    }
                }, _settings.MaxClickAttempts, TimeSpan.FromMilliseconds(250), _ => true, cancellationToken,
                    beforeRetry: async (_, _) => page = await _browserPool.RenewAsync(page, cancellationToken));
            }
            catch
            {
                // On failure the caller still holds originalPage, so it cannot release a replacement.
                if (!ReferenceEquals(page, originalPage))
                {
                    try { await page.Context.CloseAsync(); } catch { }
                }
                throw;
            }
        }
    }
}
