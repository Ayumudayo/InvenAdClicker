using Microsoft.Playwright;
using InvenAdClicker.Models;
using System;
using System.Threading;
using System.Threading.Tasks;
using InvenAdClicker.Services.Interfaces;

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

        public async Task<IPage> ClickAdAsync(IPage page, string link, CancellationToken cancellationToken)
        {
            if (_settings.DryRun)
            {
                _logger.Info($"[DryRun] 클릭 시뮬레이션 (실제 클릭 안 함): {link}");
                await Task.Delay(100, cancellationToken);
                return page;
            }

            for (int i = 0; i < _settings.MaxClickAttempts; i++)
            {
                try
                {
                    await page.GotoAsync(link, new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.Commit, // Commit으로 변경: 페이지 로딩 완료 대기 안 함
                        Timeout = _settings.PageLoadTimeoutMilliseconds
                    });
                    await Task.Delay(_settings.ClickDelayMilliseconds, cancellationToken);
                    return page; // 성공
                }
                catch (Exception ex)
                {
                    _logger.Warn($"Playwright 클릭 {i + 1}회차 실패('{link}'): {ex.Message}");
                    if (i < _settings.MaxClickAttempts - 1)
                    {
                        page = await _browserPool.RenewAsync(page, cancellationToken);
                    }
                    else
                    {
                        _logger.Error($"Playwright 최종 클릭 시도 실패('{link}')");
                        throw;
                    }
                }
            }
            return page; // 여까지 오면 큰일남
        }
    }
}
