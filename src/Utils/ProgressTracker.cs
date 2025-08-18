using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

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
            if (_map.IsEmpty)
            {
                lock (ConsoleLocker.Lock)
                {
                    Console.WriteLine("대상 URL이 없습니다. 작업을 종료합니다.");
                }
                return;
            }

            int urlW = Math.Max(20, _map.Keys.Max(k => k.Length) + 2);
            int statusW = Enum.GetNames(typeof(ProgressStatus)).Max(s => s.Length) + 2;
            var urls = _map.Keys.OrderBy(k => k).ToList();
            int headerLines = 2;

            lock (ConsoleLocker.Lock)
            {
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

                for (int i = 0; i < urls.Count; i++)
                {
                    PrintProgressLine(urls[i], _map[urls[i]], urlW, statusW, false);
                    Console.WriteLine();
                }
            }

            while (!_shouldStopProgress)
            {
                lock (ConsoleLocker.Lock)
                {
                    for (int i = 0; i < urls.Count; i++)
                    {
                        var url = urls[i];
                        var info = _map[url];

                        lock (info)
                        {
                            Console.SetCursorPosition(0, headerLines + i);
                            PrintProgressLine(url, info, urlW, statusW, true);
                        }
                    }
                }

                if (_map.Values.All(info =>
                    info.Status == ProgressStatus.Finished || info.Status == ProgressStatus.Error || info.Status == ProgressStatus.NoAds))
                {
                    Thread.Sleep(1000);
                    lock (ConsoleLocker.Lock)
                    {
                        Console.SetCursorPosition(0, headerLines + urls.Count);
                        Console.WriteLine();
                        Console.WriteLine("모든 작업이 완료되었습니다!");
                    }
                    break;
                }
                Thread.Sleep(500);
            }
        }

        private void PrintProgressLine(string url, ProgressInfo info, int urlW, int statusW, bool unused)
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

            string line = $"{url.PadRight(urlW)}{info.Status.ToString().PadRight(statusW)}{info.Iteration.ToString().PadRight(8)}{info.TotalAds.ToString().PadRight(8)}{info.ClickedAds.ToString().PadRight(8)}{info.PendingClicks.ToString().PadRight(9)}{info.Errors.ToString().PadRight(6)}{info.Threads}";
            Console.Write(line.Substring(0, Math.Min(Console.WindowWidth, line.Length)));

            Console.ResetColor();
        }
    }
}
