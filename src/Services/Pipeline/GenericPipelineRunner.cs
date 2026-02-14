using InvenAdClicker.Models;
using InvenAdClicker.Services.Interfaces;
using InvenAdClicker.Utils;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace InvenAdClicker.Services.Pipeline
{
    public class GenericPipelineRunner<TPage> : IPipelineRunner where TPage : class
    {
        private readonly AppSettings _settings;
        private readonly IAppLogger _logger;
        private readonly IBrowserPool<TPage> _browserPool;
        private readonly ProgressTracker _progress;
        private readonly IAdCollector<TPage> _adCollector;
        private readonly IAdClicker<TPage> _adClicker;

        public GenericPipelineRunner(AppSettings settings, IAppLogger logger,
            IBrowserPool<TPage> browserPool, ProgressTracker progress,
            IAdCollector<TPage> adCollector, IAdClicker<TPage> adClicker)
        {
            _settings = settings;
            _logger = logger;
            _browserPool = browserPool;
            _progress = progress;
            _adCollector = adCollector;
            _adClicker = adClicker;
        }

        public async Task RunAsync(string[] urls, CancellationToken cancellationToken = default)
        {
            if (urls == null || urls.Length == 0)
            {
                _logger.Warn("TargetUrls가 비어 있어 파이프라인을 종료합니다.");
                return;
            }

            int mdp = Math.Max(1, _settings.MaxDegreeOfParallelism);
            if (mdp == 1)
            {
                await RunSingleWorkerAsync(urls, cancellationToken);
                _logger.Info("파이프라인 실행이 완료되었습니다.");
                return;
            }

            int collectorCount = Math.Max(1, mdp - 1);
            int clickerCount = Math.Max(1, mdp - collectorCount);
            int urlCapacity = Math.Max(4, collectorCount * 2);
            int clickCapacity = Math.Max(100, mdp * 50);

            var urlChannel = Channel.CreateBounded<string>(new BoundedChannelOptions(urlCapacity)
            {
                SingleWriter = true,
                SingleReader = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false
            });

            var clickChannel = Channel.CreateBounded<(string page, string link)>(new BoundedChannelOptions(clickCapacity)
            {
                SingleWriter = false,
                SingleReader = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false
            });

            var urlWriter = urlChannel.Writer;
            var urlReader = urlChannel.Reader;
            var clickWriter = clickChannel.Writer;
            var clickReader = clickChannel.Reader;

            Task producerTask = ProduceUrlsAsync(urls, urlWriter, cancellationToken);
            Task[] clickers = StartClickers(clickerCount, clickReader, cancellationToken);
            Task[] collectors = StartCollectors(collectorCount, urlReader, clickWriter, cancellationToken);

            try
            {
                await producerTask;
                await Task.WhenAll(collectors);
                clickWriter.TryComplete();
                await Task.WhenAll(clickers);
            }
            catch (OperationCanceledException)
            {
                urlWriter.TryComplete();
                clickWriter.TryComplete();
                throw;
            }
            catch (Exception ex)
            {
                urlWriter.TryComplete(ex);
                clickWriter.TryComplete(ex);
                throw;
            }
            finally
            {
                urlWriter.TryComplete();
                clickWriter.TryComplete();
            }

            _logger.Info("파이프라인 실행이 완료되었습니다.");
        }

        private async Task ProduceUrlsAsync(string[] urls, ChannelWriter<string> writer, CancellationToken cancellationToken)
        {
            try
            {
                foreach (var url in urls)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await writer.WriteAsync(url, cancellationToken);
                    _progress.Update(url, ProgressStatus.Waiting, iterDelta: 1);
                }
                writer.TryComplete();
            }
            catch (Exception ex)
            {
                writer.TryComplete(ex);
                throw;
            }
        }

        private Task[] StartCollectors(
            int collectorCount,
            ChannelReader<string> urlReader,
            ChannelWriter<(string page, string link)> clickWriter,
            CancellationToken cancellationToken)
        {
            var collectors = new Task[collectorCount];
            for (int i = 0; i < collectorCount; i++)
            {
                int workerId = i;
                collectors[i] = Task.Run(async () =>
                {
                    var page = await _browserPool.AcquireAsync(cancellationToken);
                    _logger.Info($"[Collector:{workerId}] Started");
                    try
                    {
                        await foreach (var url in urlReader.ReadAllAsync(cancellationToken))
                        {
                            page = await CollectOneAsync(page, url, clickWriter, cancellationToken);
                        }
                    }
                    finally
                    {
                        _browserPool.Release(page);
                        _logger.Info($"[Collector:{workerId}] Ended");
                    }
                }, cancellationToken);
            }
            return collectors;
        }

        private async Task<TPage> CollectOneAsync(
            TPage page,
            string url,
            ChannelWriter<(string page, string link)> clickWriter,
            CancellationToken cancellationToken)
        {
            List<string> links;
            _progress.Update(url, ProgressStatus.Collecting, threadDelta: +1);
            try
            {
                links = await _adCollector.CollectLinksAsync(page, url, cancellationToken);
                var status = links.Count > 0 ? ProgressStatus.Collected : ProgressStatus.NoAds;
                _progress.Update(url, status, adsDelta: links.Count);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error($"[Collector] Collection Error on {url}: {ex.Message}", ex);
                _progress.Update(url, ProgressStatus.Error, errDelta: 1);
                page = await _browserPool.RenewAsync(page, cancellationToken);
                return page;
            }
            finally
            {
                _progress.Update(url, threadDelta: -1);
            }

            if (links.Count == 0)
            {
                return page;
            }

            foreach (var link in links)
            {
                // Important: increase PendingClicks before making the item visible to clickers.
                _progress.Update(url, pendingClicksDelta: +1);
                try
                {
                    await clickWriter.WriteAsync((url, link), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    _progress.Update(url, pendingClicksDelta: -1);
                    throw;
                }
                catch (ChannelClosedException)
                {
                    _progress.Update(url, pendingClicksDelta: -1);
                    throw;
                }
                catch
                {
                    _progress.Update(url, pendingClicksDelta: -1);
                    throw;
                }
            }

            return page;
        }

        private Task[] StartClickers(
            int clickerCount,
            ChannelReader<(string page, string link)> clickReader,
            CancellationToken cancellationToken)
        {
            var clickers = new Task[clickerCount];
            for (int i = 0; i < clickerCount; i++)
            {
                int workerId = i;
                clickers[i] = Task.Run(async () =>
                {
                    var page = await _browserPool.AcquireAsync(cancellationToken);
                    _logger.Info($"[Clicker:{workerId}] Started");
                    try
                    {
                        await foreach (var work in clickReader.ReadAllAsync(cancellationToken))
                        {
                            page = await ClickOneAsync(page, work.page, work.link, cancellationToken);
                        }
                    }
                    finally
                    {
                        _browserPool.Release(page);
                        _logger.Info($"[Clicker:{workerId}] Ended");
                    }
                }, cancellationToken);
            }
            return clickers;
        }

        private async Task<TPage> ClickOneAsync(TPage page, string sourceUrl, string link, CancellationToken cancellationToken)
        {
            _progress.Update(sourceUrl, ProgressStatus.Clicking, threadDelta: +1);
            try
            {
                page = await _adClicker.ClickAdAsync(page, link, cancellationToken);
                _progress.Update(sourceUrl, clickDelta: 1);
                return page;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error($"[Clicker] Click Error on '{link}' (from '{sourceUrl}'): {ex.Message}", ex);
                _progress.Update(sourceUrl, errDelta: 1);
                page = await _browserPool.RenewAsync(page, cancellationToken);
                return page;
            }
            finally
            {
                _progress.Update(sourceUrl, pendingClicksDelta: -1, threadDelta: -1);
            }
        }

        private async Task RunSingleWorkerAsync(string[] urls, CancellationToken cancellationToken)
        {
            var page = await _browserPool.AcquireAsync(cancellationToken);
            try
            {
                foreach (var url in urls)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    List<string> links;
                    _progress.Update(url, ProgressStatus.Collecting, threadDelta: +1);
                    try
                    {
                        links = await _adCollector.CollectLinksAsync(page, url, cancellationToken);
                        var status = links.Count > 0 ? ProgressStatus.Collected : ProgressStatus.NoAds;
                        _progress.Update(url, status, adsDelta: links.Count);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"[SingleWorker] Collection Error on {url}: {ex.Message}", ex);
                        _progress.Update(url, ProgressStatus.Error, errDelta: 1);
                        page = await _browserPool.RenewAsync(page, cancellationToken);
                        continue;
                    }
                    finally
                    {
                        _progress.Update(url, threadDelta: -1);
                    }

                    if (links.Count == 0)
                    {
                        continue;
                    }

                    _progress.Update(url, pendingClicksDelta: links.Count);
                    foreach (var link in links)
                    {
                        _progress.Update(url, ProgressStatus.Clicking, threadDelta: +1);
                        try
                        {
                            page = await _adClicker.ClickAdAsync(page, link, cancellationToken);
                            _progress.Update(url, clickDelta: 1);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            _logger.Error($"[SingleWorker] Click Error on '{link}' (from '{url}'): {ex.Message}", ex);
                            _progress.Update(url, errDelta: 1);
                            page = await _browserPool.RenewAsync(page, cancellationToken);
                        }
                        finally
                        {
                            _progress.Update(url, pendingClicksDelta: -1, threadDelta: -1);
                        }
                    }
                }
            }
            finally
            {
                _browserPool.Release(page);
            }
        }
    }
}
