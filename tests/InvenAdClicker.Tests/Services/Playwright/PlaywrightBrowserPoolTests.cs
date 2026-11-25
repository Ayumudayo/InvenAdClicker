using InvenAdClicker.Models;
using InvenAdClicker.Services.Playwright;
using InvenAdClicker.Utils;
using Microsoft.Playwright;
using Moq;
using NUnit.Framework;
using System;
using System.Threading.Tasks;
using System.Threading;
using System.Linq;
using System.Collections.Concurrent;

namespace InvenAdClicker.Tests.Services.Playwright
{
    [TestFixture]
    public class PlaywrightBrowserPoolTests
    {
        [Test]
        public async Task StorageSnapshot_IsCapturedWithoutPathAndReusedForPreloadRenewAndRestart()
        {
            const string state = "{\"cookies\":[],\"origins\":[]}";
            bool connected = true;
            var original = CreateBrowser(() => connected);
            var replacement = CreateBrowser(true);
            original.Context.Setup(context => context.StorageStateAsync(It.IsAny<BrowserContextStorageStateOptions>()))
                .ReturnsAsync(state);
            await using var pool = new PlaywrightBrowserPool(original.Browser.Object,
                new AppSettings { MaxDegreeOfParallelism = 1 }, new TestLogger(), new Encryption(),
                () => Task.FromResult(replacement.Browser.Object));

            await pool.CaptureStorageStateAsync(original.Context.Object);
            await pool.PreloadAsync(1);
            var page = await pool.AcquireAsync();
            page = await pool.RenewAsync(page);
            connected = false;
            page = await pool.RenewAsync(page);
            pool.Release(page);

            original.Context.Verify(context => context.StorageStateAsync(null), Times.Once);
            original.Browser.Verify(browser => browser.NewContextAsync(It.Is<BrowserNewContextOptions>(
                options => options.StorageState == state && options.StorageStatePath == null)), Times.Exactly(2));
            replacement.Browser.Verify(browser => browser.NewContextAsync(It.Is<BrowserNewContextOptions>(
                options => options.StorageState == state && options.StorageStatePath == null)), Times.Once);
        }

        [Test]
        public async Task MaintenanceRefill_UsesCapturedStorageSnapshot()
        {
            const string state = "{\"cookies\":[],\"origins\":[]}";
            var browser = CreateBrowser(true);
            browser.Context.Setup(context => context.StorageStateAsync(It.IsAny<BrowserContextStorageStateOptions>()))
                .ReturnsAsync(state);
            await using var pool = new PlaywrightBrowserPool(browser.Browser.Object,
                new AppSettings { MaxDegreeOfParallelism = 1 }, new TestLogger(), new Encryption());
            await pool.CaptureStorageStateAsync(browser.Context.Object);
            var page = await pool.AcquireAsync();
            browser.Page.SetupGet(item => item.IsClosed).Returns(true);
            pool.Release(page);
            await pool.MaintainPoolAsync(1);
            browser.Browser.Verify(item => item.NewContextAsync(It.Is<BrowserNewContextOptions>(
                options => options.StorageState == state && options.StorageStatePath == null)), Times.Exactly(2));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task CaptureStorageState_CancelledOrDisposedCaptureCannotPublishSnapshot(bool cancel)
        {
            var browser = CreateBrowser(true);
            var state = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            browser.Context.Setup(context => context.StorageStateAsync(It.IsAny<BrowserContextStorageStateOptions>()))
                .Returns(state.Task);
            var pool = new PlaywrightBrowserPool(browser.Browser.Object,
                new AppSettings { MaxDegreeOfParallelism = 1 }, new TestLogger(), new Encryption());
            using var cancellation = new CancellationTokenSource();
            var capture = pool.CaptureStorageStateAsync(browser.Context.Object, cancellation.Token);
            if (cancel) cancellation.Cancel();
            else await pool.DisposeAsync();
            state.SetResult("{\"cookies\":[],\"origins\":[]}");
            if (cancel) Assert.ThrowsAsync<OperationCanceledException>(async () => await capture);
            else Assert.ThrowsAsync<ObjectDisposedException>(async () => await capture);
            browser.Context.Verify(context => context.StorageStateAsync(null), Times.Once);
            await pool.DisposeAsync();
        }

        [Test]
        public async Task AbortAsync_BypassesBlockedCreationAndIsIdempotent()
        {
            var browser = CreateBrowser(true);
            var creating = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finishCreation = new TaskCompletionSource<IBrowserContext>(TaskCreationOptions.RunContinuationsAsynchronously);
            int aborts = 0;
            browser.Browser.Setup(item => item.NewContextAsync(It.IsAny<BrowserNewContextOptions>()))
                .Returns(() => { creating.TrySetResult(); return finishCreation.Task; });
            browser.Browser.Setup(item => item.CloseAsync(It.IsAny<BrowserCloseOptions>()))
                .Returns(() => { finishCreation.TrySetException(new PlaywrightException("aborted")); return Task.CompletedTask; });
            var pool = new PlaywrightBrowserPool(browser.Browser.Object,
                new AppSettings { MaxDegreeOfParallelism = 1 }, new TestLogger(), new Encryption(),
                abortDriver: () => Interlocked.Increment(ref aborts));
            var acquire = pool.AcquireAsync();
            await creating.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await Task.WhenAll(pool.AbortAsync(), pool.AbortAsync()).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.ThrowsAsync<PlaywrightException>(async () => await acquire);
            await pool.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(aborts, Is.EqualTo(1));
            browser.Browser.Verify(item => item.CloseAsync(It.IsAny<BrowserCloseOptions>()), Times.Once);
            Assert.ThrowsAsync<ObjectDisposedException>(() => pool.AcquireAsync());
        }

        [Test]
        public async Task AbortAsync_CancelsPoolWaiterAndAllowsLateLeaseReturn()
        {
            var browser = CreateBrowser(true);
            var pool = new PlaywrightBrowserPool(browser.Browser.Object,
                new AppSettings { MaxDegreeOfParallelism = 1 }, new TestLogger(), new Encryption());
            var leased = await pool.AcquireAsync();
            var waiting = pool.AcquireAsync();
            await pool.AbortAsync();
            Assert.ThrowsAsync(Is.InstanceOf<OperationCanceledException>(),
                async () => await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
            await pool.DisposeAsync();
            Assert.DoesNotThrow(() => pool.Release(leased));
            browser.Context.Verify(item => item.CloseAsync(It.IsAny<BrowserContextCloseOptions>()), Times.Once);
        }

        [Test]
        public void AbortAsync_CloseFailureStillAbortsDriverAndIsObservedByDisposal()
        {
            var browser = CreateBrowser(true);
            int aborts = 0;
            var failure = new PlaywrightException("close failed");
            browser.Browser.Setup(item => item.CloseAsync(It.IsAny<BrowserCloseOptions>())).ThrowsAsync(failure);
            var pool = new PlaywrightBrowserPool(browser.Browser.Object,
                new AppSettings { MaxDegreeOfParallelism = 1 }, new TestLogger(), new Encryption(),
                abortDriver: () => Interlocked.Increment(ref aborts));

            Assert.That(Assert.ThrowsAsync<PlaywrightException>(() => pool.AbortAsync()), Is.SameAs(failure));
            Assert.That(aborts, Is.EqualTo(1));
            Assert.That(Assert.ThrowsAsync<PlaywrightException>(() => pool.DisposeAsync().AsTask()), Is.SameAs(failure));
        }

        [Test]
        public async Task RenewAsync_RelaunchesBrowserWhenCurrentBrowserIsDisconnected()
        {
            var disconnected = CreateBrowser(connected: false);
            var replacement = CreateBrowser(connected: true);
            int launches = 0;

            var pool = new PlaywrightBrowserPool(
                disconnected.Browser.Object,
                new AppSettings { MaxDegreeOfParallelism = 1 },
                new TestLogger(),
                new Encryption(),
                () =>
                {
                    launches++;
                    return Task.FromResult(replacement.Browser.Object);
                });

            var oldPage = CreatePage().Page.Object;

            var renewedPage = await pool.RenewAsync(oldPage);

            Assert.That(renewedPage, Is.SameAs(replacement.Page.Object));
            Assert.That(launches, Is.EqualTo(1));
            disconnected.Browser.Verify(
                browser => browser.NewContextAsync(It.IsAny<BrowserNewContextOptions>()),
                Times.Never);
            replacement.Browser.Verify(
                browser => browser.NewContextAsync(It.IsAny<BrowserNewContextOptions>()),
                Times.Once);
        }

        [Test]
        public async Task Release_DoesNotReturnPagesFromDisconnectedBrowserToPool()
        {
            bool originalConnected = true;
            var original = CreateBrowser(() => originalConnected);
            var replacement = CreateBrowser(connected: true);

            var pool = new PlaywrightBrowserPool(
                original.Browser.Object,
                new AppSettings { MaxDegreeOfParallelism = 1 },
                new TestLogger(),
                new Encryption(),
                () => Task.FromResult(replacement.Browser.Object));

            var stalePage = await pool.AcquireAsync();
            originalConnected = false;

            pool.Release(stalePage);
            var acquiredAfterDisconnect = await pool.AcquireAsync();

            Assert.That(acquiredAfterDisconnect, Is.SameAs(replacement.Page.Object));
            Assert.That(acquiredAfterDisconnect, Is.Not.SameAs(stalePage));
        }

        [Test]
        public async Task Release_ClosesContextForUnusablePage()
        {
            var browser = CreateBrowser(connected: true);
            browser.Page.SetupGet(item => item.IsClosed).Returns(true);

            var pool = new PlaywrightBrowserPool(
                browser.Browser.Object,
                new AppSettings { MaxDegreeOfParallelism = 1 },
                new TestLogger(),
                new Encryption());

            var unusablePage = await pool.AcquireAsync();

            pool.Release(unusablePage);

            browser.Context.Verify(
                context => context.CloseAsync(It.IsAny<BrowserContextCloseOptions>()),
                Times.Once);
        }

        [Test]
        public async Task DisposeAsync_DoesNotCloseDisconnectedBrowser()
        {
            var disconnected = CreateBrowser(connected: false);
            var pool = new PlaywrightBrowserPool(
                disconnected.Browser.Object,
                new AppSettings { MaxDegreeOfParallelism = 1 },
                new TestLogger(),
                new Encryption());

            await pool.DisposeAsync();

            disconnected.Browser.Verify(
                browser => browser.CloseAsync(It.IsAny<BrowserCloseOptions>()),
                Times.Never);
        }

        [Test]
        public async Task AcquireAsync_ReplacesPageClosedWhileIdle()
        {
            var browser = CreateBrowser(connected: true);
            var pool = new PlaywrightBrowserPool(browser.Browser.Object,
                new AppSettings { MaxDegreeOfParallelism = 1 }, new TestLogger(), new Encryption());
            var page = await pool.AcquireAsync();
            pool.Release(page);
            browser.Page.SetupGet(item => item.IsClosed).Returns(true);
            var replacement = CreatePage();
            replacement.Context.SetupGet(item => item.Browser).Returns(browser.Browser.Object);
            browser.Browser.Setup(item => item.NewContextAsync(It.IsAny<BrowserNewContextOptions>()))
                .ReturnsAsync(replacement.Context.Object);
            var acquired = await pool.AcquireAsync();
            Assert.That(acquired, Is.SameAs(replacement.Page.Object));
            browser.Context.Verify(item => item.CloseAsync(It.IsAny<BrowserContextCloseOptions>()), Times.Once);
            pool.Release(acquired);
            await pool.DisposeAsync();
        }

        [Test]
        public async Task ConcurrentMaintenance_CountsLeasedPagesAndDoesNotOverfill()
        {
            var browser = CreateBrowser(connected: true);
            var fixtures = new ConcurrentBag<PageFixture>();
            browser.Browser.Setup(item => item.NewContextAsync(It.IsAny<BrowserNewContextOptions>()))
                .Returns(async () =>
                {
                    await Task.Yield();
                    var fixture = CreatePage();
                    fixture.Context.SetupGet(item => item.Browser).Returns(browser.Browser.Object);
                    fixtures.Add(fixture);
                    return fixture.Context.Object;
                });
            var pool = new PlaywrightBrowserPool(browser.Browser.Object,
                new AppSettings { MaxDegreeOfParallelism = 4 }, new TestLogger(), new Encryption());
            var leases = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => pool.AcquireAsync()));
            await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => pool.MaintainPoolAsync(100)));
            Assert.That(fixtures.Count, Is.EqualTo(4));
            foreach (var fixture in fixtures) fixture.Page.SetupGet(item => item.IsClosed).Returns(true);
            await Task.WhenAll(leases.Select(page => Task.Run(() => pool.Release(page))));
            await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => pool.MaintainPoolAsync(4)));
            Assert.That(fixtures.Count, Is.EqualTo(8));
            var replacements = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => pool.AcquireAsync()));
            Assert.That(replacements.Distinct().Count(), Is.EqualTo(4));
            Assert.That(replacements.All(page => !page.IsClosed), Is.True);
            foreach (var page in replacements) pool.Release(page);
            await pool.DisposeAsync();
        }

        [Test]
        public async Task ConcurrentInvalidReturnsAndAcquires_RecoverWithoutLeakingSlots()
        {
            var browser = CreateBrowser(connected: true);
            int creates = 0;
            browser.Browser.Setup(item => item.NewContextAsync(It.IsAny<BrowserNewContextOptions>()))
                .Returns(async () =>
                {
                    await Task.Yield();
                    Interlocked.Increment(ref creates);
                    var fixture = CreatePage();
                    fixture.Context.SetupGet(item => item.Browser).Returns(browser.Browser.Object);
                    fixture.Page.SetupGet(item => item.IsClosed).Returns(true);
                    return fixture.Context.Object;
                });
            var pool = new PlaywrightBrowserPool(browser.Browser.Object,
                new AppSettings { MaxDegreeOfParallelism = 4 }, new TestLogger(), new Encryption());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await Task.WhenAll(Enumerable.Range(0, 16).Select(async _ =>
            {
                for (int iteration = 0; iteration < 8; iteration++)
                {
                    var page = await pool.AcquireAsync(timeout.Token);
                    await Task.Yield();
                    pool.Release(page);
                }
            }));
            Assert.That(creates, Is.EqualTo(128));
            await pool.DisposeAsync();
        }

        [Test]
        public async Task AcquireAsync_CreationFailureReturnsCapacityForNextRequest()
        {
            var browser = CreateBrowser(connected: true);
            browser.Browser.SetupSequence(item => item.NewContextAsync(It.IsAny<BrowserNewContextOptions>()))
                .ThrowsAsync(new PlaywrightException("creation failed"))
                .ReturnsAsync(browser.Context.Object);
            var pool = new PlaywrightBrowserPool(browser.Browser.Object,
                new AppSettings { MaxDegreeOfParallelism = 1 }, new TestLogger(), new Encryption());
            Assert.ThrowsAsync<PlaywrightException>(() => pool.AcquireAsync());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var page = await pool.AcquireAsync(timeout.Token);
            pool.Release(page);
            await pool.DisposeAsync();
        }

        [Test]
        public async Task MaintainPoolAsync_PrunesClosedIdlePagesBeforeCountingCapacity()
        {
            var browser = CreateBrowser(connected: true);
            var replacement = CreatePage();
            replacement.Context.SetupGet(item => item.Browser).Returns(browser.Browser.Object);
            var pool = new PlaywrightBrowserPool(browser.Browser.Object,
                new AppSettings { MaxDegreeOfParallelism = 1 }, new TestLogger(), new Encryption());
            await pool.PreloadAsync(1);
            browser.Page.SetupGet(item => item.IsClosed).Returns(true);
            browser.Browser.Setup(item => item.NewContextAsync(It.IsAny<BrowserNewContextOptions>()))
                .ReturnsAsync(replacement.Context.Object);

            await pool.MaintainPoolAsync(1);
            var page = await pool.AcquireAsync();

            Assert.That(page, Is.SameAs(replacement.Page.Object));
            browser.Context.Verify(item => item.CloseAsync(It.IsAny<BrowserContextCloseOptions>()), Times.Once);
            pool.Release(page);
            await pool.DisposeAsync();
        }

        [Test]
        public async Task MaintenanceLoop_InvalidReleaseAutomaticallyRefillsWithoutAcquire()
        {
            var browser = CreateBrowser(connected: true);
            var replacement = CreatePage();
            replacement.Context.SetupGet(item => item.Browser).Returns(browser.Browser.Object);
            var created = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pool = new PlaywrightBrowserPool(browser.Browser.Object,
                new AppSettings { MaxDegreeOfParallelism = 1 }, new TestLogger(), new Encryption());
            try
            {
                var original = await pool.AcquireAsync();
                browser.Page.SetupGet(item => item.IsClosed).Returns(true);
                browser.Browser.Setup(item => item.NewContextAsync(It.IsAny<BrowserNewContextOptions>()))
                    .Returns(() => { created.TrySetResult(); return Task.FromResult(replacement.Context.Object); });
                pool.StartMaintenance();
                pool.Release(original);

                await created.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var page = await pool.AcquireAsync();
                Assert.That(page, Is.SameAs(replacement.Page.Object));
                pool.Release(page);
            }
            finally { await pool.DisposeAsync(); }
            replacement.Context.Verify(item => item.CloseAsync(It.IsAny<BrowserContextCloseOptions>()), Times.Once);
            browser.Browser.Verify(item => item.CloseAsync(It.IsAny<BrowserCloseOptions>()), Times.Once);
        }

        [Test]
        public async Task MaintenanceLoop_PeriodicallyReplacesPageClosedWhileIdle()
        {
            var browser = CreateBrowser(connected: true);
            var replacement = CreatePage();
            replacement.Context.SetupGet(item => item.Browser).Returns(browser.Browser.Object);
            var created = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pool = new PlaywrightBrowserPool(browser.Browser.Object,
                new AppSettings { MaxDegreeOfParallelism = 1 }, new TestLogger(), new Encryption());
            try
            {
                await pool.PreloadAsync(1);
                browser.Page.SetupGet(item => item.IsClosed).Returns(true);
                browser.Browser.Setup(item => item.NewContextAsync(It.IsAny<BrowserNewContextOptions>()))
                    .Returns(() => { created.TrySetResult(); return Task.FromResult(replacement.Context.Object); });
                pool.StartMaintenance();

                await created.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var page = await pool.AcquireAsync();
                Assert.That(page, Is.SameAs(replacement.Page.Object));
                pool.Release(page);
            }
            finally { await pool.DisposeAsync(); }
            browser.Context.Verify(item => item.CloseAsync(It.IsAny<BrowserContextCloseOptions>()), Times.Once);
        }

        [Test]
        public async Task DisposeAsync_AwaitsInFlightMaintenanceAndClosesLateReplacement()
        {
            var browser = CreateBrowser(connected: true);
            var replacement = CreatePage();
            replacement.Context.SetupGet(item => item.Browser).Returns(browser.Browser.Object);
            var creating = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finishCreation = new TaskCompletionSource<IBrowserContext>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pool = new PlaywrightBrowserPool(browser.Browser.Object,
                new AppSettings { MaxDegreeOfParallelism = 1 }, new TestLogger(), new Encryption());
            var page = await pool.AcquireAsync();
            browser.Page.SetupGet(item => item.IsClosed).Returns(true);
            browser.Browser.Setup(item => item.NewContextAsync(It.IsAny<BrowserNewContextOptions>()))
                .Returns(() => { creating.TrySetResult(); return finishCreation.Task; });
            pool.StartMaintenance();
            pool.Release(page);
            await creating.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var disposal = pool.DisposeAsync().AsTask();
            Assert.That(disposal.IsCompleted, Is.False);
            finishCreation.SetResult(replacement.Context.Object);
            await disposal.WaitAsync(TimeSpan.FromSeconds(5));
            await pool.DisposeAsync();

            replacement.Context.Verify(item => item.CloseAsync(It.IsAny<BrowserContextCloseOptions>()), Times.Once);
            browser.Browser.Verify(item => item.CloseAsync(It.IsAny<BrowserCloseOptions>()), Times.Once);
            Assert.ThrowsAsync<ObjectDisposedException>(() => pool.AcquireAsync());
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task RenewAsync_InFlightCreationClosesLatePageOnCancellationOrDisposal(bool disposeWhileCreating)
        {
            var browser = CreateBrowser(connected: true);
            var replacement = CreatePage();
            replacement.Context.SetupGet(item => item.Browser).Returns(browser.Browser.Object);
            var creating = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finishCreation = new TaskCompletionSource<IBrowserContext>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pool = new PlaywrightBrowserPool(browser.Browser.Object,
                new AppSettings { MaxDegreeOfParallelism = 1 }, new TestLogger(), new Encryption());
            var original = await pool.AcquireAsync();
            browser.Browser.Setup(item => item.NewContextAsync(It.IsAny<BrowserNewContextOptions>()))
                .Returns(() => { creating.TrySetResult(); return finishCreation.Task; });
            using var cancellation = new CancellationTokenSource();
            var renewal = pool.RenewAsync(original, cancellation.Token);
            await creating.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Task? disposal = null;
            if (disposeWhileCreating)
            {
                disposal = pool.DisposeAsync().AsTask();
                Assert.That(disposal.IsCompleted, Is.False);
                browser.Browser.Verify(item => item.CloseAsync(It.IsAny<BrowserCloseOptions>()), Times.Never);
            }
            else cancellation.Cancel();
            finishCreation.SetResult(replacement.Context.Object);

            if (disposeWhileCreating)
                Assert.ThrowsAsync<ObjectDisposedException>(async () => await renewal.WaitAsync(TimeSpan.FromSeconds(5)));
            else
                Assert.ThrowsAsync(Is.InstanceOf<OperationCanceledException>(), async () => await renewal.WaitAsync(TimeSpan.FromSeconds(5)));
            browser.Page.SetupGet(item => item.IsClosed).Returns(true);
            pool.Release(original);
            if (disposal != null) await disposal.WaitAsync(TimeSpan.FromSeconds(5));
            else await pool.DisposeAsync();

            replacement.Context.Verify(item => item.CloseAsync(It.IsAny<BrowserContextCloseOptions>()), Times.Once);
            browser.Context.Verify(item => item.CloseAsync(It.IsAny<BrowserContextCloseOptions>()), Times.Exactly(2));
            browser.Browser.Verify(item => item.CloseAsync(It.IsAny<BrowserCloseOptions>()), Times.Once);
        }

        private static BrowserFixture CreateBrowser(bool connected)
        {
            return CreateBrowser(() => connected);
        }

        private static BrowserFixture CreateBrowser(Func<bool> isConnected)
        {
            var page = CreatePage();
            var browser = new Mock<IBrowser>(MockBehavior.Strict);
            browser.SetupGet(item => item.IsConnected).Returns(isConnected);
            browser.Setup(item => item.NewContextAsync(It.IsAny<BrowserNewContextOptions>()))
                .ReturnsAsync(page.Context.Object);
            browser.Setup(item => item.CloseAsync(It.IsAny<BrowserCloseOptions>()))
                .Returns(Task.CompletedTask);
            page.Context.SetupGet(item => item.Browser).Returns(browser.Object);

            return new BrowserFixture(browser, page.Context, page.Page);
        }

        private static PageFixture CreatePage()
        {
            var context = new Mock<IBrowserContext>(MockBehavior.Strict);
            var page = new Mock<IPage>(MockBehavior.Strict);

            page.SetupGet(item => item.Context).Returns(context.Object);
            page.SetupGet(item => item.IsClosed).Returns(false);
            page.Setup(item => item.RouteAsync(
                    "**/*",
                    It.IsAny<Func<IRoute, Task>>(),
                    It.IsAny<PageRouteOptions>()))
                .Returns(Task.CompletedTask);
            page.Setup(item => item.AddInitScriptAsync(It.IsAny<string>(), It.IsAny<string>()))
                .Returns(Task.CompletedTask);

            context.Setup(item => item.NewPageAsync()).ReturnsAsync(page.Object);
            context.Setup(item => item.CloseAsync(It.IsAny<BrowserContextCloseOptions>()))
                .Returns(Task.CompletedTask);

            return new PageFixture(context, page);
        }

        private sealed class BrowserFixture
        {
            public BrowserFixture(Mock<IBrowser> browser, Mock<IBrowserContext> context, Mock<IPage> page)
            {
                Browser = browser;
                Context = context;
                Page = page;
            }

            public Mock<IBrowser> Browser { get; }
            public Mock<IBrowserContext> Context { get; }
            public Mock<IPage> Page { get; }
        }

        private sealed class PageFixture
        {
            public PageFixture(Mock<IBrowserContext> context, Mock<IPage> page)
            {
                Context = context;
                Page = page;
            }

            public Mock<IBrowserContext> Context { get; }
            public Mock<IPage> Page { get; }
        }

        private sealed class TestLogger : IAppLogger
        {
            public void Debug(string msg)
            {
            }

            public void Error(string msg, Exception? ex = null)
            {
            }

            public void Fatal(string msg, Exception? ex = null)
            {
            }

            public void Info(string msg)
            {
            }

            public void Warn(string msg)
            {
            }
        }
    }
}
