using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Firefox;
using OpenQA.Selenium.Support.UI;
using OpenQA.Selenium;
using SeleniumExtras.WaitHelpers;
using InvenAdClicker.Helper;
using System;
using System.Collections.Generic;
using System.Threading;
using InvenAdClicker.helper;

namespace InvenAdClicker.Processing
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
            _browserType = "chrome"; // 설정에서 가져오도록 수정 가능
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

            // 기본 설정
            options.AddArguments(
                "--headless",
                "--incognito",
                "--disable-extensions",
                "--disable-gpu",
                "--no-sandbox",
                "--disable-dev-shm-usage",
                "--disable-software-rasterizer",
                "--disable-browser-side-navigation",
                "--disable-infobars",
                "--disable-notifications",
                "--disable-popup-blocking",
                "--blink-settings=imagesEnabled=false",
                "--proxy-server='direct://'",
                "--proxy-bypass-list=*",
                "--disable-blink-features=AutomationControlled"
            );

            // 페이지 로드 전략 설정
            //options.PageLoadStrategy = PageLoadStrategy.Eager;

            // 사용자 프로필 설정
            var prefs = new Dictionary<string, object>
            {
                ["profile.default_content_settings.images"] = 2,
                ["profile.managed_default_content_settings.stylesheets"] = 2,
                ["profile.managed_default_content_settings.fonts"] = 2,
                ["profile.managed_default_content_settings.videos"] = 2,
                ["profile.managed_default_content_settings.audio"] = 2,
                ["profile.managed_default_content_settings.plugins"] = 2,
                ["profile.managed_default_content_settings.svg"] = 2,
                ["profile.managed_default_content_settings.javascript"] = 1,
                ["profile.default_content_settings.cookies"] = 2,
                ["profile.managed_default_content_settings.geolocation"] = 2,
                ["profile.managed_default_content_settings.media_stream"] = 2
            };

            options.AddUserProfilePreference("profile.default_content_settings", prefs);

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

            return _chromeService;
        }

        private FirefoxDriverService GetFirefoxDriverService()
        {
            string geckodriverPath = "C:/geckodriver/geckodriver.exe";

            _firefoxService = FirefoxDriverService.CreateDefaultService(geckodriverPath);
            _firefoxService.SuppressInitialDiagnosticInformation = true;
            _firefoxService.HideCommandPromptWindow = true;

            return _firefoxService;
        }

        private bool IsLoginFailed(IWebDriver driver)
        {
            try
            {
                var noticeDiv = driver.FindElement(By.CssSelector("div#notice[role='tooltip']"));
                var errorMessage = noticeDiv.FindElement(By.CssSelector(".alert.alert-error p"));
                if (errorMessage.Text.Contains("로그인 정보가 일치하지 않습니다."))
                {
                    Logger.Error("Login failed.");
                    return true;
                }
            }
            catch (NoSuchElementException)
            {
                // 오류 메시지 요소를 찾지 못한 경우
                return false;
            }
            return false;
        }

        public bool SetupAndLogin(out IWebDriver driver, CancellationToken cancellationToken)
        {
            try
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
                    _driver.Quit();
                    _driver.Dispose();
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
                _driver?.Quit();
                _driver?.Dispose();
                return false;
            }
            catch (Exception ex)
            {
                Logger.Error($"Exception during login: {ex.Message}");
                driver = null;
                _driver?.Quit();
                _driver?.Dispose();
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
