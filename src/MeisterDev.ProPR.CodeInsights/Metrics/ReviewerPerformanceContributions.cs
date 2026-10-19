// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Entities;

namespace MeisterDev.ProPR.CodeInsights.Metrics;

/// <summary>Reuses source/day vectors and cumulative sums across measurement buckets.</summary>
internal sealed class ReviewerPerformanceContributions
{
    private readonly Source[] _sources;

    internal ReviewerPerformanceContributions(IReadOnlyList<ReviewerPerformanceDailyCount> evidence)
    {
        this._sources = evidence.Where(row => !ReviewerPerformanceMeasurement.IsCoverage(row))
            .GroupBy(ReviewerPerformanceSourceKey.Of).Select(group => new Source(group.Key, group)).ToArray();
    }

    internal (ReviewerPerformanceCounts Overall, ReviewerPerformanceCounts Covered) Sum(
        DateOnly from, DateOnly to, HashSet<ReviewerPerformanceSourceKey> coveredSources)
    {
        var overall = default(ReviewerPerformanceCounts);
        var covered = default(ReviewerPerformanceCounts);
        foreach (var source in this._sources)
        {
            var counts = source.Sum(from, to);
            overall += counts;
            if (coveredSources.Contains(source.Key))
            {
                covered += counts;
            }
        }

        return (overall, covered);
    }

    internal bool HasObservation(DateOnly from, DateOnly to) => this._sources.Any(source => source.HasObservation(from, to));

    private sealed class Source
    {
        private readonly DateOnly[] _dates;
        private readonly ReviewerPerformanceCounts[] _daily;
        private readonly ReviewerPerformanceCounts[] _cumulative;

        internal Source(ReviewerPerformanceSourceKey key, IEnumerable<ReviewerPerformanceDailyCount> rows)
        {
            this.Key = key;
            var days = rows.GroupBy(row => row.BucketDate).OrderBy(group => group.Key).ToArray();
            this._dates = new DateOnly[days.Length];
            this._daily = new ReviewerPerformanceCounts[days.Length];
            this._cumulative = new ReviewerPerformanceCounts[days.Length];
            var cumulative = default(ReviewerPerformanceCounts);
            for (var index = 0; index < days.Length; index++)
            {
                var daily = default(ReviewerPerformanceCounts);
                foreach (var row in days[index])
                {
                    daily += ReviewerPerformanceMeasurement.Vector(row);
                }

                cumulative += daily;
                this._dates[index] = days[index].Key;
                this._daily[index] = daily;
                this._cumulative[index] = cumulative;
            }
        }

        internal ReviewerPerformanceSourceKey Key { get; }

        internal bool HasObservation(DateOnly from, DateOnly to)
        {
            var first = this.FirstIndex(from);
            return first < this._dates.Length && this._dates[first] <= to;
        }

        internal ReviewerPerformanceCounts Sum(DateOnly from, DateOnly to)
        {
            var last = Array.BinarySearch(this._dates, to);
            // A missing value returns the bitwise complement of its insertion index.
            // Inverting it and subtracting one selects the last date earlier than the requested end.
            last = last < 0 ? ~last - 1 : last;
            var first = this.FirstIndex(from);
            if (first > last)
            {
                return default;
            }

            if (first == 0)
            {
                return this._cumulative[last];
            }

            var sum = default(ReviewerPerformanceCounts);
            for (var index = first; index <= last; index++)
            {
                sum += this._daily[index];
            }

            return sum;
        }

        private int FirstIndex(DateOnly from)
        {
            var first = Array.BinarySearch(this._dates, from);
            return first < 0 ? ~first : first;
        }
    }
}
