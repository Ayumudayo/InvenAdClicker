using System.Collections.Concurrent;

namespace InvenAdClicker.Utils
{
    public enum ProgressStatus
    {
        Waiting,    // 대기 중
        Collecting, // 수집 중
        Collected,  // 수집 완료
        Clicking,   // 클릭 중
        Finished,   // 클릭 완료
        Error       // 오류 발생
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

        // 내부 상태 저장소
        private readonly ConcurrentDictionary<string, ProgressInfo> _map =
            new ConcurrentDictionary<string, ProgressInfo>();

        private ProgressTracker() { }

        public void Initialize(IEnumerable<string> urls)
        {
            foreach (var url in urls)
            {
                _map.TryAdd(url, new ProgressInfo
                {
                    Status = ProgressStatus.Waiting,
                    PendingClicks = 0
                });
            }
        }

        /// URL별 진행 상태를 업데이트
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
            _map.AddOrUpdate(url,
                key => new ProgressInfo
                {
                    Status = status ?? ProgressStatus.Waiting,
                    Iteration = iterDelta,
                    TotalAds = adsDelta,
                    ClickedAds = clickDelta,
                    Errors = errDelta,
                    Threads = Math.Max(0, threadDelta),
                    PendingClicks = pendingClicksDelta
                },
                (key, old) =>
                {
                    if (status.HasValue) old.Status = status.Value;
                    old.Iteration += iterDelta;
                    old.TotalAds += adsDelta;
                    old.ClickedAds += clickDelta;
                    old.Errors += errDelta;
                    old.Threads = Math.Max(0, old.Threads + threadDelta);
                    old.PendingClicks += pendingClicksDelta;

                    // 클릭 중(pendingClicks==0)이면 자동으로 Finished
                    if (old.Status == ProgressStatus.Clicking
                        && old.PendingClicks <= 0)
                        old.Status = ProgressStatus.Finished;

                    return old;
                });
        }

        public void PrintProgress()
        {
            // 컬럼 너비 계산
            int urlW = Math.Max(20, _map.Keys.Max(k => k.Length) + 2);
            int statusW = Enum.GetNames(typeof(ProgressStatus)).Max(s => s.Length) + 2;

            while (true)
            {
                Console.Clear();
                // 헤더
                Console.WriteLine(
                    "URL".PadRight(urlW)
                  + "Status".PadRight(statusW)
                  + "#Iter".PadRight(8)
                  + "Ads".PadRight(8)
                  + "Clicked".PadRight(8)
                  + "Pending".PadRight(9)
                  + "Err".PadRight(6)
                  + "Thr");
                Console.WriteLine(new string('-', urlW + statusW + 8 + 8 + 8 + 9 + 6 + 4));

                // 각 URL 상태 출력
                foreach (var kv in _map.OrderBy(k => k.Key))
                {
                    var url = kv.Key.PadRight(urlW);
                    var info = kv.Value;

                    // 컬러 지정
                    Console.ForegroundColor = info.Status switch
                    {
                        ProgressStatus.Collecting => ConsoleColor.Yellow,
                        ProgressStatus.Collected => ConsoleColor.Magenta,
                        ProgressStatus.Clicking => ConsoleColor.Green,
                        ProgressStatus.Finished => ConsoleColor.Cyan,
                        ProgressStatus.Error => ConsoleColor.Red,
                        _ => ConsoleColor.DarkGray,
                    };

                    Console.Write(url);
                    Console.Write(info.Status.ToString().PadRight(statusW));
                    Console.Write(info.Iteration.ToString().PadRight(8));
                    Console.Write(info.TotalAds.ToString().PadRight(8));
                    Console.Write(info.ClickedAds.ToString().PadRight(8));
                    Console.Write(info.PendingClicks.ToString().PadRight(9));
                    Console.Write(info.Errors.ToString().PadRight(6));
                    Console.WriteLine(info.Threads);

                    Console.ResetColor();
                }

                Thread.Sleep(1000);
            }
        }
    }
}
