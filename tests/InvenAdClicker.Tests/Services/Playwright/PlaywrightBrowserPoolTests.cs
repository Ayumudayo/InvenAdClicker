using InvenAdClicker.Models;
using InvenAdClicker.Services.Playwright;
using InvenAdClicker.Utils;
using Microsoft.Playwright;
using Moq;
using NUnit.Framework;
using System;
using System.Threading.Tasks;

namespace InvenAdClicker.Tests.Services.Playwright
{
    [TestFixture]
    public class PlaywrightBrowserPoolTests
    {
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
