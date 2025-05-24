using InvenAdClicker.Config;
using InvenAdClicker.Services.Interfaces;
using InvenAdClicker.Utils;
using OpenQA.Selenium;
using System.Collections.Concurrent;

namespace InvenAdClicker.Services.Selenium
{
    public class SeleniumAdCollector : IAdCollector
    {
        private readonly AppSettings _settings;
        private readonly ILogger _logger;
        private readonly Encryption _encryption;

        public SeleniumAdCollector(
            AppSettings settings,
            ILogger logger,
            Encryption encryption)
        {
            _settings = settings;
            _logger = logger;
            _encryption = encryption;
        }

        public async Task<Dictionary<string, IEnumerable<string>>> CollectAsync(
            string[] urls,
            CancellationToken cancellationToken = default)
        {
            // ������ Ǯ ���� (MaxDegreeOfParallelism ������ŭ)
            var browserPool = new ConcurrentQueue<SeleniumWebBrowser>();
            for (int i = 0; i < _settings.MaxDegreeOfParallelism; i++)
            {
                // �����ڿ��� �α��α��� �Ϸ��
                browserPool.Enqueue(new SeleniumWebBrowser(_settings, _logger, _encryption));
            }

            try
            {
                var result = new ConcurrentDictionary<string, IEnumerable<string>>();
                var po = new ParallelOptions
                {
                    MaxDegreeOfParallelism = _settings.MaxDegreeOfParallelism,
                    CancellationToken = cancellationToken
                };

                await Task.Run(() =>
                {
                    Parallel.ForEach(urls, po, pageUrl =>
                    {
                        po.CancellationToken.ThrowIfCancellationRequested();

                        // ������ ī��Ʈ +1
                        ProgressTracker.Instance.Update(
                            pageUrl,
                            threadDelta: +1,
                            status: ProgressStatus.Collecting);

                        // Ǯ���� ������ ������
                        if (!browserPool.TryDequeue(out var browser))
                            throw new InvalidOperationException("No available browser in pool");

                        try
                        {
                            browser.Driver.Navigate().GoToUrl(pageUrl);

                            // iframe ��ȸ�Ͽ� ���� ��ũ ����
                            var frames = browser.Driver
                                               .FindElements(By.TagName("iframe"))
                                               .ToList();
                            _logger.Info($"[{pageUrl}] Found {frames.Count} iframes");

                            var links = new List<string>();
                            foreach (var frame in frames)
                            {
                                try
                                {
                                    browser.Driver.SwitchTo().Frame(frame);
                                    links.AddRange(
                                        browser.Driver.FindElements(By.TagName("a"))
                                            .Select(a => a.GetAttribute("href"))
                                            .Where(h => !string.IsNullOrEmpty(h)));
                                }
                                catch (Exception ex)
                                {
                                    _logger.Warn($"[{pageUrl}] iframe ����: {ex.Message}");
                                    ProgressTracker.Instance.Update(
                                        pageUrl,
                                        errDelta: 1);
                                }
                                finally
                                {
                                    browser.Driver.SwitchTo().DefaultContent();
                                }
                            }

                            var distinct = links.Distinct().ToArray();
                            result[pageUrl] = distinct;

                            // ���� ��� �ݿ�
                            ProgressTracker.Instance.Update(
                                pageUrl,
                                status: ProgressStatus.Collected,
                                adsDelta: distinct.Length);
                        }
                        finally
                        {
                            // ������ ī��Ʈ -1
                            ProgressTracker.Instance.Update(
                                pageUrl,
                                threadDelta: -1);

                            // ������ �ݳ�
                            browserPool.Enqueue(browser);
                        }
                    });
                }, cancellationToken);

                return result.ToDictionary(kv => kv.Key, kv => kv.Value);
            }
            finally
            {
                // Ǯ�� ���� ������ ���� ����
                while (browserPool.TryDequeue(out var browser))
                {
                    browser.Dispose();
                }
            }
        }
    }
}
