using InvenAdClicker.Config;
using InvenAdClicker.Services.Interfaces;
using InvenAdClicker.Services.Selenium;
using InvenAdClicker.Utils;
using OpenQA.Selenium;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

public class SeleniumAdClicker : IAdClicker
{
    private readonly AppSettings _settings;
    private readonly ILogger _logger;
    private readonly BrowserPool _browserPool;
    private readonly ProgressTracker _progress;

    public SeleniumAdClicker(AppSettings settings, ILogger logger,
        BrowserPool browserPool, ProgressTracker progress)
    {
        _settings = settings;
        _logger = logger;
        _browserPool = browserPool;
        _progress = progress;
    }

    public async Task ClickAsync(
        Dictionary<string, IEnumerable<string>> pageToLinks,
        CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<(string page, string link)>(new UnboundedChannelOptions { SingleWriter = true, SingleReader = false });

        // 링크 공급
        _ = Task.Run(async () =>
        {
            foreach (var (page, links) in pageToLinks)
            {
                _progress.Update(page, pendingClicksDelta: links.Count());
                foreach (var link in links)
                    await channel.Writer.WriteAsync((page, link), cancellationToken);
            }
            channel.Writer.Complete();
        }, cancellationToken);

        // 워커 생성
        var workers = new Task[_settings.MaxDegreeOfParallelism];
        for (int i = 0; i < _settings.MaxDegreeOfParallelism; i++)
        {
            int workerId = i;
            workers[i] = Task.Run(async () =>
            {
                var browser = await _browserPool.AcquireAsync(cancellationToken);
                _logger.Info($"ClickerWorker {workerId} started");

                try
                {
                    await foreach (var (page, link) in channel.Reader.ReadAllAsync(cancellationToken))
                    {
                        _progress.Update(page, ProgressStatus.Clicking, threadDelta: +1);

                        bool success = false;
                        int attempt = 0;

                        while (attempt < _settings.RetryCount && !success)
                        {
                            attempt++;
                            try
                            {
                                await ClickWithBrowserAsync(browser, page, link, cancellationToken);
                                success = true;
                            }
                            catch (Exception ex)
                            {
                                _logger.Warn($"[{workerId}] Attempt {attempt} failed for {link}: {ex.Message}");

                                // 문제 인스턴스 폐기
                                try
                                {
                                    _logger.Info($"[{workerId}] Disposing browser instance due to error");
                                    browser.Dispose();
                                } catch { }

                                // 세마포어 릴리즈
                                _browserPool.Release(browser);

                                // 새 인스턴스 확보
                                browser = await _browserPool.AcquireAsync(cancellationToken);
                            }
                        }

                        if (!success)
                        {
                            // 모두 실패했을 때
                            _logger.Error($"[{workerId}] {_settings.RetryCount} attempts failed for {link}");
                            _progress.Update(page, errDelta: 1);
                        }
                        else
                        {
                            _progress.Update(page, clickDelta: 1);
                        }

                        _progress.Update(page, pendingClicksDelta: -1);
                        _progress.Update(page, threadDelta: -1);
                    }
                }
                finally
                {
                    _browserPool.Release(browser);
                    _logger.Info($"ClickerWorker {workerId} stopped");
                }
            }, cancellationToken);
        }

        await Task.WhenAll(workers);
        _logger.Info("All ad clicking done");
    }

    public async Task ClickWithBrowserAsync(
        SeleniumWebBrowser browser,
        string page,
        string link,
        CancellationToken cancellationToken)
    {
        try
        {
            browser.Driver.Navigate().GoToUrl(link);
            await Task.Delay(_settings.ClickDelayMilliseconds, cancellationToken);
        }
        catch (WebDriverException ex)
        {
            _logger.Error($"WebDriver error during click of '{link}': {ex.Message}");
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error($"Unexpected error during ClickWithBrowserAsync: {ex.Message}");
            throw;
        }
    }
}