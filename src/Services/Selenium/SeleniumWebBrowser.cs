using InvenAdClicker.Config;
using InvenAdClicker.Utils;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;
using SeleniumExtras.WaitHelpers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;

namespace InvenAdClicker.Services.Selenium
{
    public class SeleniumWebBrowser : IDisposable
    {
        private readonly ChromeDriver _driver;
        private readonly ChromeDriverService _service;
        private readonly ILogger _logger;
        private readonly string _loginUrl = "https://member.inven.co.kr/user/scorpio/mlogin";        
        private short _instanceId;
        private int? _browserProcessId = null;

        public SeleniumWebBrowser(AppSettings settings, ILogger logger, Encryption encryption)
        {
            try
            {
                _logger = logger;

                // ChromeDriverService 설정
                _service = ChromeDriverService.CreateDefaultService();
                _service.SuppressInitialDiagnosticInformation = true;  // 초기 진단 메시지 억제
                _service.HideCommandPromptWindow = true;
                _service.LogPath = "NUL";
                _service.EnableVerboseLogging = false;

                // ChromeOptions 설정
                var options = new ChromeOptions();
                options.AddArguments(
                    "--headless",
                    "--incognito",
                    "--disable-extensions",
                    "--disable-gpu",
                    "--no-sandbox",
                    "--disable-dev-shm-usage",
                    "--disable-browser-side-navigation",
                    "--disable-infobars",
                    "--disable-notifications",
                    "--disable-popup-blocking",
                    "--blink-settings=imagesEnabled=false",
                    "--proxy-server='direct://'",
                    "--proxy-bypass-list=*",
                    "--disable-blink-features=AutomationControlled",
                    "--log-level=3",
                    "--disable-logging",
                    "--remote-debugging-port=0" // 임의의 사용 가능한 포트를 사용하도록 설정
                );
                options.AddExcludedArgument("enable-logging");      // 브라우저/CEF 로그 제거
                options.AddExcludedArgument("enable-automation");   // 자동화 경고 제거
                options.AddArgument("--silent");                    // 추가 Silent 모드

                options.AddUserProfilePreference("profile.default_content_setting_values.sound", 2);

                if (settings.DisableImages)
                    options.AddUserProfilePreference(
                        "profile.managed_default_content_settings.images", 2);
                if (settings.DisableCss)
                    options.AddUserProfilePreference(
                        "profile.managed_default_content_settings.stylesheets", 2);
                if (settings.DisableFonts)
                    options.AddUserProfilePreference(
                        "profile.managed_default_content_settings.fonts", 2);

                _driver = new ChromeDriver(
                    _service,
                    options,
                    TimeSpan.FromMilliseconds(settings.CommandTimeoutMilliSeconds));

                // 드라이버의 Capabilities에서 디버거 주소를 가져옴
                var capabilities = _driver.Capabilities;
                var debuggerAddress = capabilities.GetCapability("goog:chromeOptions") as Dictionary<string, object>;
                var debuggerPortStr = debuggerAddress?["debuggerAddress"]?.ToString().Split(':').Last();

                if (!string.IsNullOrEmpty(debuggerPortStr) && int.TryParse(debuggerPortStr, out var debuggerPort))
                {
                    _logger.Info($"Instance #{_instanceId} | Debugger port found: {debuggerPort}");
                    _browserProcessId = GetProcessIdByPort(debuggerPort);
                    if (_browserProcessId.HasValue)
                    {
                        _logger.Info($"Instance #{_instanceId} | Successfully identified chrome.exe PID: {_browserProcessId.Value}");
                    }
                    else
                    {
                        _logger.Warn($"Instance #{_instanceId} | Could not find process using port {debuggerPort}.");
                    }
                }
                else
                {
                    _logger.Warn($"Instance #{_instanceId} | Could not retrieve debugger port from capabilities.");
                }

                encryption.LoadAndValidateCredentials(out var id, out var pw);
                _driver.Navigate().GoToUrl(_loginUrl);

                var wait = new WebDriverWait(_driver, TimeSpan.FromSeconds(settings.IframeTimeoutSeconds));

                wait.Until(ExpectedConditions.ElementExists(By.Id("user_id")));
                wait.Until(ExpectedConditions.ElementExists(By.Id("password")));
                wait.Until(ExpectedConditions.ElementToBeClickable(By.Id("loginBtn")));

                _driver.FindElement(By.Id("user_id")).SendKeys(id);
                _driver.FindElement(By.Id("password")).SendKeys(pw);
                _driver.FindElement(By.Id("loginBtn")).Click();

                // 로그인 성공 또는 실패 판정
                try
                {
                    wait.Until(drv =>
                    {
                        // (A) 로그인 페이지에서 벗어나면 성공
                        if (!drv.Url.StartsWith(_loginUrl, StringComparison.OrdinalIgnoreCase))
                            return true;

                        // (B) 실패 알림 텍스트 확인
                        var notice = drv.FindElement(By.Id("notice"));
                        return notice.Text.Contains("로그인 정보가 일치하지 않습니다.");
                    });
                }
                catch (WebDriverTimeoutException ex)
                {
                    _logger.Error("로그인 결과 확인 타임아웃", ex);
                    throw;
                }

                // 성공 vs 실패 후처리
                if (!_driver.Url.StartsWith(_loginUrl, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.Info("로그인 성공: 페이지 리다이렉트로 확인됨.");
                }
                else
                {
                    var noticeText = _driver.FindElement(By.Id("notice")).Text.Trim();
                    _logger.Error($"Instance #{_instanceId} | 로그인 실패: {noticeText}");

                    if (encryption.EnterCredentials())
                    {
                        Console.WriteLine("새로운 자격증명을 저장했습니다. 프로그램을 다시 실행하세요.");

                        _driver?.Quit();
                        _service?.Dispose();
                        Environment.Exit(0); // 정상 종료
                    }
                    else
                    {
                        Console.WriteLine("자격증명 입력에 실패했습니다. 프로그램을 종료합니다.");
                        throw new ApplicationException("로그인 실패 및 자격증명 입력 실패");
                    }
                }
            }
            catch (Exception)
            {
                _driver?.Quit();
                _service?.Dispose();
                throw;
            }
        }

        public void SetInstanceId(short id)
        {
            _instanceId = id;
        }

        public short InstanceId => _instanceId;

        public IWebDriver Driver => _driver;

        private int? GetProcessIdByPort(int port)
        {
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "netstat.exe",
                        Arguments = "-a -n -o",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    }
                };
                process.Start();
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                // netstat 출력에서 "TCP 0.0.0.0:port ... LISTENING pid" OR "TCP [::]:port ... LISTENING pid" 패턴을 찾음
                string pattern = $@"TCP\s+.*?:{port}\s+.*?LISTENING\s+(\d+)";
                Match match = Regex.Match(output, pattern);

                if (match.Success)
                {
                    return int.Parse(match.Groups[1].Value);
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to get process ID by port {port}: {ex.Message}");
            }
            return null;
        }

        private bool _disposed = false;

        // Dtor 추가
        ~SeleniumWebBrowser()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;
            // Last Resort: 추적된 PID를 직접 강제 종료
            if (_browserProcessId.HasValue)
            {
                try
                {
                    var p = Process.GetProcessById(_browserProcessId.Value);
                    if (p != null && !p.HasExited)
                    {
                        _logger.Warn($"Instance #{_instanceId} | Found zombie chrome.exe PID {_browserProcessId.Value}. Killing it.");
                        p.Kill(true);
                    }
                }
                catch (ArgumentException) { /* 프로세스가 이미 종료됨 */ }
                catch (Exception ex)
                {
                    _logger.Error($"Instance #{_instanceId} | Error during final zombie cleanup for PID {_browserProcessId.Value}: {ex.Message}");
                }
            }

            _disposed = true;

            if (disposing)
            {
                // 관리 리소스 정리
            }

            // 비관리 리소스 정리
            int? serviceProcessId = _service?.ProcessId;

            // 정상 종료 시도
            try
            {
                _driver?.Quit();
                _logger.Info($"Instance #{_instanceId} | Driver.Quit() successful.");
            }
            catch (Exception ex)
            {
                _logger.Warn($"Instance #{_instanceId} | Driver.Quit() failed (might be expected if driver is already unresponsive): {ex.Message.Split('\n').FirstOrDefault()}");
            }
            finally
            {
                _driver?.Dispose();
            }

            // 서비스 Dispose 시도
            try
            {
                _service?.Dispose();
                _logger.Info($"Instance #{_instanceId} | Service.Dispose() successful.");
            }
            catch (Exception ex)
            {
                _logger.Warn($"Instance #{_instanceId} | Service.Dispose() failed: {ex.Message}");
            }

            // Last Resort: 프로세스 트리 강제 종료
            if (serviceProcessId.HasValue && serviceProcessId > 0)
            {
                try
                {
                    var process = Process.GetProcessById(serviceProcessId.Value);
                    if (process != null && !process.HasExited)
                    {
                        _logger.Warn($"Instance #{_instanceId} | Service process {serviceProcessId} is still running. Attempting to kill the process tree.");
                        var taskkill = new Process
                        {
                            StartInfo =
                            {
                                FileName = "taskkill",
                                Arguments = $"/PID {serviceProcessId.Value} /T /F",
                                UseShellExecute = false,
                                CreateNoWindow = true
                            }
                        };
                        taskkill.Start();
                        taskkill.WaitForExit();
                        _logger.Info($"Instance #{_instanceId} | Executed taskkill on PID tree {serviceProcessId}. Exit code: {taskkill.ExitCode}");
                    }
                }
                catch (ArgumentException) 
                {
                    // 프로세스가 이미 종료된 경우
                    _logger.Info($"Instance #{_instanceId} | Service process {serviceProcessId} already exited.");
                }
                catch (Exception ex)
                {
                    _logger.Error($"Instance #{_instanceId} | Failed to execute taskkill on PID {serviceProcessId}: {ex.Message}");
                }
            }
        }
    }
}
