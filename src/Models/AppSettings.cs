using System;
using System.ComponentModel.DataAnnotations;

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
        [Range(1, 10)]
        public int MaxDegreeOfParallelism { get; set; } = 3;
        public bool DryRun { get; set; } = false;
        [Range(100, int.MaxValue)]
        public int IframeTimeoutMilliseconds { get; set; } = 5000;
        [Range(200, int.MaxValue, ErrorMessage = "AppSettings.ClickDelayMilliseconds: 클릭 간 딜레이는 200ms 이상이어야 합니다.")]
        public int ClickDelayMilliseconds { get; set; } = 300;
        [Range(1000, int.MaxValue)]
        public int PageLoadTimeoutMilliseconds { get; set; } = 5000;
        [Range(1000, int.MaxValue)]
        public int CommandTimeoutMilliseconds { get; set; } = 5000;
        [Range(200, int.MaxValue)]
        public int PostMessageBufferMilliseconds { get; set; } = 1000;
        [Range(1, int.MaxValue)]
        public int CollectionAttempts { get; set; } = 1;
        [Range(1, int.MaxValue)]
        public int MaxClickAttempts { get; set; } = 2;
        [Required]
        public PlaywrightDebugOptions Debug { get; set; } = new();
        [Required]
        public string[] TargetUrls { get; set; } = Array.Empty<string>();
    }
}
