using InvenAdClicker.helper;
using InvenAdClicker.processing;
using OpenQA.Selenium;

namespace InvenAdClicker
{
    class Program
    {
        private static bool isPassed = false;
        public static async Task Main()
        {
            Logger.Info("BEGIN==========================================");
            Logger.Info("Check credential...");
            Console.WriteLine("Check credential...");

            using (Encryption en = new())
            {
                {
                    en.LoadAndValidateCredentials(out string id, out string pw);
                }

                using (WebDriverService wd = new WebDriverService())
                {
                    while (true)
                    {
                        IWebDriver driver = null;
                        if (!wd.SetupAndLogin(out driver))
                        {
                            Logger.Info("Failed to login.");
                            Console.WriteLine("Failed to login. Enter new credential.");
                            en.EnterCredentials();
                        }
                        else
                        {
                            Logger.Info("Login passed.");
                            Console.WriteLine("Login passed.");
                            break;
                        }
                    }
                }

                Thread.Sleep(3000);
            }

            Logger.Info("Initializing...");
            UrlProcessor _pr = new UrlProcessor();
            await _pr.StartProcessing();
            Logger.Info("END============================================");
        }
    }
}