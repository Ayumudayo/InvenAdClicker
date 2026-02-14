using InvenAdClicker.Models;
using InvenAdClicker.Services.Interfaces;
using InvenAdClicker.Services.Pipeline;
using InvenAdClicker.Utils;
using Moq;
using NUnit.Framework;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace InvenAdClicker.Tests.Services.Pipeline
{
    [TestFixture]
    public class GenericPipelineRunnerTests
    {
        private Mock<IAppLogger> _mockLogger;
        private Mock<IBrowserPool<object>> _mockBrowserPool;
        private Mock<IAdCollector<object>> _mockCollector;
        private Mock<IAdClicker<object>> _mockClicker;
        private AppSettings _settings;
        private ProgressTracker _progress;

        [SetUp]
        public void Setup()
        {
            _mockLogger = new Mock<IAppLogger>();
            _mockBrowserPool = new Mock<IBrowserPool<object>>();
            _mockCollector = new Mock<IAdCollector<object>>();
            _mockClicker = new Mock<IAdClicker<object>>();
            _settings = new AppSettings { MaxDegreeOfParallelism = 1 };
            _progress = ProgressTracker.Instance;
            _progress.Initialize(new[] { "http://test.com" });
        }

        [Test]
        public async Task RunAsync_ShouldProcessUrls()
        {
            // Arrange
            var urls = new[] { "http://test.com" };
            var runner = new GenericPipelineRunner<object>(
                _settings,
                _mockLogger.Object,
                _mockBrowserPool.Object,
                _progress,
                _mockCollector.Object,
                _mockClicker.Object
            );

            _mockBrowserPool.Setup(x => x.AcquireAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new object());

            _mockCollector.Setup(x => x.CollectLinksAsync(It.IsAny<object>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new System.Collections.Generic.List<string>());

            // Act
            await runner.RunAsync(urls);

            // Assert
            _mockCollector.Verify(x => x.CollectLinksAsync(It.IsAny<object>(), "http://test.com", It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task RunAsync_Mdp2_ShouldClickCollectedLinks()
        {
            // Arrange
            _settings.MaxDegreeOfParallelism = 2;
            var urls = new[] { "http://test.com" };
            var runner = new GenericPipelineRunner<object>(
                _settings,
                _mockLogger.Object,
                _mockBrowserPool.Object,
                _progress,
                _mockCollector.Object,
                _mockClicker.Object
            );

            _mockBrowserPool.Setup(x => x.AcquireAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new object());

            var links = new List<string> { "http://example.com/a", "http://example.com/b" };
            _mockCollector.Setup(x => x.CollectLinksAsync(It.IsAny<object>(), urls[0], It.IsAny<CancellationToken>()))
                .ReturnsAsync(links);

            _mockClicker.Setup(x => x.ClickAdAsync(It.IsAny<object>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((object page, string _, CancellationToken __) => page);

            // Act
            await runner.RunAsync(urls);

            // Assert
            _mockCollector.Verify(x => x.CollectLinksAsync(It.IsAny<object>(), urls[0], It.IsAny<CancellationToken>()), Times.Once);
            _mockClicker.Verify(x => x.ClickAdAsync(It.IsAny<object>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(links.Count));
            _mockClicker.Verify(x => x.ClickAdAsync(It.IsAny<object>(), links[0], It.IsAny<CancellationToken>()), Times.Once);
            _mockClicker.Verify(x => x.ClickAdAsync(It.IsAny<object>(), links[1], It.IsAny<CancellationToken>()), Times.Once);
        }
    }
}
