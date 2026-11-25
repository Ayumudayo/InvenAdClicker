using System;
using System.Collections.Generic;
using System.Linq;

namespace InvenAdClicker.Utils
{
    // Aggregates one detached snapshot at a single instant for a consistent frame.
    internal sealed record ProgressMetrics(
        IReadOnlyList<ProgressRowSnapshot> Rows, DateTime CapturedUtc, int TotalAds,
        int ClickedAds, int PendingClicks, int Errors, double AverageClicksPerSecond,
        bool IsComplete)
    {
        public static ProgressMetrics Calculate(IEnumerable<ProgressRowSnapshot> snapshot, DateTime now)
        {
            var rows = snapshot.Select(row => row with
                {
                    ClicksPerSecond = row.Status == ProgressStatus.Finished
                        ? row.CompletedCps ?? 0
                        : row.FirstClickUtc.HasValue
                            ? row.ClickedAds / Math.Max(1, (now - row.FirstClickUtc.Value).TotalSeconds)
                            : 0
                })
                .OrderBy(row => StatusGroup(row.Status))
                .ThenBy(row => row.Url, StringComparer.Ordinal)
                .ToArray();
            var rates = rows.Where(row => row.ClicksPerSecond > 0).Select(row => row.ClicksPerSecond).ToArray();
            return new ProgressMetrics(rows, now, rows.Sum(row => row.TotalAds),
                rows.Sum(row => row.ClickedAds), rows.Sum(row => row.PendingClicks),
                rows.Sum(row => row.Errors), rates.Length > 0 ? rates.Average() : 0,
                rows.All(row => row.Status is ProgressStatus.Finished or ProgressStatus.Error or ProgressStatus.NoAds));
        }

        private static int StatusGroup(ProgressStatus status) => status switch
        {
            ProgressStatus.Collecting or ProgressStatus.Clicking => 0,
            ProgressStatus.Collected or ProgressStatus.Waiting => 1,
            ProgressStatus.Error => 2,
            _ => 3
        };
    }
}
