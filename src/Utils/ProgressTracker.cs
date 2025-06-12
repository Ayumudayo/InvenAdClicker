using System.Collections.Concurrent;

namespace InvenAdClicker.Utils
{
    public enum ProgressStatus
    {
        Waiting,
        Collecting,
        Collected,
        NoAds,
        Clicking,
        Finished,
        Error
    }

    public class ProgressInfo
    {
        public ProgressStatus Status { get; set; }
        public int Iteration { get; set; } = 0;
        public int TotalAds { get; set; } = 0;
        public int ClickedAds { get; set; } = 0;
        public int PendingClicks { get; set; } = 0;
        public int Errors { get; set; } = 0;
        public int Threads { get; set; } = 0;
    }

    public class ProgressTracker
    {
        private static readonly Lazy<ProgressTracker> _lazy =
            new Lazy<ProgressTracker>(() => new ProgressTracker());
        public static ProgressTracker Instance => _lazy.Value;

        private readonly ConcurrentDictionary<string, ProgressInfo> _map =
            new ConcurrentDictionary<string, ProgressInfo>();
        private volatile bool _shouldStopProgress = false;

        private ProgressTracker() { }

        public void Initialize(IEnumerable<string> urls)
        {
            foreach (var url in urls)
            {
                _map.TryAdd(url, new ProgressInfo { Status = ProgressStatus.Waiting });
            }
        }

        public void StopProgress() => _shouldStopProgress = true;

        public void Update(
            string url,
            ProgressStatus? status = null,
            int iterDelta = 0,
            int adsDelta = 0,
            int clickDelta = 0,
            int errDelta = 0,
            int threadDelta = 0,
            int pendingClicksDelta = 0)
        {
            var info = _map.GetOrAdd(url, _ => new ProgressInfo { Status = status ?? ProgressStatus.Waiting });

            lock (info)
            {
                if (status.HasValue)
                    info.Status = status.Value;

                info.Iteration += iterDelta;
                info.TotalAds += adsDelta;
                info.ClickedAds += clickDelta;
                info.Errors += errDelta;
                info.Threads = Math.Max(0, info.Threads + threadDelta);
                info.PendingClicks += pendingClicksDelta;

                if (info.Status == ProgressStatus.Clicking && info.PendingClicks <= 0)
                    info.Status = ProgressStatus.Finished;
            }
        }

        public void PrintProgress()
        {
            int urlW = Math.Max(20, _map.Keys.Max(k => k.Length) + 2);
            int statusW = Enum.GetNames(typeof(ProgressStatus)).Max(s => s.Length) + 2;

            // 헤더
            Console.WriteLine(
                "URL".PadRight(urlW) +
                "Status".PadRight(statusW) +
                "#Iter".PadRight(8) +
                "Ads".PadRight(8) +
                "Clicked".PadRight(8) +
                "Pending".PadRight(9) +
                "Err".PadRight(6) +
                "Thrd");
            Console.WriteLine(new string('-', urlW + statusW + 8 + 8 + 8 + 9 + 6 + 4));

            var urls = _map.Keys.OrderBy(k => k).ToList();
            int headerLines = 2;

            // 초기 라인 출력
            for (int i = 0; i < urls.Count; i++)
            {
                PrintProgressLine(urls[i], _map[urls[i]], urlW, statusW);
            }

            while (!_shouldStopProgress)
            {
                for (int i = 0; i < urls.Count; i++)
                {
                    var url = urls[i];
                    var info = _map[url];

                    lock (info)
                    {
                        Console.SetCursorPosition(0, headerLines + i);
                        PrintProgressLine(url, info, urlW, statusW);
                    }
                }

                if (_map.Values.All(info =>
                    info.Status == ProgressStatus.Finished || info.Status == ProgressStatus.Error))
                {
                    Thread.Sleep(1000);
                    Console.WriteLine();
                    Console.WriteLine("모든 작업이 완료되었습니다!");
                    break;
                }
                Thread.Sleep(500);
            }
        }

        private void PrintProgressLine(string url, ProgressInfo info, int urlW, int statusW)
        {
            Console.ForegroundColor = info.Status switch
            {
                ProgressStatus.Collecting => ConsoleColor.Yellow,
                ProgressStatus.Collected => ConsoleColor.Magenta,
                ProgressStatus.Clicking => ConsoleColor.Green,
                ProgressStatus.Finished => ConsoleColor.Cyan,
                ProgressStatus.Error => ConsoleColor.Red,
                _ => ConsoleColor.DarkGray,
            };

            Console.Write(url.PadRight(urlW));
            Console.Write(info.Status.ToString().PadRight(statusW));
            Console.Write(info.Iteration.ToString().PadRight(8));
            Console.Write(info.TotalAds.ToString().PadRight(8));
            Console.Write(info.ClickedAds.ToString().PadRight(8));
            Console.Write(info.PendingClicks.ToString().PadRight(9));
            Console.Write(info.Errors.ToString().PadRight(6));
            Console.Write(info.Threads);

            Console.ResetColor();
            Console.WriteLine();
        }
    }
}
