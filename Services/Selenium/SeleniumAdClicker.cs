using InvenAdClicker.Config;
using InvenAdClicker.Services.Interfaces;
using InvenAdClicker.Utils;
using System.Collections.Concurrent;

namespace InvenAdClicker.Services.Selenium
{
    public class SeleniumAdClicker : IAdClicker
    {
        private readonly AppSettings _settings;
        private readonly ILogger _logger;
        private readonly Encryption _encryption;

        public SeleniumAdClicker(
            AppSettings settings,
            ILogger logger,
            Encryption encryption)
        {
            _settings = settings;
            _logger = logger;
            _encryption = encryption;
        }

        public async Task ClickAsync(
            Dictionary<string, IEnumerable<string>> pageToLinks,
            CancellationToken cancellationToken = default)
        {
            // ������ Ǯ ����
            var browserPool = new ConcurrentQueue<SeleniumWebBrowser>();
            for (int i = 0; i < _settings.MaxDegreeOfParallelism; i++)
            {
                browserPool.Enqueue(new SeleniumWebBrowser(_settings, _logger, _encryption));
            }

            try
            {
                // �������� ���� ó��
                var po = new ParallelOptions
                {
                    MaxDegreeOfParallelism = _settings.MaxDegreeOfParallelism,
                    CancellationToken = cancellationToken
                };

                await Task.Run(() =>
                {
                    Parallel.ForEach(pageToLinks, po, kv =>
                    {
                        po.CancellationToken.ThrowIfCancellationRequested();

                        string pageUrl = kv.Key;
                        var links = kv.Value.ToArray();

                        // Ŭ�� �ܰ� ����: Pending �ʱ�ȭ + ������ ī��Ʈ
                        ProgressTracker.Instance.Update(
                            pageUrl,
                            status: ProgressStatus.Clicking,
                            pendingClicksDelta: links.Length,
                            threadDelta: +1);

                        // Ǯ���� ������ ������
                        if (!browserPool.TryDequeue(out var browser))
                            throw new InvalidOperationException("No available browser in pool");

                        try
                        {
                            foreach (var link in links)
                            {
                                try
                                {
                                    po.CancellationToken.ThrowIfCancellationRequested();

                                    browser.Driver.Navigate().GoToUrl(link);
                                    _logger.Info($"Clicked ad: {link}");

                                    ProgressTracker.Instance.Update(
                                        pageUrl,
                                        clickDelta: 1,
                                        pendingClicksDelta: -1);

                                    // ���� Delay
                                    Thread.Sleep(_settings.ClickDelayMilliseconds);
                                }
                                catch (Exception exLink)
                                {
                                    _logger.Warn($"[{pageUrl}] Ŭ�� ����: {exLink.Message}");
                                    ProgressTracker.Instance.Update(
                                        pageUrl,
                                        errDelta: 1);
                                }
                            }
                        }
                        finally
                        {
                            // ������ ���� + ������ �ݳ�
                            ProgressTracker.Instance.Update(
                                pageUrl,
                                threadDelta: -1);
                            browserPool.Enqueue(browser);
                        }
                    });
                }, cancellationToken);
            }
            finally
            {
                // Ǯ�� ���� ������ ��� ����
                while (browserPool.TryDequeue(out var browser))
                {
                    browser.Dispose();
                }
            }
        }
    }
}
