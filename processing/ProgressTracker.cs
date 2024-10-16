using System;
using System.Collections.Concurrent;
using System.Linq;
using InvenAdClicker.@struct;
using InvenAdClicker.helper;

namespace InvenAdClicker.Processing
{
    public enum ProgressStatus
    {
        Waiting,
        Collecting,
        Collected,
        Clicking,
        Finished,
        Error
    }

    public class ProgressInfo
    {
        public ProgressStatus Status { get; set; }
        public int Iteration { get; set; }
        public int ErrorCount { get; set; }
        public int ThreadCount { get; set; }

        public int TotalAdsCollected { get; set; }
        public int AdsClicked { get; set; }
        public int PendingClicks { get; set; }
    }


    public class ProgressTracker
    {
        private static readonly Lazy<ProgressTracker> _instance = new Lazy<ProgressTracker>(() => new ProgressTracker());
        public static ProgressTracker Instance => _instance.Value;

        private readonly ConcurrentDictionary<string, ProgressInfo> _progressTracker;
        private readonly AppSettings _settings;

        private ProgressTracker()
        {
            _progressTracker = new ConcurrentDictionary<string, ProgressInfo>();
            _settings = SettingsManager.LoadAppSettings();
        }

        public void UpdateProgress(string url, ProgressStatus? status = null, bool incrementIteration = false, bool incrementError = false, int threadCountChange = 0, int adsCollectedChange = 0, int adsClickedChange = 0, int pendingClicksChange = 0)
        {
            _progressTracker.AddOrUpdate(url,
                (key) => new ProgressInfo
                {
                    Status = status ?? ProgressStatus.Waiting,
                    Iteration = 0,
                    ErrorCount = 0,
                    ThreadCount = 0,
                    TotalAdsCollected = 0,
                    AdsClicked = 0,
                    PendingClicks = 0
                },
                (key, oldValue) =>
                {
                    if (status.HasValue) oldValue.Status = status.Value;
                    if (incrementIteration) oldValue.Iteration++;
                    if (incrementError) oldValue.ErrorCount++;
                    oldValue.ThreadCount += threadCountChange;
                    oldValue.ThreadCount = Math.Max(oldValue.ThreadCount, 0);

                    oldValue.TotalAdsCollected += adsCollectedChange;
                    oldValue.AdsClicked += adsClickedChange;
                    oldValue.PendingClicks += pendingClicksChange;

                    // PendingClicks가 0이고 상태가 Clicking이면 Finished로 업데이트
                    if (oldValue.PendingClicks == 0 && oldValue.Status == ProgressStatus.Clicking)
                    {
                        oldValue.Status = ProgressStatus.Finished;
                    }

                    return oldValue;
                });
        }

        public void PrintProgress()
        {
            int urlMaxLength = _progressTracker.Keys.Any() ? _progressTracker.Keys.Max(url => url.Length) + 2 : 20;
            int statusMaxLength = Enum.GetNames(typeof(ProgressStatus)).Max(name => name.Length) + 2;
            int iterationMaxLength = 15;
            int clicksMaxLength = 10;
            int errorCountMaxLength = 10;
            int threadCountMaxLength = 10;

            // 초기 설정
            Console.Clear();
            bool isInitialPrint = true;
            int cursorTop = Console.CursorTop;
            Console.WriteLine("Inven Ad Clicker V3\n");

            while (true)
            {
                if (isInitialPrint)
                {
                    Console.WriteLine($"{"URL".PadRight(urlMaxLength)} {"Status".PadRight(statusMaxLength)} {"Iteration".PadRight(iterationMaxLength)} {"Clicks".PadRight(clicksMaxLength)} {"ErrorCount".PadRight(errorCountMaxLength)} {"ThreadCount".PadRight(threadCountMaxLength)}");
                    Console.WriteLine(new string('-', urlMaxLength + statusMaxLength + iterationMaxLength + clicksMaxLength + errorCountMaxLength + threadCountMaxLength));
                    isInitialPrint = false;
                }

                int currentLine = cursorTop + 4; // 헤더와 구분선 아래부터 시작
                foreach (var entry in _progressTracker)
                {
                    Console.SetCursorPosition(0, currentLine++);
                    string url = entry.Key.PadRight(urlMaxLength);
                    var info = entry.Value;
                    string status = info.Status.ToString().PadRight(statusMaxLength);
                    string iteration = $"{info.Iteration}/{_settings.MaxIter}".PadRight(iterationMaxLength);
                    string clicks = $"{info.AdsClicked}/{info.TotalAdsCollected}".PadRight(clicksMaxLength);
                    string errorCount = info.ErrorCount.ToString().PadRight(errorCountMaxLength);
                    string threadCount = info.ThreadCount.ToString().PadRight(threadCountMaxLength);

                    Console.ForegroundColor = GetColorForStatus(info.Status);
                    Console.WriteLine($"{url} {status} {iteration} {clicks} {errorCount} {threadCount}");
                    Console.ResetColor();
                }

                Thread.Sleep(1000); // 1초 대기
            }
        }

        private static ConsoleColor GetColorForStatus(ProgressStatus status)
        {
            return status switch
            {
                ProgressStatus.Collecting => ConsoleColor.Yellow,
                ProgressStatus.Collected => ConsoleColor.Magenta,
                ProgressStatus.Clicking => ConsoleColor.Green,
                ProgressStatus.Finished => ConsoleColor.Cyan,
                ProgressStatus.Error => ConsoleColor.Red,
                _ => ConsoleColor.DarkGray,
            };
        }
    }
}
