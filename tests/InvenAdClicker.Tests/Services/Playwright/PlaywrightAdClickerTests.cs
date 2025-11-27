using InvenAdClicker.Models;
using InvenAdClicker.Services.Interfaces;
using InvenAdClicker.Services.Playwright;
using InvenAdClicker.Utils;
using Microsoft.Playwright;
using Moq;
using NUnit.Framework;
using System.Threading;
using System.Threading.Tasks;

namespace InvenAdClicker.Tests.Services.Playwright
{
    [TestFixture]
    public class PlaywrightAdClickerTests
    {
        private Mock<IAppLogger> _mockLogger;
        private Mock<IBrowserPool<IPage>> _mockBrowserPool;
        private Mock<IPage> _mockPage;
        private AppSettings _settings;
        private PlaywrightAdClicker _clicker;

        [SetUp]
        public void Setup()
        {
            _mockLogger = new Mock<IAppLogger>();
            _mockBrowserPool = new Mock<IBrowserPool<IPage>>();
            _mockPage = new Mock<IPage>();
            _settings = new AppSettings();
            _clicker = new PlaywrightAdClicker(_settings, _mockLogger.Object, _mockBrowserPool.Object);
        }

        [Test]
        public async Task ClickAdAsync_DryRunEnabled_ShouldNotNavigate()
        {
            // Arrange
            _settings.DryRun = true;
            var link = "http://example.com/ad";

            // Act
            await _clicker.ClickAdAsync(_mockPage.Object, link, CancellationToken.None);

            // Assert
            _mockPage.Verify(p => p.GotoAsync(It.IsAny<string>(), It.IsAny<PageGotoOptions>()), Times.Never);
            _mockLogger.Verify(l => l.Info(It.Is<string>(s => s.Contains("[DryRun]"))), Times.Once);
        }

        [Test]
        public async Task ClickAdAsync_DryRunDisabled_ShouldNavigate()
        {
            // Arrange
            _settings.DryRun = false;
            var link = "http://example.com/ad";

            // Act
            await _clicker.ClickAdAsync(_mockPage.Object, link, CancellationToken.None);

            // Assert
            _mockPage.Verify(p => p.GotoAsync(link, It.IsAny<PageGotoOptions>()), Times.Once);
        }
    }
}
