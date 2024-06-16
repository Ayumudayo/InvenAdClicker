using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InvenAdClicker.@struct;
using InvenAdClicker.helper;
using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using SeleniumExtras.WaitHelpers;
using System.Text.RegularExpressions;

namespace InvenAdClicker.processing
{
    public class UrlProcessor
    {
        private readonly AppSettings _appSettings;
        private readonly JobManager _jobManager;
        private readonly ProgressTracker _progressTracker;
#if DEUBG
        private ConcurrentDictionary<string, HashSet<string>> _adLinkCache = new ConcurrentDictionary<string, HashSet<string>>();
#endif
        private readonly SemaphoreSlim _maxDegreeOfParallelism;
        private readonly ConcurrentQueue<IWebDriver> _browserInstances = new ConcurrentQueue<IWebDriver>();

        public UrlProcessor()
        {
            _appSettings = SettingsManager.LoadAppSettings();
            _jobManager = new JobManager();
            _progressTracker = ProgressTracker.Instance;
            _maxDegreeOfParallelism = new SemaphoreSlim(_appSettings.MaxWorker);
            Logger.Info($"UrlProcessor Initialized. Iteration: {_appSettings.MaxIter} / Set: {_appSettings.MaxSet} / Worker: {_appSettings.MaxWorker}");
        }

        public async Task StartProcessing(CancellationToken cancellationToken)
        {
            InitializeBrowserInstances();
            StartProgressDisplay();
            await ProcessUrlsAsync(cancellationToken);
            CleanupBrowserInstances();
        }

        private void InitializeBrowserInstances()
        {
            for (int i = 0; i < _appSettings.MaxWorker; i++)
            {
                var webDriverService = new WebDriverService();
                if (webDriverService.SetupAndLogin(out IWebDriver driver, CancellationToken.None))
                {
                    _browserInstances.Enqueue(driver);
                }
                else
                {
                    throw new Exception("Failed to initialize all browser instances.");
                }
            }
        }

        private void CleanupBrowserInstances()
        {
            while (_browserInstances.TryDequeue(out var driver))
            {
                driver.Quit();
                driver.Dispose();
            }
        }

        private void StartProgressDisplay() => Task.Run(() => _progressTracker.PrintProgress());

        private async Task ProcessUrlsAsync(CancellationToken cancellationToken)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            List<Task> tasks = new List<Task>();

            while (!_jobManager.IsEmpty() || tasks.Any(t => !t.IsCompleted))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    if (_jobManager.Dequeue(out Job job))
                    {
                        await _maxDegreeOfParallelism.WaitAsync(cancellationToken);

                        tasks.Add(Task.Run(async () =>
                        {
                            try
                            {
                                if (_browserInstances.TryDequeue(out var driver))
                                {
                                    await ProcessSingleUrlAsync(driver, job, cancellationToken);
                                    _browserInstances.Enqueue(driver);
                                }
                                else
                                {
                                    _jobManager.AppendJob(job.Url, job.Iteration);
                                }
                            }
                            finally
                            {
                                _maxDegreeOfParallelism.Release();
                            }
                        }, cancellationToken));
                    }
                    else
                    {
                        tasks.RemoveAll(t => t.IsCompleted);
                        await Task.Delay(100, cancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    Logger.Info("ProcessUrlsAsync canceled.");
                }
            }

            await Task.WhenAll(tasks);
            PrintCompletionLog(stopwatch);
        }

        private async Task ProcessSingleUrlAsync(IWebDriver driver, Job job, CancellationToken cancellationToken)
        {
            _progressTracker.UpdateProgress(job.Url, false, false, 1);

            try
            {
                await Task.Run(() => ProcessJob(driver, job, cancellationToken), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Logger.Info($"ProcessSingleUrlAsync canceled. {job.Url}");
            }
            catch (Exception ex)
            {
                Logger.Error($"ProcessSingleUrlAsync Error: {ex}");
            }
            finally
            {
                _progressTracker.UpdateProgress(job.Url, false, false, -1);
            }

            Logger.Debug($"WebDriverService Disposed | {job.Url} | {job.Iteration}");
        }

        private void ProcessJob(IWebDriver driver, Job job, CancellationToken cancellationToken)
        {
            string originalWindow = driver.CurrentWindowHandle;
            driver.Navigate().GoToUrl(job.Url);
            WebDriverWait wait = new WebDriverWait(driver, TimeSpan.FromSeconds(20));
            wait.Until(d => ((IJavaScriptExecutor)d).ExecuteScript("return document.readyState").Equals("complete"));

            for (int iteration = 0; iteration < job.Iteration; iteration++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!TryProcessIteration(driver, job.Url, originalWindow, cancellationToken))
                {
                    int remaining = job.Iteration - iteration - 1;
                    _jobManager.AppendJob(job.Url, remaining);
                    Logger.Info($"Remaining Job appended. {job.Url} | Remaining: {remaining}");
                    return;
                }
            }
        }

        private bool TryProcessIteration(IWebDriver driver, string url, string originalWindow, CancellationToken cancellationToken)
        {
            try
            {
                FindAndClickAds(driver, url, cancellationToken);
                Thread.Sleep(_appSettings.IterationInterval); // IterationInterval
                CloseTabs(driver, originalWindow);

                _progressTracker.UpdateProgress(url, true);
                driver.Navigate().Refresh();
                return true;
            }
            catch (OperationCanceledException)
            {
                Logger.Info($"Iteration canceled. {url}");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Error($"Iteration Error: {ex}");
                return false;
            }
        }

        private void FindAndClickAds(IWebDriver driver, string url, CancellationToken cancellationToken)
        {
            var iframes = WaitForIframes(driver);
            foreach (var iframe in iframes)
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string iframeId = iframe.GetAttribute("id");
#if DEBUG
                    if (_adLinkCache.TryGetValue(iframeId, out HashSet<string> clickedAdLinks))
                    {
                        TryClickAdsInIframe(driver, iframe, url, clickedAdLinks);
                    }
                    else
                    {
                        clickedAdLinks = new HashSet<string>();
                        if (_adLinkCache.TryAdd(iframeId, clickedAdLinks))
                        {
                            TryClickAdsInIframe(driver, iframe, url, clickedAdLinks);
                        }
                    }
#endif
                    TryClickAdsInIframe(driver, iframe, url);
                }
                catch (OperationCanceledException)
                {
                    Logger.Info($"FindAndClickAds canceled. {url}");
                    return;
                }
            }
        }

        private ReadOnlyCollection<IWebElement> WaitForIframes(IWebDriver driver)
        {
            return new WebDriverWait(driver, TimeSpan.FromSeconds(20))
                .Until(ExpectedConditions.PresenceOfAllElementsLocatedBy(By.CssSelector("iframe[id^='comAd']")));
        }

        private void TryClickAdsInIframe(IWebDriver driver, IWebElement iframe, string url)
        {
#if DEBUG
            string iframeId = iframe.GetAttribute("id");
            if (_adLinkCache.TryGetValue(iframeId, out HashSet<string> clickedAdLinks))
            {
                ProcessAdsInIframe(driver, iframe, url, clickedAdLinks);
            }
            else
            {
                clickedAdLinks = new HashSet<string>();
                if (_adLinkCache.TryAdd(iframeId, clickedAdLinks))
                {
                    ProcessAdsInIframe(driver, iframe, url, clickedAdLinks);
                }
            }
#else
            ProcessAdsInIframe(driver, iframe, url);
#endif
        }

        private void ProcessAdsInIframe(IWebDriver driver, IWebElement iframe, string url, HashSet<string> clickedAdLinks = null)
        {
            try
            {
                driver.SwitchTo().Frame(iframe);
                if (clickedAdLinks != null)
                {
                    ClickAds(driver, clickedAdLinks);
                }
                else
                {
                    ClickAds(driver);
                }
                driver.SwitchTo().DefaultContent();
                Thread.Sleep(_appSettings.ClickIframeInterval);
            }
            catch (Exception e)
            {
                Logger.Error($"Error clicking ads: {url}\n{e}");
                driver.SwitchTo().DefaultContent();
                Thread.Sleep(200);
            }
        }

        private void ClickAds(IWebDriver driver, HashSet<string> clickedAdLinks)
        {
            var adLinks = driver.FindElements(By.TagName("a"))
                .Where(link => link.GetAttribute("href") != null);

            foreach (var link in adLinks)
            {
                string href = link.GetAttribute("href");
                string pattern = @"/(?:x\d+|Top\d+)/.*";
                var match = Regex.Match(href, pattern);

                if (clickedAdLinks.Add(match.Value))
                {
                    ((IJavaScriptExecutor)driver).ExecuteScript($"window.open('{href}');");
                    Logger.Debug($"Clicked '{match.Value}' : {href}");
                }
                else
                {
                    Logger.Debug($"Click Passed : {href}");
                }
            }
        }

        private void ClickAds(IWebDriver driver)
        {
            var adLinks = driver.FindElements(By.TagName("a"))
                .Where(link => link.GetAttribute("href") != null);

            foreach (var link in adLinks)
            {
                string href = link.GetAttribute("href");
                string pattern = @"/(?:x\d+|Top\d+)/.*";
                var match = Regex.Match(href, pattern);

                ((IJavaScriptExecutor)driver).ExecuteScript($"window.open('{href}');");
                Logger.Debug($"Clicked '{match.Value}' : {href}");
            }
        }

        private void CloseTabs(IWebDriver driver, string originalWindow)
        {
            var windowHandles = driver.WindowHandles;
            foreach (var handle in windowHandles)
            {
                if (handle != originalWindow)
                {
                    driver.SwitchTo().Window(handle).Close();
                }
            }
            driver.SwitchTo().Window(originalWindow);
        }

        private void PrintCompletionLog(Stopwatch stopwatch)
        {
            stopwatch.Stop();
            TimeSpan elapsed = stopwatch.Elapsed;
            Console.WriteLine($"All tasks are completed.\nRunTime: {elapsed.Minutes}min {elapsed.Seconds}.{elapsed.Milliseconds}sec");
            Logger.Info($"All tasks are completed. RunTime: {elapsed.Minutes}min {elapsed.Seconds}.{elapsed.Milliseconds}sec");
        }
    }
}