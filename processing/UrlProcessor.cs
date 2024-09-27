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
            finally
            {
                CleanupBrowserInstances();
            }
        }

        private void InitializeBrowserInstances()
        {
            try
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
            catch (Exception ex)
            {
                Logger.Error($"Browser initialization failed: {ex}");
                CleanupBrowserInstances();
                throw;
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

        private async Task CollectAdLinksAsync(CancellationToken cancellationToken)
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
                            await ProcessJobWithRetryAsync(job, cancellationToken);
                            _maxDegreeOfParallelism.Release();
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
                }
            }

            await Task.WhenAll(tasks);
            stopwatch.Stop();
            Logger.Info($"Collection phase completed in {stopwatch.Elapsed.TotalSeconds} seconds.");
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
                        await CollectAdsAsync(driver, job, cancellationToken);
                        success = true;
                        _browserInstances.Enqueue(driver);
                    }
                    else
                    {
                        driver = CreateNewBrowserInstance();
                        await CollectAdsAsync(driver, job, cancellationToken);
                        success = true;
                        _browserInstances.Enqueue(driver);
                    }
                }
                catch (WebDriverException ex)
                {
                    Logger.Error($"WebDriverException occurred: {ex.Message}. Removing browser instance and retrying.");
                    if (driver != null)
                    {
                        driver.Quit();
                        driver.Dispose();
                    }
                    driver = null; // 인스턴스를 제거했으므로 null로 설정

                    // 새로운 브라우저 인스턴스 생성
                    driver = CreateNewBrowserInstance();

                    retryCount++;
                    if (retryCount >= maxRetries)
                    {
                        Logger.Error($"Max retries reached for {job.Url}. Skipping job.");
                        _progressTracker.UpdateProgress(job.Url, status: ProgressStatus.Error, incrementError: true);
                        break;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"Exception occurred: {ex.Message}. Retrying.");
                    retryCount++;
                    if (retryCount >= maxRetries)
                    {
                        Logger.Error($"Max retries reached for {job.Url}. Skipping job.");
                        _progressTracker.UpdateProgress(job.Url, status: ProgressStatus.Error, incrementError: true);
                        break;
                    }
                }
            }
        }

        private async Task CollectAdsAsync(IWebDriver driver, Job job, CancellationToken cancellationToken)
        {
            _progressTracker.UpdateProgress(job.Url, status: ProgressStatus.Collecting, threadCountChange: 1);

            try
            {
                if (!_collectedAdLinks.ContainsKey(job.Url))
                {
                    _collectedAdLinks.TryAdd(job.Url, new List<string>());
                }

                for (int iteration = 0; iteration < job.Iteration; iteration++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    driver.Navigate().GoToUrl(job.Url);

                    // 페이지 로드 타임아웃 설정
                    driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(120);

                    WebDriverWait wait = new WebDriverWait(driver, TimeSpan.FromSeconds(20));
                    wait.Until(d => ((IJavaScriptExecutor)d).ExecuteScript("return document.readyState").Equals("complete"));

                    var iframes = WaitForIframes(driver);
                    foreach (var iframe in iframes)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            driver.SwitchTo().Frame(iframe);
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
                            }
                        }
                        catch (Exception e)
                        {
                            Logger.Error($"Error collecting ads from iframe: {e}");
                        }
                        finally
                        {
                            driver.SwitchTo().DefaultContent();
                        }
                    }

                    _progressTracker.UpdateProgress(job.Url, incrementIteration: true);
                    await Task.Delay(_appSettings.IterationInterval, cancellationToken);
                }

                // 수집 완료 후 상태 업데이트
                _progressTracker.UpdateProgress(job.Url, status: ProgressStatus.Collected);
            }
            catch (OperationCanceledException)
            {
                Logger.Info($"Collection canceled for {job.Url}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error collecting ads from {job.Url}: {ex}");
                _progressTracker.UpdateProgress(job.Url, status: ProgressStatus.Error, incrementError: true);
                throw; // 예외를 상위로 전달하여 재시도 로직이 작동하도록 함
            }
            finally
            {
                _progressTracker.UpdateProgress(job.Url, threadCountChange: -1);
            }
        }

        private async Task ClickAdLinksAsync(CancellationToken cancellationToken)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            List<Task> tasks = new List<Task>();

            foreach (var kvp in _collectedAdLinks)
            {
                var url = kvp.Key;
                var adLinks = kvp.Value.Distinct().ToList();

                // 클릭 시작 시 상태를 Clicking으로 설정하고 PendingClicks를 설정
                _progressTracker.UpdateProgress(url, status: ProgressStatus.Clicking, pendingClicksChange: adLinks.Count);

                foreach (var adLink in adLinks)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    await _maxDegreeOfParallelism.WaitAsync(cancellationToken);

                    tasks.Add(Task.Run(async () =>
                    {
                        await ClickAdWithRetryAsync(adLink, url, cancellationToken);
                        _maxDegreeOfParallelism.Release();
                    }, cancellationToken));
                }

                // 여기에서 상태를 Finished로 설정하지 않음
            }

            await Task.WhenAll(tasks);
            stopwatch.Stop();
            Logger.Info($"Clicking phase completed in {stopwatch.Elapsed.TotalSeconds} seconds.");
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
                        await ClickSingleAdAsync(driver, adLink, originalUrl, cancellationToken);
                        success = true;
                        _browserInstances.Enqueue(driver);
                    }
                    else
                    {
                        driver = CreateNewBrowserInstance();
                        await ClickSingleAdAsync(driver, adLink, originalUrl, cancellationToken);
                        success = true;
                        _browserInstances.Enqueue(driver);
                    }
                }
                catch (WebDriverException ex)
                {
                    Logger.Error($"WebDriverException occurred while clicking ad: {ex.Message}. Removing browser instance and retrying.");
                    if (driver != null)
                    {
                        driver.Quit();
                        driver.Dispose();
                    }
                    driver = null; // 인스턴스를 제거했으므로 null로 설정

                    // 새로운 브라우저 인스턴스 생성
                    driver = CreateNewBrowserInstance();

                    retryCount++;
                    if (retryCount >= maxRetries)
                    {
                        Logger.Error($"Max retries reached for ad {adLink}. Skipping ad.");
                        _progressTracker.UpdateProgress(originalUrl, incrementError: true);
                        break;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"Exception occurred while clicking ad: {ex.Message}. Retrying.");
                    retryCount++;
                    if (retryCount >= maxRetries)
                    {
                        Logger.Error($"Max retries reached for ad {adLink}. Skipping ad.");
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

                // 페이지 로드 타임아웃 설정
                driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(60);

                WebDriverWait wait = new WebDriverWait(driver, TimeSpan.FromSeconds(20));
                wait.Until(d => ((IJavaScriptExecutor)d).ExecuteScript("return document.readyState").Equals("complete"));

                await Task.Delay(_appSettings.ClickAdInterval, cancellationToken); // 광고가 등록되도록 대기

                // 광고 클릭 수 증가
                _progressTracker.UpdateProgress(originalUrl, adsClickedChange: 1);

                // PendingClicks 감소
                _progressTracker.UpdateProgress(originalUrl, pendingClicksChange: -1);
            }
            catch (OperationCanceledException)
            {
                Logger.Info($"Clicking canceled for {adLink}");
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
            var webDriverService = new WebDriverService();
            if (webDriverService.SetupAndLogin(out IWebDriver driver, CancellationToken.None))
            {
                Logger.Info("New browser instance created.");
                return driver;
            }
            else
            {
                throw new Exception("Failed to create new browser instance.");
            }
        }

        private ReadOnlyCollection<IWebElement> WaitForIframes(IWebDriver driver)
        {
            return new WebDriverWait(driver, TimeSpan.FromSeconds(20))
                .Until(d =>
                {
                    var iframes = d.FindElements(By.CssSelector("iframe[id^='comAd']"));
                    return iframes.Count > 0 ? iframes : null;
                });
        }
    }
}
