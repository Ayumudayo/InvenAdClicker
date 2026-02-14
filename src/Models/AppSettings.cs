using System;

namespace InvenAdClicker.Models
{
    public class PlaywrightDebugOptions
    {
        public bool Enabled { get; set; } = false;
        public bool Headless { get; set; } = true;
        public bool JavaScriptEnabled { get; set; } = true;
        public bool AllowImages { get; set; } = false;
        public bool AllowStylesheets { get; set; } = false;
        public bool AllowFonts { get; set; } = false;
    }

    public class AppSettings
    {
        public int MaxDegreeOfParallelism { get; set; } = 3;
        public bool DryRun { get; set; } = false;
        public int IframeTimeoutMilliSeconds { get; set; } = 5000;
        public int ClickDelayMilliseconds { get; set; } = 300;
        public int PageLoadTimeoutMilliseconds { get; set; } = 5000;
        public int CommandTimeoutMilliSeconds { get; set; } = 5000;
        public int PostMessageBufferMilliseconds { get; set; } = 1000;
        public int CollectionAttempts { get; set; } = 1;
        public int MaxClickAttempts { get; set; } = 2;
        public PlaywrightDebugOptions Debug { get; set; } = new();
        public string[] TargetUrls { get; set; } = Array.Empty<string>();
    }
}
