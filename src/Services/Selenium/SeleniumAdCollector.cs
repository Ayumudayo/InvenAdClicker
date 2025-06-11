using InvenAdClicker.Config;
using InvenAdClicker.Services.Interfaces;
using InvenAdClicker.Services.Selenium;
using InvenAdClicker.Utils;
using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using System.Collections.Concurrent;
using System.Security.Policy;
using System.Threading.Channels;

public class SeleniumAdCollector : IAdCollector
{
    private readonly AppSettings _settings;
    private readonly ILogger _logger;
    private readonly BrowserPool _browserPool;
    private readonly ProgressTracker _progress;

    public SeleniumAdCollector(AppSettings settings, ILogger logger,
        BrowserPool browserPool, ProgressTracker progress)
    {
        _settings = settings;
        _logger = logger;
        _browserPool = browserPool;
        _progress = progress;
    }

    public async Task<Dictionary<string, IEnumerable<string>>> CollectAsync(
        string[] urls, CancellationToken cancellationToken = default)
    {
        var result = new ConcurrentDictionary<string, IEnumerable<string>>();
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleWriter = true, SingleReader = false });

        // URL 傍鞭
        _ = Task.Run(async () =>
        {
            foreach (var url in urls)
            {
                await channel.Writer.WriteAsync(url, cancellationToken);
                _progress.Update(url, ProgressStatus.Waiting, iterDelta: 1);
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
                _logger.Info($"CollectorWorker {workerId} started");

                try
                {
                    await foreach (var url in channel.Reader.ReadAllAsync(cancellationToken))
                    {
                        _progress.Update(url, ProgressStatus.Collecting, threadDelta: 1);
                        try
                        {
                            var links = await CollectWithBrowserAsync(browser, url, cancellationToken);
                            result[url] = links;

                            bool collectedCount = links.Count > 0;
                            _progress.Update(url, collectedCount ? ProgressStatus.Collected : ProgressStatus.NoAds, adsDelta: links.Count(), threadDelta: -1);
                            _logger.Info($"[Collector{workerId}] {url} => {links.Count} links");
                        }
                        catch (Exception ex)
                        {
                            _logger.Error($"[Collector{workerId}] Failed: {url}", ex);
                            _progress.Update(url, ProgressStatus.Error, errDelta: 1, threadDelta: -1);
                        }
                    }
                }
                finally
                {
                    _browserPool.Release(browser);
                    _logger.Info($"CollectorWorker {workerId} stopped");
                }
            }, cancellationToken);
        }

        await Task.WhenAll(workers);
        return result.ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    private async Task<List<string>> CollectWithBrowserAsync(
        SeleniumWebBrowser browser, string url, CancellationToken cancellationToken)
    {
        var driver = browser.Driver;
        driver.Navigate().GoToUrl(url);
        WaitForPageLoad(driver, TimeSpan.FromMilliseconds(_settings.PageLoadTimeoutMilliseconds));

        var links = new List<string>();
        foreach (var iframe in driver.FindElements(By.TagName("iframe")))
        {
            try
            {
                driver.SwitchTo().Frame(iframe);
                links.AddRange(driver.FindElements(By.TagName("a"))
                    .Select(e => e.GetAttribute("href"))
                    .Where(h => !string.IsNullOrEmpty(h)));
            }
            catch (Exception ex)
            {
                _logger.Warn($"[Collector] iframe fail {url}: {ex.Message}");
            }
            finally { driver.SwitchTo().DefaultContent(); }
        }

        return links;
    }

    private void WaitForPageLoad(IWebDriver driver, TimeSpan timeout)
        => new WebDriverWait(driver, timeout)
            .Until(d => ((IJavaScriptExecutor)d)
                .ExecuteScript("return document.readyState").Equals("complete"));
}