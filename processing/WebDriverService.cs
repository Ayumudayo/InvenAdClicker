using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Firefox;
using OpenQA.Selenium.Support.UI;
using OpenQA.Selenium;
using SeleniumExtras.WaitHelpers;
using InvenAdClicker.helper;

namespace InvenAdClicker.processing
{
    public class WebDriverService : IDisposable
    {
        private IWebDriver _driver;
        private ChromeDriverService _chromeService;
        private FirefoxDriverService _firefoxService;
        private bool disposedValue;
        private readonly string _browserType;

        public WebDriverService()
        {
            _browserType = "chrome";
            AppDomain.CurrentDomain.ProcessExit += (sender, e) => CleanUp();
            Console.CancelKeyPress += (sender, e) =>
            {
                CleanUp();
                e.Cancel = true;
            };
        }

        private ChromeOptions GetChromeOptions()
        {
            var options = new ChromeOptions();
            options.AddArguments("--headless", "--incognito");
            return options;
        }

        private FirefoxOptions GetFirefoxOptions()
        {
            var options = new FirefoxOptions();
            options.AddArguments("--headless");
            options.BrowserExecutableLocation = "C:/Program Files/Mozilla Firefox/firefox.exe";
            return options;
        }

        private ChromeDriverService GetChromeDriverService()
        {
            _chromeService = ChromeDriverService.CreateDefaultService();
            _chromeService.SuppressInitialDiagnosticInformation = true;
            _chromeService.HideCommandPromptWindow = true;

            //string logFileName = $"chromedriver_{DateTime.Now:yyyy-MM-dd}.log";
            //string logFilePath = System.IO.Path.Combine("logs/ChromeDriver", logFileName);

            //_chromeService.LogPath = logFilePath;
            //_chromeService.EnableAppendLog = true;

            return _chromeService;
        }

        private FirefoxDriverService GetFirefoxDriverService()
        {
            string geckodriverPath = "C:/geckodriver/geckodriver.exe";

            _firefoxService = FirefoxDriverService.CreateDefaultService(geckodriverPath);
            _firefoxService.SuppressInitialDiagnosticInformation = true;
            _firefoxService.HideCommandPromptWindow = true;

            //string logFileName = $"firefoxdriver_{DateTime.Now:yyyy-MM-dd}.log";
            //string logFilePath = System.IO.Path.Combine("logs/FirefoxDriver", logFileName);

            return _firefoxService;
        }

        private bool IsLoginFailed(IWebDriver driver)
        {
            var alertElements = driver.FindElements(By.CssSelector(".alert.alert-error"));
            foreach (var element in alertElements)
            {
                if (element.Text.Contains("로그인 정보가 일치하지 않습니다."))
                {
                    Logger.Error("Login failed.");
                    return true;
                }
            }
            return false;
        }

        public bool SetupAndLogin(out IWebDriver driver, CancellationToken cancellationToken)
        {
            switch (_browserType.ToLower())
            {
                case "chrome":
                    _driver = new ChromeDriver(GetChromeDriverService(), GetChromeOptions());
                    break;
                case "firefox":
                    _driver = new FirefoxDriver(GetFirefoxDriverService(), GetFirefoxOptions());
                    break;
                default:
                    throw new ArgumentException($"Unsupported browser type: {_browserType}");
            }

            try
            {
                WebDriverWait wait = new WebDriverWait(_driver, TimeSpan.FromSeconds(10));
                _driver.Navigate().GoToUrl("https://member.inven.co.kr/user/scorpio/mlogin");
                using (Encryption _en = new Encryption())
                {
                    _en.LoadAndValidateCredentials(out string id, out string pw);
                    wait.Until(ExpectedConditions.ElementToBeClickable(By.Name("user_id"))).SendKeys(id);
                    wait.Until(ExpectedConditions.ElementToBeClickable(By.Name("password"))).SendKeys(pw);
                }
                wait.Until(ExpectedConditions.ElementToBeClickable(By.Id("loginBtn"))).Click();

                if (IsLoginFailed(_driver))
                {
                    Logger.Error("Login failed.");
                    driver = null;
                    return false;
                }
                else
                {
                    driver = _driver;
                    return true;
                }
            }
            catch (OperationCanceledException)
            {
                Logger.Info("Login canceled.");
                driver = null;
                return false;
            }
            catch (Exception ex)
            {
                Logger.Error($"Exception during login: {ex.Message}");
                driver = null;
                return false;
            }
        }

        public void CleanUp()
        {
            _driver?.Quit();
            _driver?.Dispose();

            switch (_browserType.ToLower())
            {
                case "chrome":
                    _chromeService?.Dispose();
                    break;
                case "firefox":
                    _firefoxService?.Dispose();
                    break;
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    CleanUp();
                }
                disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}