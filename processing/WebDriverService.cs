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
            // --headless: GUI 없이 브라우저를 백그라운드에서 실행
            // --incognito: 시크릿 모드로 실행, 쿠키와 캐시를 저장하지 않음

            options.AddArgument("--disable-extensions");
            // 모든 Chrome 확장 프로그램을 비활성화하여 리소스 사용 감소

            options.AddArgument("--disable-gpu");
            // GPU 하드웨어 가속을 비활성화, 일부 시스템에서 안정성 향상

            options.AddArgument("--no-sandbox");
            // 샌드박스 보안 기능을 비활성화, 주의: 보안상 위험할 수 있음

            options.AddArgument("--disable-dev-shm-usage");
            // /dev/shm 파티션 사용을 비활성화, 메모리 부족 문제 해결에 도움

            options.AddArgument("--disable-software-rasterizer");
            // 소프트웨어 래스터라이저를 비활성화, 그래픽 처리 최소화

            options.AddArgument("--disable-browser-side-navigation");
            // 브라우저 측 탐색을 비활성화, 안정성 향상

            options.AddArgument("--disable-infobars");
            // 정보 표시줄 비활성화 (예: "Chrome이 자동화된 테스트 소프트웨어에 의해 제어되고 있습니다" 메시지)

            options.AddArgument("--disable-notifications");
            // 웹 알림을 비활성화

            options.AddArgument("--disable-popup-blocking");
            // 팝업 차단을 비활성화, 필요한 경우 팝업 허용

            options.AddUserProfilePreference("profile.default_content_settings.images", 2);
            // 이미지 로딩 비활성화 (2는 차단을 의미)

            options.AddUserProfilePreference("profile.managed_default_content_settings.stylesheets", 2);
            // CSS 스타일시트 로딩 비활성화

            options.AddUserProfilePreference("profile.managed_default_content_settings.fonts", 2);
            // 웹 폰트 로딩 비활성화

            options.AddUserProfilePreference("profile.managed_default_content_settings.videos", 2);
            // 비디오 로딩 비활성화

            options.AddUserProfilePreference("profile.managed_default_content_settings.audio", 2);
            // 오디오 로딩 비활성화

            options.AddUserProfilePreference("profile.managed_default_content_settings.plugins", 2);
            // 플러그인 (예: Flash) 비활성화

            options.AddUserProfilePreference("profile.managed_default_content_settings.svg", 2);
            // SVG 이미지 로딩 비활성화

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