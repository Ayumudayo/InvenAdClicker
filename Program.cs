using InvenAdClicker.helper;
using InvenAdClicker.processing;
using OpenQA.Selenium;
using System;
using System.Threading;
using System.Threading.Tasks;

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
                            driver.Quit(); // Close the browser after successful login check
                        }
                    }
                }

                Thread.Sleep(3000);
            }

            Logger.Info("Initializing...");
            UrlProcessor _pr = new UrlProcessor();
            await _pr.StartProcessing(cancellationToken);
            Logger.Info("END============================================");
        }
    }
}
