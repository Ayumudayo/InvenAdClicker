namespace InvenAdClicker.Config
{
    public class AppSettings
    {
        public int MaxDegreeOfParallelism { get; set; } = 4;
        public int IframeTimeoutSeconds { get; set; } = 5;
        public int RetryCount { get; set; } = 1;
        public int ClickDelayMilliseconds { get; set; } = 500;
        public bool DisableImages { get; set; } = true;
        public bool DisableCss { get; set; } = true;
        public bool DisableFonts { get; set; } = true;
        public string[]? TargetUrls { get; set; }
    }
}