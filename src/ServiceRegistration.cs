using InvenAdClicker.Models;
using InvenAdClicker.Services.Interfaces;
using InvenAdClicker.Services.Pipeline;
using InvenAdClicker.Services.Playwright;
using InvenAdClicker.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using System;
using System.IO;

namespace InvenAdClicker
{
    public static class ServiceRegistration
    {
        public static IServiceCollection AddInvenAdClicker(this IServiceCollection services, AppSettings settings, string? settingsFilePath = null, AppPaths? paths = null)
        {
            paths ??= AppPaths.CreateDefault();
            services.AddSingleton(paths);
            services.AddSingleton(settings);
            services.AddSingleton<IAppLogger, MsLoggerAdapter>();
            services.AddSingleton(provider => new Encryption(provider.GetRequiredService<AppPaths>().CredentialFilePath));
            services.AddSingleton<ProgressTracker>();
            services.AddSingleton(provider => new SettingsManager(settings,
                provider.GetRequiredService<IAppLogger>(),
                settingsFilePath ?? paths.SettingsFilePath));
            services.AddScoped<PlaywrightSession>();
            services.AddScoped<IBrowserPool<IPage>>(provider =>
            {
                var session = provider.GetRequiredService<PlaywrightSession>();
                return new PlaywrightBrowserPool(session.Browser, settings,
                    provider.GetRequiredService<IAppLogger>(),
                    provider.GetRequiredService<Encryption>(), session.LaunchBrowserAsync, session.Abort);
            });
            services.AddScoped<IAdCollector<IPage>, PlaywrightAdCollector>();
            services.AddScoped<IAdClicker<IPage>, PlaywrightAdClicker>();
            services.AddScoped<IPipelineRunner, GenericPipelineRunner<IPage>>();
            services.AddSingleton<AppOrchestrator>();
            services.AddSingleton<SmokeTestRunner>();
            return services;
        }
    }
}
