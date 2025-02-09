using InvenAdClicker.Helper;
using InvenAdClicker.Processing;
using OpenQA.Selenium;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using InvenAdClicker.helper;

namespace InvenAdClicker
{
    class Program
    {
        public static async Task Main()
        {
            using CancellationTokenSource cts = new CancellationTokenSource();
            var cancellationToken = cts.Token;

            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            Stopwatch programStopwatch = Stopwatch.StartNew();

            Logger.Info("BEGIN==========================================");
            Logger.Info("Check credential...");
            Console.WriteLine("Check credential...");

            // 로그인 검증 로직 분리
            bool loginSuccess = await EnsureLoginAsync(cancellationToken);
            if (!loginSuccess)
            {
                Logger.Error("Login process aborted.");
                return;
            }

            // 잠시 대기 후 실제 프로세스 시작
            await Task.Delay(3000, cancellationToken);

            Logger.Info("Initializing...");
            var processor = new UrlProcessor();
            await processor.StartProcessingAsync(cancellationToken);

            Logger.Info("END============================================");
            programStopwatch.Stop();
            double totalSec = programStopwatch.Elapsed.TotalSeconds;
            Logger.Info($"Total Execution Time: {Math.Round(totalSec / 60)}Minute {Math.Round(totalSec % 60)}seconds.");
            Console.WriteLine($"Total Execution Time: {Math.Round(totalSec / 60)}Minute {Math.Round(totalSec % 60)}seconds.");
        }

        private static async Task<bool> EnsureLoginAsync(CancellationToken cancellationToken)
        {
            using Encryption en = new Encryption();
            en.LoadAndValidateCredentials(out string id, out string pw);

            while (true)
            {
                using (WebDriverService wd = new WebDriverService())
                {
                    if (!wd.SetupAndLogin(out IWebDriver driver, cancellationToken))
                    {
                        Logger.Info("Failed to login.");
                        Console.WriteLine("Failed to login. Enter new credentials.");
                        en.EnterCredentials();
                        en.LoadAndValidateCredentials(out id, out pw);
                    }
                    else
                    {
                        Logger.Info("Login passed.");
                        Console.WriteLine("Login passed.");
                        // 로그인 성공 후 브라우저는 바로 종료
                        driver.Quit();
                        return true;
                    }
                }
                await Task.Delay(500, cancellationToken);
            }
        }
    }
}
