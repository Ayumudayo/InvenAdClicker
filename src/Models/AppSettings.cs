namespace InvenAdClicker.Models
{
    public class AppSettings
    {
        public int MaxDegreeOfParallelism { get; set; } = 3;
        public int IframeTimeoutSeconds { get; set; } = 5;
        public int RetryCount { get; set; } = 1;
        public int ClickDelayMilliseconds { get; set; } = 300;
        public int PageLoadTimeoutMilliseconds { get; set; } = 1000;
        public int CommandTimeoutMilliSeconds { get; set; } = 10000;
        public int CollectionAttempts { get; set; } = 1;
        public bool DisableImages { get; set; } = true;
        public bool DisableCss { get; set; } = true;
        public bool DisableFonts { get; set; } = true;
        public string[]? TargetUrls { get; set; }
        public string BrowserType { get; set; } = "Selenium"; // "Selenium" or "Playwright"
    }
}
