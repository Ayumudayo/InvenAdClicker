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
            try
            {
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
            catch (Exception ex)
            {
                Logger.Error($"Browser initialization failed: {ex}");
                CleanupBrowserInstances();
                throw;
            }
        }

        private void CleanupBrowserInstances()
        {
            Logger.Info("Cleaning up browser instances.");
            while (_browserInstances.TryDequeue(out var driver))
            {
                try
                {
                    driver.Quit();
                    driver.Dispose();
                    Logger.Info("Browser instance quit and disposed.");
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error while disposing browser instance: {ex}");
                }
            }
        }

        private void StartProgressDisplay()
        {
            Logger.Info("Starting progress display.");
            Task.Run(() => _progressTracker.PrintProgress());
        }

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

                    try
                    {
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
                    catch (OperationCanceledException)
                    {
                        Logger.Info("CollectAdLinksAsync canceled.");
                        break; // 루프 종료
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Unexpected error in CollectAdLinksAsync loop: {ex}");
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
            int maxRetries = 3;
            int retryCount = 0;
            bool success = false;

            while (!success && retryCount < maxRetries)
            {
                IWebDriver driver = null;
                try
                {
                    if (_browserInstances.TryDequeue(out driver))
                    {
                        Logger.Info($"Browser instance dequeued for job URL: {job.Url}");
                        await CollectAdsAsync(driver, job, cancellationToken);
                        success = true;
                        _browserInstances.Enqueue(driver);
                        Logger.Info($"Browser instance enqueued after processing job URL: {job.Url}");
                    }
                    else
                    {
                        driver = CreateNewBrowserInstance();
                        await CollectAdsAsync(driver, job, cancellationToken);
                        success = true;
                        _browserInstances.Enqueue(driver);
                        Logger.Info($"New browser instance created and enqueued for job URL: {job.Url}");
                    }
                }
                catch (WebDriverException ex)
                {
                    Logger.Error($"WebDriverException for job URL: {job.Url}. Exception: {ex}. Removing and retrying.");
                    if (driver != null)
                    {
                        try
                        {
                            driver.Quit();
                            driver.Dispose();
                            Logger.Info($"Browser instance disposed due to WebDriverException for URL: {job.Url}");
                        }
                        catch (Exception disposeEx)
                        {
                            Logger.Error($"Error disposing browser instance: {disposeEx}");
                        }
                    }
                    driver = null; // 인스턴스를 제거했으므로 null로 설정

                    // 새로운 브라우저 인스턴스 생성
                    try
                    {
                        driver = CreateNewBrowserInstance();
                        Logger.Info("New browser instance created after WebDriverException.");
                    }
                    catch (Exception createEx)
                    {
                        Logger.Error($"Failed to create new browser instance after WebDriverException: {createEx}");
                        throw;
                    }

                    retryCount++;
                    if (retryCount >= maxRetries)
                    {
                        Logger.Error($"Max retries reached for job URL: {job.Url}. Skipping job.");
                        _progressTracker.UpdateProgress(job.Url, status: ProgressStatus.Error, incrementError: true);
                        break;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"Exception while processing job URL: {job.Url}. Exception: {ex}. Retrying.");
                    retryCount++;
                    if (retryCount >= maxRetries)
                    {
                        Logger.Error($"Max retries reached for job URL: {job.Url}. Skipping job.");
                        _progressTracker.UpdateProgress(job.Url, status: ProgressStatus.Error, incrementError: true);
                        break;
                    }
                }
            }
        }

        private async Task CollectAdsAsync(IWebDriver driver, Job job, CancellationToken cancellationToken)
        {
            _progressTracker.UpdateProgress(job.Url, status: ProgressStatus.Collecting, threadCountChange: 1);
            Logger.Info($"CollectAdsAsync started for URL: {job.Url}");

            try
            {
                if (!_collectedAdLinks.ContainsKey(job.Url))
                {
                    _collectedAdLinks.TryAdd(job.Url, new List<string>());
                    Logger.Info($"Initialized ad links list for URL: {job.Url}");
                }

                for (int iteration = 0; iteration < job.Iteration; iteration++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // 페이지 로드 타임아웃 설정
                    driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(5);

                    bool navigationSuccess = false;
                    int navigationRetryCount = 0;
                    int maxNavigationRetries = 2; // 최대 재시도 횟수
                    TimeSpan retryDelay = TimeSpan.FromSeconds(3); // 재시도 간 대기 시간

                    while (!navigationSuccess && navigationRetryCount < maxNavigationRetries)
                    {
                        try
                        {
                            driver.Navigate().GoToUrl(job.Url);
                            Logger.Info($"Navigated to {job.Url} (Iteration {iteration + 1}/{job.Iteration})");
                            navigationSuccess = true; // 성공 시 루프 종료
                        }
                        catch (WebDriverTimeoutException ex)
                        {
                            navigationRetryCount++;
                            Logger.Error($"Page load timeout for URL: {job.Url} (Iteration {iteration + 1}/{job.Iteration}). Attempt {navigationRetryCount}/{maxNavigationRetries}. Exception: {ex}");

                            if (navigationRetryCount < maxNavigationRetries)
                            {
                                Logger.Info($"Retrying navigation to {job.Url} after {retryDelay.TotalSeconds} seconds...");
                                await Task.Delay(retryDelay, cancellationToken);
                            }
                            else
                            {
                                Logger.Error($"Max navigation retries reached for URL: {job.Url} (Iteration {iteration + 1}/{job.Iteration}). Skipping iteration.");
                                _progressTracker.UpdateProgress(job.Url, status: ProgressStatus.Error, incrementError: true);
                                throw; // 재시도 실패 시 예외를 상위로 전달
                            }
                        }
                    }

                    if (!navigationSuccess)
                    {
                        // 모든 재시도가 실패한 경우 다음 이터레이션으로 넘어감
                        continue;
                    }

                    // document.readyState 외에 특정 요소가 로드되었는지 확인
                    WebDriverWait wait = new WebDriverWait(driver, TimeSpan.FromSeconds(10));
                    try
                    {
                        wait.Until(d => ((IJavaScriptExecutor)d).ExecuteScript("return document.readyState").Equals("complete"));
                        Logger.Info($"Page load complete for {job.Url} (Iteration {iteration + 1}/{job.Iteration})");
                    }
                    catch (WebDriverTimeoutException ex)
                    {
                        Logger.Error($"ReadyState 'complete' timeout for URL: {job.Url} (Iteration {iteration + 1}/{job.Iteration}). Exception: {ex}");
                        throw;
                    }

                    // 광고 iframe 기다리기
                    ReadOnlyCollection<IWebElement> iframes = null;
                    try
                    {
                        iframes = WaitForIframes(driver);
                        Logger.Info($"Found {iframes.Count} ad iframes in {job.Url} (Iteration {iteration + 1}/{job.Iteration})");
                    }
                    catch (WebDriverTimeoutException ex)
                    {
                        Logger.Error($"No ad iframes found in {job.Url} (Iteration {iteration + 1}/{job.Iteration}). Exception: {ex}");
                        throw;
                    }

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
                                int newAds = 0;
                                foreach (var link in links)
                                {
                                    if (!_collectedAdLinks[job.Url].Contains(link))
                                    {
                                        _collectedAdLinks[job.Url].Add(link);
                                        newAds++;
                                    }
                                }

                                // 새로운 광고 링크 수만큼 TotalAdsCollected 업데이트
                                _progressTracker.UpdateProgress(job.Url, adsCollectedChange: newAds);
                                Logger.Info($"Collected {newAds} new ad links from {job.Url} (Iteration {iteration + 1}/{job.Iteration})");
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
                    Logger.Info($"Completed iteration {iteration + 1}/{job.Iteration} for URL: {job.Url}");
                    await Task.Delay(_appSettings.IterationInterval, cancellationToken);
                }

                // 수집 완료 후 상태 업데이트
                _progressTracker.UpdateProgress(job.Url, status: ProgressStatus.Collected);
                Logger.Info($"Completed collecting ads for {job.Url}");
            }
            catch (OperationCanceledException)
            {
                Logger.Info($"Collection canceled for URL: {job.Url}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error collecting ads from URL: {job.Url}. Exception: {ex}");
                _progressTracker.UpdateProgress(job.Url, status: ProgressStatus.Error, incrementError: true);
                throw; // 예외를 상위로 전달하여 재시도 로직이 작동하도록 함
            }
            finally
            {
                _progressTracker.UpdateProgress(job.Url, threadCountChange: -1);
                Logger.Info($"CollectAdsAsync ended for URL: {job.Url}");
            }
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

                    // 클릭 시작 시 상태를 Clicking으로 설정하고 PendingClicks를 설정
                    _progressTracker.UpdateProgress(url, status: ProgressStatus.Clicking, pendingClicksChange: adLinks.Count);
                    Logger.Info($"Starting clicking phase for URL: {url} with {adLinks.Count} ad links.");

                    foreach (var adLink in adLinks)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        await _maxDegreeOfParallelism.WaitAsync(cancellationToken);
                        Logger.Info($"Semaphore acquired for clicking ad: {adLink}");

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
                                _maxDegreeOfParallelism.Release();
                                Logger.Info($"Semaphore released after clicking ad: {adLink}");
                            }
                        }, cancellationToken));
                    }

                    // 상태 업데이트는 클릭이 모두 완료된 후에 수행할 수 있습니다.
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
            int maxRetries = 3;
            int retryCount = 0;
            bool success = false;

            while (!success && retryCount < maxRetries)
            {
                IWebDriver driver = null;
                try
                {
                    if (_browserInstances.TryDequeue(out driver))
                    {
                        Logger.Info($"Browser instance dequeued for clicking ad: {adLink}");
                        await ClickSingleAdAsync(driver, adLink, originalUrl, cancellationToken);
                        success = true;
                        _browserInstances.Enqueue(driver);
                        Logger.Info($"Browser instance enqueued after clicking ad: {adLink}");
                    }
                    else
                    {
                        driver = CreateNewBrowserInstance();
                        await ClickSingleAdAsync(driver, adLink, originalUrl, cancellationToken);
                        success = true;
                        _browserInstances.Enqueue(driver);
                        Logger.Info($"New browser instance created and enqueued for clicking ad: {adLink}");
                    }
                }
                catch (WebDriverException ex)
                {
                    Logger.Error($"WebDriverException while clicking ad: {adLink}. Exception: {ex}. Removing and retrying.");
                    if (driver != null)
                    {
                        try
                        {
                            driver.Quit();
                            driver.Dispose();
                            Logger.Info($"Browser instance disposed due to WebDriverException for ad: {adLink}");
                        }
                        catch (Exception disposeEx)
                        {
                            Logger.Error($"Error disposing browser instance: {disposeEx}");
                        }
                    }
                    driver = null; // 인스턴스를 제거했으므로 null로 설정

                    // 새로운 브라우저 인스턴스 생성
                    try
                    {
                        driver = CreateNewBrowserInstance();
                        Logger.Info("New browser instance created after WebDriverException.");
                    }
                    catch (Exception createEx)
                    {
                        Logger.Error($"Failed to create new browser instance after WebDriverException: {createEx}");
                        throw;
                    }

                    retryCount++;
                    if (retryCount >= maxRetries)
                    {
                        Logger.Error($"Max retries reached for ad: {adLink}. Skipping ad.");
                        _progressTracker.UpdateProgress(originalUrl, incrementError: true);
                        break;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"Exception while clicking ad: {adLink}. Exception: {ex}. Retrying.");
                    retryCount++;
                    if (retryCount >= maxRetries)
                    {
                        Logger.Error($"Max retries reached for ad: {adLink}. Skipping ad.");
                        _progressTracker.UpdateProgress(originalUrl, incrementError: true);
                        break;
                    }
                }
            }
        }

        private async Task ClickSingleAdAsync(IWebDriver driver, string adLink, string originalUrl, CancellationToken cancellationToken)
        {
            try
            {
                driver.Navigate().GoToUrl(adLink);
                Logger.Info($"Navigated to ad URL: {adLink}");

                // 페이지 로드 타임아웃 설정 (필요 시 활성화)
                // driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(5);

                // WebDriverWait wait = new WebDriverWait(driver, TimeSpan.FromSeconds(5));
                // wait.Until(d => ((IJavaScriptExecutor)d).ExecuteScript("return document.readyState").Equals("complete"));

                await Task.Delay(_appSettings.ClickAdInterval, cancellationToken); // 광고가 등록되도록 대기
                Logger.Info($"Waited for {_appSettings.ClickAdInterval} after navigating to ad URL: {adLink}");

                // 광고 클릭 수 증가
                _progressTracker.UpdateProgress(originalUrl, adsClickedChange: 1);
                Logger.Info($"Ads clicked updated for URL: {originalUrl}. Incremented by 1.");

                // PendingClicks 감소
                _progressTracker.UpdateProgress(originalUrl, pendingClicksChange: -1);
                Logger.Info($"Pending clicks updated for URL: {originalUrl}. Decremented by 1.");
            }
            catch (OperationCanceledException)
            {
                Logger.Info($"Clicking canceled for ad: {adLink}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error clicking ad {adLink}: {ex}");
                _progressTracker.UpdateProgress(originalUrl, incrementError: true);

                // 예외 발생 시에도 PendingClicks 감소
                _progressTracker.UpdateProgress(originalUrl, pendingClicksChange: -1);

                throw; // 예외를 상위로 전달하여 재시도 로직이 작동하도록 함
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
    }
}
