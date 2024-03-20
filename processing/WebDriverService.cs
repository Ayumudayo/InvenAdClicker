using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;
using OpenQA.Selenium;
using SeleniumExtras.WaitHelpers;
using InvenAdClicker.helper;

namespace InvenAdClicker.processing
{
    public class WebDriverService : IDisposable
    {
        private ChromeDriver _driver;
        private ChromeDriverService _service;
        private bool disposedValue;

        public WebDriverService()
        {
            AppDomain.CurrentDomain.ProcessExit += (sender, e) => CleanUp();
            Console.CancelKeyPress += (sender, e) =>
            {
                CleanUp();
                e.Cancel = true; // Prevent the process from terminating immediately
            };
        }

        private ChromeOptions GetDriverOptions()
        {
            var options = new ChromeOptions();
            options.AddArguments("--headless", "--incognito");
            return options;
        }

        private ChromeDriverService GetChromeDriverService()
        {
            _service = ChromeDriverService.CreateDefaultService();
            _service.SuppressInitialDiagnosticInformation = true;
            _service.HideCommandPromptWindow = true;

            string logFileName = $"chromedriver_{DateTime.Now:yyyy-MM-dd}.log";
            string logFilePath = System.IO.Path.Combine("logs/ChromeDriver", logFileName);

            _service.LogPath = logFilePath;
            _service.EnableAppendLog = true;

            return _service;
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

        public bool SetupAndLogin(out IWebDriver driver)
        {
            _driver = new ChromeDriver(GetChromeDriverService(), GetDriverOptions());
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
                    CleanUp();
                    driver = null;
                    return false;
                }
                else
                {
                    driver = _driver;
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Exception during login: {ex.Message}");
                CleanUp();
                driver = null;
                return false;
            }
        }

        public void CleanUp()
        {
            _driver?.Quit();
            _driver?.Dispose();
            _service?.Dispose();
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    // TODO: 관리형 상태(관리형 개체)를 삭제합니다.
                    CleanUp();
                }

                // TODO: 비관리형 리소스(비관리형 개체)를 해제하고 종료자를 재정의합니다.
                // TODO: 큰 필드를 null로 설정합니다.
                disposedValue = true;
            }
        }

        // // TODO: 비관리형 리소스를 해제하는 코드가 'Dispose(bool disposing)'에 포함된 경우에만 종료자를 재정의합니다.
        // ~WebDriverService()
        // {
        //     // 이 코드를 변경하지 마세요. 'Dispose(bool disposing)' 메서드에 정리 코드를 입력합니다.
        //     Dispose(disposing: false);
        // }

        public void Dispose()
        {
            // 이 코드를 변경하지 마세요. 'Dispose(bool disposing)' 메서드에 정리 코드를 입력합니다.
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
