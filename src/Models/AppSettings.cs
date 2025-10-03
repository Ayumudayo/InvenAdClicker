namespace InvenAdClicker.Models
{
    public class AppSettings
    {
        public int MaxDegreeOfParallelism { get; set; } = 3;
        public int IframeTimeoutMilliSeconds { get; set; } = 5000;
        public int ClickDelayMilliseconds { get; set; } = 300;
        public int PageLoadTimeoutMilliseconds { get; set; } = 5000;
        public int CommandTimeoutMilliSeconds { get; set; } = 5000;
        public int PostMessageBufferMilliseconds { get; set; } = 1000;
        public int CollectionAttempts { get; set; } = 1;
        public int MaxClickAttempts { get; set; } = 2;
        public bool PlaywrightHeadless { get; set; } = true;
        public bool PlaywrightJavaScriptEnabled { get; set; } = true;
        public string[]? TargetUrls { get; set; } = new[]
        {
            "https://www.inven.co.kr/",
            "https://m.inven.co.kr/",
            "https://it.inven.co.kr/",
        };
    }
}
