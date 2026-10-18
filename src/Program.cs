using InvenAdClicker.Models;
using InvenAdClicker.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace InvenAdClicker
{
    class Program
    {
        public static async Task<int> Main(string[] args)
        {
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
                Console.WriteLine("\n종료 요청을 감지했습니다. 안전하게 종료합니다...");
            };

            using IHost host = Host.CreateDefaultBuilder(args)
                .ConfigureLogging(logging =>
                {
                    logging.ClearProviders();
                    logging.AddProvider(new RollingFileLoggerProvider());
                })
                .ConfigureServices((hostContext, services) =>
                {
                    var appSettings = hostContext.Configuration.GetSection("AppSettings").Get<AppSettings>()
                        ?? new AppSettings();
                    services.AddSingleton(appSettings);

                    services.AddSingleton<IAppLogger, MsLoggerAdapter>();
                    services.AddSingleton<Encryption>();
                    services.AddSingleton(provider => ProgressTracker.Instance);
                    services.AddSingleton<SettingsManager>();
                    services.AddSingleton<AppOrchestrator>();
                })
                .Build();

            await host.StartAsync(cts.Token);
            try
            {
                var orchestrator = host.Services.GetRequiredService<AppOrchestrator>();
                return await orchestrator.RunAsync(cts.Token);
            }
            finally
            {
                await host.StopAsync();
            }
        }
    }
}
