using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

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

        public DateTime? FirstUpdateUtc { get; set; }
        public DateTime? FirstClickUtc { get; set; }
        public DateTime? LastUpdateUtc { get; set; }

        public double? CompletedCps { get; set; }
    }

    // Owns mutable per-URL counters; consumers only receive detached snapshots.
    internal sealed class ProgressState
    {
        private readonly ConcurrentDictionary<string, ProgressInfo> _map = new();

        public void Initialize(IEnumerable<string> urls)
        {
            _map.Clear();
            foreach (var url in urls)
                _map.TryAdd(url, new ProgressInfo { Status = ProgressStatus.Waiting });
        }

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
                var now = DateTime.UtcNow;
                var prevStatus = info.Status;
                if (status.HasValue)
                    info.Status = status.Value;

                info.Iteration += iterDelta;
                info.TotalAds += adsDelta;
                info.ClickedAds += clickDelta;
                info.Errors += errDelta;
                info.Threads = Math.Max(0, info.Threads + threadDelta);
                info.PendingClicks += pendingClicksDelta;

                info.LastUpdateUtc = now;
                if (!info.FirstUpdateUtc.HasValue)
                    info.FirstUpdateUtc = now;
                if (clickDelta > 0 && !info.FirstClickUtc.HasValue)
                    info.FirstClickUtc = now;

                bool becameFinished = false;
                if (info.Status == ProgressStatus.Clicking && info.PendingClicks <= 0)
                {
                    info.Status = ProgressStatus.Finished;
                    becameFinished = true;
                }
                if (status.HasValue && status.Value == ProgressStatus.Finished && prevStatus != ProgressStatus.Finished)
                {
                    becameFinished = true;
                }

                if (becameFinished && info.FirstClickUtc.HasValue && !info.CompletedCps.HasValue)
                {
                    var elapsedSec = Math.Max(1.0, (now - info.FirstClickUtc.Value).TotalSeconds);
                    info.CompletedCps = info.ClickedAds / elapsedSec;
                }
            }
        }

        public ProgressRowSnapshot[] Snapshot()
        {
            return _map.Select(entry =>
            {
                var info = entry.Value;
                lock (info)
                {
                    return new ProgressRowSnapshot(entry.Key, info.Status, info.Iteration,
                        info.TotalAds, info.ClickedAds, info.PendingClicks, info.Errors,
                        info.Threads, info.CompletedCps, info.FirstClickUtc);
                }
            }).ToArray();
        }
    }

    internal sealed record ProgressRowSnapshot(string Url, ProgressStatus Status, int Iteration,
        int TotalAds, int ClickedAds, int PendingClicks, int Errors, int Threads,
        double? CompletedCps, DateTime? FirstClickUtc)
    {
        public double ClicksPerSecond { get; init; }
    }
}
