using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace InvenAdClicker.Utils
{
    public class ProgressTracker
    {
        private static readonly Lazy<ProgressTracker> _lazy = new(() => new ProgressTracker());
        public static ProgressTracker Instance => _lazy.Value;

        private readonly ProgressState _state = new();
        private volatile bool _shouldStopProgress;

        public ProgressTracker() { }

        public void Initialize(IEnumerable<string> urls)
        {
            _state.Initialize(urls);
            _shouldStopProgress = false;
        }

        public void StopProgress() => _shouldStopProgress = true;

        public void Update(string url, ProgressStatus? status = null, int iterDelta = 0,
            int adsDelta = 0, int clickDelta = 0, int errDelta = 0, int threadDelta = 0,
            int pendingClicksDelta = 0)
        {
            _state.Update(url, status, iterDelta, adsDelta, clickDelta, errDelta,
                threadDelta, pendingClicksDelta);
        }

        public void PrintProgress() => PrintProgressAsync().GetAwaiter().GetResult();

        public Task PrintProgressAsync(CancellationToken cancellationToken = default)
        {
            var renderer = new ConsoleDashboardRenderer();
            return renderer.RunAsync(() => ProgressMetrics.Calculate(_state.Snapshot(), DateTime.UtcNow),
                () => _shouldStopProgress, cancellationToken);
        }
    }
}
