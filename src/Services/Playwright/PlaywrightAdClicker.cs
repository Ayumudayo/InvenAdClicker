using Microsoft.Playwright;
using InvenAdClicker.Models;
using InvenAdClicker.Utils;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace InvenAdClicker.Services.Playwright
{
    public class PlaywrightAdClicker
    {
        private readonly AppSettings _settings;
        private readonly ILogger _logger;
        private readonly PlaywrightBrowserPool _browserPool;

        public PlaywrightAdClicker(AppSettings settings, ILogger logger, PlaywrightBrowserPool browserPool)
        {
            _settings = settings;
            _logger = logger;
            _browserPool = browserPool;
        }

        public async Task<IPage> ClickAdAsync(IPage page, string link, CancellationToken cancellationToken)
        {
            for (int i = 0; i < _settings.MaxClickAttempts; i++)
            {
                try
                {
                    await page.GotoAsync(link, new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.Load,
                        Timeout = _settings.PageLoadTimeoutMilliseconds
                    });
                    await Task.Delay(_settings.ClickDelayMilliseconds, cancellationToken);
                    return page; // Success
                }
                catch (Exception ex)
                {
                    _logger.Warn($"Playwright click attempt {i + 1} for '{link}' failed: {ex.Message}");
                    if (i < _settings.MaxClickAttempts - 1)
                    {
                        page = await _browserPool.RenewAsync(page, cancellationToken);
                    }
                    else
                    {
                        _logger.Error($"Final Playwright click attempt for '{link}' failed.");
                        throw;
                    }
                }
            }
            return page; // Should not be reached
        }
    }
}
