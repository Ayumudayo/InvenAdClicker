
using Microsoft.Playwright;
using System.Linq;
using InvenAdClicker.Models;
using InvenAdClicker.Services.Interfaces;
using InvenAdClicker.Services.Playwright;
using InvenAdClicker.Utils;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

public class PlaywrightAdClicker : IAdClicker
{
    private readonly AppSettings _settings;
    private readonly ILogger _logger;
    private readonly PlaywrightWebBrowser _browser;
    private readonly ProgressTracker _progress;

    public PlaywrightAdClicker(AppSettings settings, ILogger logger, PlaywrightWebBrowser browser, ProgressTracker progress)
    {
        _settings = settings;
        _logger = logger;
        _browser = browser;
        _progress = progress;
    }

    public async Task ClickAsync(
        Dictionary<string, IEnumerable<string>> pageToLinks,
        CancellationToken cancellationToken = default)
    {
        _logger.Info("Clicking ads with Playwright...");

        foreach (var (page, links) in pageToLinks)
        {
            _progress.Update(page, pendingClicksDelta: links.Count());
            foreach (var link in links)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.Info("Ad clicking cancelled.");
                    return;
                }

                _progress.Update(page, ProgressStatus.Clicking, threadDelta: +1);
                try
                {
                    await ClickWithBrowserAsync(link, cancellationToken);
                    _logger.Info($"Clicked ad: {link}");
                    _progress.Update(page, clickDelta: 1);
                }
                catch (Exception ex)
                {
                    _logger.Error($"Failed to click ad {link}: {ex.Message}");
                    _progress.Update(page, errDelta: 1);
                }
                finally
                {
                    _progress.Update(page, pendingClicksDelta: -1, threadDelta: -1);
                }
            }
        }
        _logger.Info("All ad clicking done.");
    }

    public async Task ClickWithBrowserAsync(string link, CancellationToken cancellationToken)
    {
        try
        {
            await _browser.Page.GotoAsync(link, new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = _settings.PageLoadTimeoutMilliseconds });
            await Task.Delay(_settings.ClickDelayMilliseconds, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Error($"Playwright error during click of '{link}': {ex.Message}");
            throw;
        }
    }
}
