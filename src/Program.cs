using InvenAdClicker.Models;
using InvenAdClicker.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace InvenAdClicker
{
    class Program
    {
        internal static IConfigurationRoot BuildConfiguration(string settingsPath) =>
            new ConfigurationBuilder().AddJsonFile(settingsPath, optional: false, reloadOnChange: false).Build();

        public static async Task<int> Main(string[] args)
        {
            bool smokeTest = args.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase);
            using var cts = new CancellationTokenSource();
            if (smokeTest)
                cts.CancelAfter(TimeSpan.FromSeconds(60));
            ConsoleCancelEventHandler cancelHandler = (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
                Console.WriteLine("\n종료 요청을 감지했습니다. 안전하게 종료합니다...");
            };
            Console.CancelKeyPress += cancelHandler;
            string? smokeDirectory = null;
            try
            {
                var paths = smokeTest
                    ? AppPaths.CreateIsolated(AppContext.BaseDirectory, smokeDirectory = Path.Combine(Path.GetTempPath(), "inven-smoke-" + Guid.NewGuid().ToString("N")))
                    : AppPaths.CreateDefault();
                var settingsPath = smokeTest ? paths.SettingsTemplatePath : paths.SettingsFilePath;
                if (!smokeTest)
                {
                    // Repair malformed JSON and migrate old keys before Host loads configuration.
                    using var startupLogging = LoggerFactory.Create(logging =>
                        logging.AddProvider(new RollingFileLoggerProvider(paths)));
                    var startupLogger = new MsLoggerAdapter(startupLogging.CreateLogger<MsLoggerAdapter>());
                    AppDataMigration.Prepare(paths, Directory.GetCurrentDirectory(), startupLogger);
                    new SettingsManager(new AppSettings(),
                        startupLogger, settingsPath)
                        .UpdateSchemaFile();
                }

                using IHost host = new HostBuilder()
                    .ConfigureAppConfiguration(configuration => configuration.AddConfiguration(BuildConfiguration(settingsPath)))
                    .UseContentRoot(AppContext.BaseDirectory)
                    .UseDefaultServiceProvider(options =>
                    {
                        options.ValidateScopes = true;
                        options.ValidateOnBuild = true;
                    })
                    .ConfigureLogging(logging =>
                    {
                        logging.ClearProviders();
                        if (smokeTest)
                            logging.AddSimpleConsole(options => options.SingleLine = true);
                        else
                            logging.AddProvider(new RollingFileLoggerProvider(paths));
                    })
                    .ConfigureServices((hostContext, services) =>
                    {
                        var settings = hostContext.Configuration.GetSection("AppSettings").Get<AppSettings>()
                            ?? new AppSettings();
                        services.AddInvenAdClicker(settings, settingsPath, paths);
                    })
                    .Build();

                await host.StartAsync(cts.Token);
                Exception? executionError = null;
                try
                {
                    if (smokeTest)
                    {
                        await host.Services.GetRequiredService<SmokeTestRunner>().RunAsync(cts.Token);
                        return 0;
                    }
                    return await host.Services.GetRequiredService<AppOrchestrator>().RunAsync(cts.Token);
                }
                catch (Exception ex)
                {
                    executionError = ex;
                    throw;
                }
                finally
                {
                    try { await host.StopAsync(); }
                    catch (Exception ex) when (executionError != null)
                    {
                        Console.Error.WriteLine($"호스트 종료 오류 (최초 실행 오류 유지): {ex}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine(smokeTest ? "SMOKE TEST CANCELLED OR TIMED OUT." : "작업이 취소되었습니다.");
                return 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"{(smokeTest ? "SMOKE TEST FAILED" : "프로그램 실행 실패")}: {ex}");
                return 2;
            }
            finally
            {
                Console.CancelKeyPress -= cancelHandler;
                if (smokeDirectory != null && Directory.Exists(smokeDirectory)) Directory.Delete(smokeDirectory, recursive: true);
            }
        }
    }
}
