using InvenAdClicker.Models;
using InvenAdClicker.Services.Interfaces;
using InvenAdClicker.Services.Pipeline;
using InvenAdClicker.Services.Playwright;
using InvenAdClicker.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Moq;
using NUnit.Framework;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace InvenAdClicker.Tests
{
    [TestFixture]
    public class ServiceRegistrationTests
    {
        [Test]
        public async Task InjectedPaths_ControlCredentialsAndConfigurationWithoutCreatingDefaultUserData()
        {
            var root = Path.Combine(Path.GetTempPath(), "inven-di-paths-" + Guid.NewGuid().ToString("N"));
            var paths = new AppPaths(root, Path.Combine(root, "isolated"));
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddInvenAdClicker(new AppSettings(), paths: paths);
            await using var provider = services.BuildServiceProvider();
            try
            {
                Assert.That(provider.GetRequiredService<AppPaths>(), Is.SameAs(paths));
                provider.GetRequiredService<SettingsManager>().UpdateSchemaFile();
                Assert.That(File.Exists(paths.SettingsFilePath), Is.True);
                if (OperatingSystem.IsWindows())
                {
                    provider.GetRequiredService<Encryption>().SaveCredentials("id", "password");
                    Assert.That(File.Exists(paths.CredentialFilePath), Is.True);
                }
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }
        [Test]
        public async Task PipelineGraph_ResolvesInScope_WithIndependentRuntimePerScope()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddInvenAdClicker(new AppSettings());
            var pool = new Mock<IBrowserPool<IPage>>();
            services.AddScoped(_ => pool.Object);
            await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true
            });
            await using var first = provider.CreateAsyncScope();
            await using var second = provider.CreateAsyncScope();

            Assert.Multiple(() =>
            {
                Assert.That(first.ServiceProvider.GetRequiredService<IPipelineRunner>(), Is.TypeOf<GenericPipelineRunner<IPage>>());
                Assert.That(first.ServiceProvider.GetRequiredService<IAdCollector<IPage>>(), Is.TypeOf<PlaywrightAdCollector>());
                Assert.That(first.ServiceProvider.GetRequiredService<IAdClicker<IPage>>(), Is.TypeOf<PlaywrightAdClicker>());
                Assert.That(first.ServiceProvider.GetRequiredService<PlaywrightSession>(),
                    Is.Not.SameAs(second.ServiceProvider.GetRequiredService<PlaywrightSession>()));
            });
        }

        [Test]
        public async Task Session_CancelledInitialization_DoesNotStartDriverOrExposeBrowser()
        {
            await using var session = new PlaywrightSession(new AppSettings());
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert.ThrowsAsync<OperationCanceledException>(() => session.InitializeAsync(true, cancellation.Token));
            Assert.Throws<InvalidOperationException>(() => _ = session.Browser);
        }

        [Test]
        public void AssetValidation_IdentifiesMissingPlaywrightDllBeforeStartingDriver()
        {
            string directory = Path.Combine(Path.GetTempPath(), "InvenAdClicker-assets-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                File.WriteAllText(Path.Combine(directory, "appsettings.json"), "{}");
                var exception = Assert.Throws<FileNotFoundException>(() => PlaywrightDeployment.VerifyAssets(directory));
                Assert.That(exception!.FileName, Is.EqualTo(Path.Combine(directory, "Microsoft.Playwright.dll")));
            }
            finally
            {
                File.Delete(Path.Combine(directory, "appsettings.json"));
                Directory.Delete(directory);
            }
        }
    }
}
