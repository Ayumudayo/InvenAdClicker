using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InvenAdClicker.Helper;
using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using SeleniumExtras.WaitHelpers;
using InvenAdClicker.@struct;
using InvenAdClicker.helper;
using System.Collections.ObjectModel;

namespace InvenAdClicker.Processing
{
    public class UrlProcessor
    {
        private readonly AppSettings _appSettings;
        private readonly JobManager _jobManager;
        private readonly ProgressTracker _progressTracker;
        private readonly SemaphoreSlim _maxDegreeOfParallelism;
        private readonly ConcurrentQueue<IWebDriver> _browserInstances = new ConcurrentQueue<IWebDriver>();
        private readonly ConcurrentDictionary<string, List<string>> _collectedAdLinks = new ConcurrentDictionary<string, List<string>>();

        public UrlProcessor()
        {
            _appSettings = SettingsManager.LoadAppSettings();
            _jobManager = new JobManager();
            _progressTracker = ProgressTracker.Instance;
            _maxDegreeOfParallelism = new SemaphoreSlim(_appSettings.MaxWorker);
            Logger.Info($"UrlProcessor Initialized. Iteration: {_appSettings.MaxIter} / Set: {_appSettings.MaxSet} / Worker: {_appSettings.MaxWorker}");
        }

        public async Task StartProcessingAsync(CancellationToken cancellationToken)
        {
            Logger.Info("StartProcessingAsync started.");
            try
            {
                InitializeBrowserInstances();
                StartProgressDisplay();

                // 수집 단계
                Logger.Info("Starting collection phase...");
                await CollectAdLinksAsync(cancellationToken);
                Logger.Info("Collection phase completed.");

                // 클릭 단계
                Logger.Info("Starting clicking phase...");
                await ClickAdLinksAsync(cancellationToken);
                Logger.Info("Clicking phase completed.");
            }
            catch (Exception ex)
            {
                Logger.Error($"Unhandled exception in StartProcessingAsync: {ex}");
                throw;
            }
            finally
            {
                CleanupBrowserInstances();
                Logger.Info("Browser instances cleaned up.");
            }
        }

        private void InitializeBrowserInstances()
        {
            Logger.Info("Initializing browser instances.");
            for (int i = 0; i < _appSettings.MaxWorker; i++)
            {
                var webDriverService = new WebDriverService();
                if (webDriverService.SetupAndLogin(out IWebDriver driver, CancellationToken.None))
                {
                    _browserInstances.Enqueue(driver);
                    Logger.Info($"Browser instance {i + 1} initialized and enqueued.");
                }
                else
                {
                    throw new Exception($"Failed to initialize browser instance {i + 1}.");
                }
            }
        }

        private void CleanupBrowserInstances()
        {
            Logger.Info("Cleaning up browser instances.");
            while (_browserInstances.TryDequeue(out var driver))
            {
                DisposeDriver(driver);
            }
        }

        private void DisposeDriver(IWebDriver driver)
        {
            try
            {
                driver.Quit();
                driver.Dispose();
                Logger.Info("Disposed browser instance.");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error disposing browser instance: {ex}");
            }
        }

        private void StartProgressDisplay()
        {
            Logger.Info("Starting progress display.");
            Task.Run(() => _progressTracker.PrintProgress());
        }

        #region Retry 및 헬퍼 함수

        private async Task ExecuteWithRetryAsync(Func<IWebDriver, Task> action, string contextIdentifier, CancellationToken cancellationToken, int maxRetries = 3)
        {
            int retryCount = 0;
            while (retryCount < maxRetries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IWebDriver driver = null;
                try
                {
                    if (!_browserInstances.TryDequeue(out driver))
                        driver = CreateNewBrowserInstance();

                    await action(driver);
                    _browserInstances.Enqueue(driver);
                    return;
                }
                catch (WebDriverException ex)
                {
                    Logger.Error($"WebDriverException in context {contextIdentifier}: {ex}");
                    if (driver != null)
                    {
                        DisposeDriver(driver);
                    }
                    retryCount++;
                    if (retryCount >= maxRetries)
                    {
                        Logger.Error($"Max retries reached in context {contextIdentifier}. Skipping.");
                        _progressTracker.UpdateProgress(contextIdentifier, status: ProgressStatus.Error, incrementError: true);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"Exception in context {contextIdentifier}: {ex}");
                    retryCount++;
                    if (retryCount >= maxRetries)
                    {
                        Logger.Error($"Max retries reached in context {contextIdentifier}. Skipping.");
                        _progressTracker.UpdateProgress(contextIdentifier, status: ProgressStatus.Error, incrementError: true);
                        return;
                    }
                }
            }
        }

        private async Task<bool> NavigateWithRetryAsync(IWebDriver driver, string url, int iteration, int maxNavigationRetries, TimeSpan retryDelay, CancellationToken cancellationToken)
        {
            int navigationRetryCount = 0;
            while (navigationRetryCount < maxNavigationRetries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    driver.Navigate().GoToUrl(url);
                    Logger.Info($"Navigated to {url} (Iteration {iteration})");
                    return true;
                }
                catch (WebDriverTimeoutException ex)
                {
                    navigationRetryCount++;
                    Logger.Error($"Page load timeout for {url} (Iteration {iteration}). Attempt {navigationRetryCount}/{maxNavigationRetries}. Exception: {ex}");
                    if (navigationRetryCount < maxNavigationRetries)
                    {
                        Logger.Info($"Retrying navigation to {url} after {retryDelay.TotalSeconds} seconds...");
                        await Task.Delay(retryDelay, cancellationToken);
                    }
                    else
                    {
                        Logger.Error($"Max navigation retries reached for {url} (Iteration {iteration}).");
                        return false;
                    }
                }
            }
            return false;
        }

        #endregion

        #region 작업 처리 함수
        private async Task CollectAdLinksAsync(CancellationToken cancellationToken)
        {
            Logger.Info("CollectAdLinksAsync started.");
            Stopwatch stopwatch = Stopwatch.StartNew();
            List<Task> tasks = new List<Task>();

            try
            {
                while (!_jobManager.IsEmpty() || tasks.Any(t => !t.IsCompleted))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (_jobManager.Dequeue(out Job job))
                    {
                        await _maxDegreeOfParallelism.WaitAsync(cancellationToken);
                        Logger.Info($"Dequeued job for URL: {job.Url}");

                        tasks.Add(Task.Run(async () =>
                        {
                            try
                            {
                                await ProcessJobWithRetryAsync(job, cancellationToken);
                            }
                            catch (Exception ex)
                            {
                                Logger.Error($"Error processing job for URL: {job.Url}. Exception: {ex}");
                            }
                            finally
                            {
                                _maxDegreeOfParallelism.Release();
                                Logger.Info($"Semaphore released after processing job for URL: {job.Url}");
                            }
                        }, cancellationToken));
                    }
                    else
                    {
                        tasks.RemoveAll(t => t.IsCompleted);
                        await Task.Delay(100, cancellationToken);
                    }
                }

                await Task.WhenAll(tasks);
                stopwatch.Stop();
                Logger.Info($"Collection phase completed in {stopwatch.Elapsed.TotalSeconds} seconds.");
            }
            catch (Exception ex)
            {
                Logger.Error($"Exception in CollectAdLinksAsync: {ex}");
                throw;
            }
        }

        private async Task ProcessJobWithRetryAsync(Job job, CancellationToken cancellationToken)
        {
            await ExecuteWithRetryAsync(async driver =>
            {
                await CollectAdsAsync(driver, job, cancellationToken);
            }, job.Url, cancellationToken);
        }

        private async Task CollectAdsAsync(IWebDriver driver, Job job, CancellationToken cancellationToken)
        {
            _progressTracker.UpdateProgress(job.Url, status: ProgressStatus.Collecting, threadCountChange: 1);
            Logger.Info($"CollectAdsAsync started for URL: {job.Url}");

            if (!_collectedAdLinks.ContainsKey(job.Url))
            {
                _collectedAdLinks.TryAdd(job.Url, new List<string>());
                Logger.Info($"Initialized ad links list for URL: {job.Url}");
            }

            for (int iteration = 1; iteration <= job.Iteration; iteration++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(5);

                bool navigationSuccess = await NavigateWithRetryAsync(driver, job.Url, iteration, maxNavigationRetries: 1, retryDelay: TimeSpan.FromSeconds(3), cancellationToken);
                if (!navigationSuccess)
                {
                    _progressTracker.UpdateProgress(job.Url, status: ProgressStatus.Error, incrementError: true);
                    continue;
                }

                // 페이지 fully loaded 체크
                WebDriverWait wait = new WebDriverWait(driver, TimeSpan.FromSeconds(10));
                try
                {
                    wait.Until(d => ((IJavaScriptExecutor)d).ExecuteScript("return document.readyState").Equals("complete"));
                    Logger.Info($"Page load complete for {job.Url} (Iteration {iteration}/{job.Iteration})");
                }
                catch (WebDriverTimeoutException ex)
                {
                    Logger.Error($"ReadyState 'complete' timeout for {job.Url} (Iteration {iteration}/{job.Iteration}). Exception: {ex}");
                    throw;
                }

                ReadOnlyCollection<IWebElement> iframes = WaitForIframes(driver);
                Logger.Info($"Found {iframes.Count} ad iframes in {job.Url} (Iteration {iteration}/{job.Iteration})");

                foreach (var iframe in iframes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        driver.SwitchTo().Frame(iframe);
                        Logger.Info($"Switched to iframe in {job.Url}");

                        var links = driver.FindElements(By.TagName("a"))
                                    .Where(link => link.GetAttribute("href") != null)
                                    .Select(link => link.GetAttribute("href"))
                                    .ToList();

                        lock (_collectedAdLinks[job.Url])
                        {
                            int newAds = links.Count(link => !_collectedAdLinks[job.Url].Contains(link));
                            foreach (var link in links)
                            {
                                if (!_collectedAdLinks[job.Url].Contains(link))
                                    _collectedAdLinks[job.Url].Add(link);
                            }
                            _progressTracker.UpdateProgress(job.Url, adsCollectedChange: newAds);
                            Logger.Info($"Collected {newAds} new ad links from {job.Url} (Iteration {iteration}/{job.Iteration})");
                        }
                    }
                    catch (NoSuchFrameException ex)
                    {
                        Logger.Error($"Failed to switch to iframe in {job.Url}: {ex}. Current URL: {driver.Url}");
                    }
                    catch (Exception e)
                    {
                        Logger.Error($"Error collecting ads from iframe in {job.Url}: {e}");
                    }
                    finally
                    {
                        driver.SwitchTo().DefaultContent();
                        Logger.Info($"Switched back to default content for {job.Url}");
                    }
                }

                _progressTracker.UpdateProgress(job.Url, incrementIteration: true);
                Logger.Info($"Completed iteration {iteration}/{job.Iteration} for URL: {job.Url}");
                await Task.Delay(_appSettings.IterationInterval, cancellationToken);
            }

            _progressTracker.UpdateProgress(job.Url, status: ProgressStatus.Collected);
            Logger.Info($"Completed collecting ads for {job.Url}");
            _progressTracker.UpdateProgress(job.Url, threadCountChange: -1);
        }

        private async Task ClickAdLinksAsync(CancellationToken cancellationToken)
        {
            Logger.Info("ClickAdLinksAsync started.");
            Stopwatch stopwatch = Stopwatch.StartNew();
            List<Task> tasks = new List<Task>();

            try
            {
                foreach (var kvp in _collectedAdLinks)
                {
                    var url = kvp.Key;
                    var adLinks = kvp.Value.Distinct().ToList();

                    _progressTracker.UpdateProgress(url, status: ProgressStatus.Clicking, pendingClicksChange: adLinks.Count);
                    Logger.Info($"Starting clicking phase for URL: {url} with {adLinks.Count} ad links.");

                    foreach (var adLink in adLinks)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await _maxDegreeOfParallelism.WaitAsync(cancellationToken);
                        Logger.Info($"Semaphore acquired for clicking ad: {adLink}");

                        // 클릭 작업 시작 전 스레드 수 1 증가
                        _progressTracker.UpdateProgress(url, threadCountChange: 1);

                        tasks.Add(Task.Run(async () =>
                        {
                            try
                            {
                                await ClickAdWithRetryAsync(adLink, url, cancellationToken);
                            }
                            catch (Exception ex)
                            {
                                Logger.Error($"Error clicking ad: {adLink}. Exception: {ex}");
                            }
                            finally
                            {
                                // 작업 완료 후 스레드 수 1 감소
                                _progressTracker.UpdateProgress(url, threadCountChange: -1);
                                _maxDegreeOfParallelism.Release();
                                Logger.Info($"Semaphore released after clicking ad: {adLink}");
                            }
                        }, cancellationToken));
                    }
                }

                await Task.WhenAll(tasks);
                stopwatch.Stop();
                Logger.Info($"Clicking phase completed in {stopwatch.Elapsed.TotalSeconds} seconds.");
            }
            catch (Exception ex)
            {
                Logger.Error($"Exception in ClickAdLinksAsync: {ex}");
                throw;
            }
        }

        private async Task ClickAdWithRetryAsync(string adLink, string originalUrl, CancellationToken cancellationToken)
        {
            await ExecuteWithRetryAsync(async driver =>
            {
                await ClickSingleAdAsync(driver, adLink, originalUrl, cancellationToken);
            }, adLink, cancellationToken);
        }

        private async Task ClickSingleAdAsync(IWebDriver driver, string adLink, string originalUrl, CancellationToken cancellationToken)
        {
            try
            {
                driver.Navigate().GoToUrl(adLink);
                Logger.Info($"Navigated to ad URL: {adLink}");
                await Task.Delay(_appSettings.ClickAdInterval, cancellationToken);
                _progressTracker.UpdateProgress(originalUrl, adsClickedChange: 1);
                Logger.Info($"Ads clicked updated for URL: {originalUrl}.");
                _progressTracker.UpdateProgress(originalUrl, pendingClicksChange: -1);
                Logger.Info($"Pending clicks updated for URL: {originalUrl}.");
            }
            catch (OperationCanceledException)
            {
                Logger.Info($"Clicking canceled for ad: {adLink}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error clicking ad {adLink}: {ex}");
                _progressTracker.UpdateProgress(originalUrl, incrementError: true);
                _progressTracker.UpdateProgress(originalUrl, pendingClicksChange: -1);
                throw;
            }
        }

        private IWebDriver CreateNewBrowserInstance()
        {
            Logger.Info("Creating a new browser instance.");
            var webDriverService = new WebDriverService();
            if (webDriverService.SetupAndLogin(out IWebDriver driver, CancellationToken.None))
            {
                Logger.Info("New browser instance created successfully.");
                return driver;
            }
            else
            {
                Logger.Error("Failed to create new browser instance.");
                throw new Exception("Failed to create new browser instance.");
            }
        }

        private ReadOnlyCollection<IWebElement> WaitForIframes(IWebDriver driver)
        {
            Logger.Info("Waiting for ad iframes to load.");
            WebDriverWait wait = new WebDriverWait(driver, TimeSpan.FromSeconds(20));
            return wait.Until(d =>
            {
                var iframes = d.FindElements(By.CssSelector("iframe[id^='comAd']"));
                if (iframes.Count > 0)
                {
                    Logger.Info($"Found {iframes.Count} ad iframes.");
                    return iframes;
                }
                Logger.Warn("No ad iframes found yet.");
                return null;
            });
        }

        #endregion
    }
}
