using InvenAdClicker.Helper;
using InvenAdClicker.Processing;
using OpenQA.Selenium;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using InvenAdClicker.helper;

namespace InvenAdClicker
{
    class Program
    {
        private static CancellationTokenSource _cancellationTokenSource;

        public static async Task Main()
        {
            _cancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = _cancellationTokenSource.Token;

            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                _cancellationTokenSource.Cancel();
            };

            Stopwatch programStopwatch = Stopwatch.StartNew(); // 프로그램 전체 시간 측정 시작

            Logger.Info("BEGIN==========================================");
            Logger.Info("Check credential...");
            Console.WriteLine("Check credential...");

            using (Encryption en = new())
            {
                en.LoadAndValidateCredentials(out string id, out string pw);

                bool loginSuccess = false;

                while (!loginSuccess)
                {
                    using (WebDriverService wd = new WebDriverService())
                    {
                        if (!wd.SetupAndLogin(out IWebDriver driver, cancellationToken))
                        {
                            Logger.Info("Failed to login.");
                            Console.WriteLine("Failed to login. Enter new credential.");
                            en.EnterCredentials();
                            en.LoadAndValidateCredentials(out id, out pw);
                        }
                        else
                        {
                            Logger.Info("Login passed.");
                            Console.WriteLine("Login passed.");
                            loginSuccess = true;
                            driver.Quit(); // 로그인 확인 후 브라우저 닫기
                        }
                    }
                }

                await Task.Delay(3000); // Thread.Sleep에서 await Task.Delay로 변경
            }

            Logger.Info("Initializing...");
            UrlProcessor processor = new UrlProcessor();
            await processor.StartProcessingAsync(cancellationToken);
            Logger.Info("END============================================");

            programStopwatch.Stop(); // 프로그램 전체 시간 측정 종료
            var TotalMin = programStopwatch.Elapsed.TotalSeconds / 60;
            var TotalSec = programStopwatch.Elapsed.TotalSeconds % 60;
            Logger.Info($"Total Execution Time: {Math.Round(TotalMin)}Minute {Math.Round(TotalSec)}seconds.");
            Console.WriteLine($"Total Execution Time: {Math.Round(TotalMin)}Minute {Math.Round(TotalSec)}seconds.");
        }
    }
}
