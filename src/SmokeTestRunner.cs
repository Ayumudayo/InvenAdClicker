using InvenAdClicker.Services.Interfaces;
using InvenAdClicker.Services.Playwright;
using InvenAdClicker.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace InvenAdClicker
{
    public sealed class SmokeTestRunner
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly SettingsManager _settingsManager;
        private readonly AppPaths _paths;

        public SmokeTestRunner(IServiceScopeFactory scopeFactory, SettingsManager settingsManager, AppPaths paths)
        {
            _scopeFactory = scopeFactory;
            _settingsManager = settingsManager;
            _paths = paths;
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            _settingsManager.ValidateRuntimeSettings();
            PlaywrightDeployment.VerifyAssets(_paths.DeploymentDirectory);
            await using var scope = _scopeFactory.CreateAsyncScope();
            var session = scope.ServiceProvider.GetRequiredService<PlaywrightSession>();
            await session.InitializeAsync(smokeTest: true, cancellationToken);
            // Resolve the production pipeline without initializing the authenticated pool.
            _ = scope.ServiceProvider.GetRequiredService<IPipelineRunner>();
            await using var context = await session.Browser.NewContextAsync();
            var page = await context.NewPageAsync();
            await page.GotoAsync("about:blank", new PageGotoOptions { Timeout = 10000 });
            cancellationToken.ThrowIfCancellationRequested();
            var result = await page.EvaluateAsync<int>("() => 6 * 7");
            if (result != 42 || page.Url != "about:blank")
                throw new InvalidOperationException("Browser smoke test did not produce the expected result.");
            Console.WriteLine("SMOKE TEST PASSED: configuration, DI, Playwright driver and headless Chromium (about:blank).");
        }

    }
}
