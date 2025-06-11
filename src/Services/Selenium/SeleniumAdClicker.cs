using InvenAdClicker.Config;
using InvenAdClicker.Services.Interfaces;
using InvenAdClicker.Services.Selenium;
using InvenAdClicker.Utils;
using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using System.Threading.Channels;

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

        // 傅农 傍鞭
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

        // 况目 积己
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
                        try
                        {
                            await ClickWithBrowserAsync(browser, page, link, cancellationToken);
                            _progress.Update(page, clickDelta: 1, pendingClicksDelta: -1);
                            _logger.Info($"[Clicker{workerId}] Clicked {link}");
                        }
                        catch (Exception ex)
                        {
                            _logger.Error($"[Clicker{workerId}] Failed click {link}", ex);
                            _progress.Update(page, ProgressStatus.Error, errDelta: 1, pendingClicksDelta: -1);
                        }
                        finally
                        {
                            _progress.Update(page, threadDelta: -1);
                        }
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

    private async Task ClickWithBrowserAsync(
        SeleniumWebBrowser browser,
        string page, string link,
        CancellationToken cancellationToken)
    {
        await RetryHelper.ExecuteWithRetryAsync(async () =>
        {
            var driver = browser.Driver;
            driver.Navigate().GoToUrl(link);
            WaitForPageLoad(driver, TimeSpan.FromMilliseconds(_settings.PageLoadTimeoutMilliseconds));

            var clickable = driver.FindElements(By.TagName("a"))
                .FirstOrDefault(e => e.Displayed && e.Enabled);
            clickable?.Click();
            await Task.Delay(_settings.ClickDelayMilliseconds, cancellationToken);
            return Task.CompletedTask;
        }, _settings.RetryCount, _logger);
    }

    private void WaitForPageLoad(IWebDriver driver, TimeSpan timeout)
        => new WebDriverWait(driver, timeout)
            .Until(d => ((IJavaScriptExecutor)d)
                .ExecuteScript("return document.readyState").Equals("complete"));
}