// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;

/// <summary>Aggregate discussion measurements with provider-level dimensions.</summary>
public static class DiscussionSynchronizationMetrics
{
    private static readonly Meter Meter = new("MeisterDEV.ProPR", "1.0.0");
    private static readonly Counter<long> Passes = Meter.CreateCounter<long>("meisterpropr_discussion_sync_passes_total");
    private static readonly Counter<long> Threads = Meter.CreateCounter<long>("meisterpropr_discussion_sync_threads_total");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("meisterpropr_discussion_sync_duration_seconds", "s");
    private static readonly Counter<long> DatabaseCommands = Meter.CreateCounter<long>("meisterpropr_discussion_sync_db_commands_total");
    private static readonly Counter<long> ReaderReadOperations = Meter.CreateCounter<long>("meisterpropr_discussion_sync_db_reader_read_operations_total");
    private static readonly Counter<long> DatabaseRows = Meter.CreateCounter<long>("meisterpropr_discussion_sync_db_rows_total");
    private static readonly Counter<long> ProviderCalls = Meter.CreateCounter<long>("meisterpropr_discussion_provider_calls_total");
    private static readonly Counter<long> ProviderBytes = Meter.CreateCounter<long>("meisterpropr_discussion_provider_response_bytes_total", "By");
    private static readonly Counter<long> UnknownBytes = Meter.CreateCounter<long>("meisterpropr_discussion_provider_unknown_response_size_total");
    private static readonly Histogram<double> ProviderDuration = Meter.CreateHistogram<double>("meisterpropr_discussion_provider_duration_seconds", "s");
    private static readonly AsyncLocal<Measurement?> Current = new();

    public static Measurement Begin(ScmProvider provider) => new(provider);
    public static void RecordDatabaseCommand(bool write) => Current.Value?.Command(write);
    public static void RecordDatabaseRows(long read, long affected) => Current.Value?.Rows(read, affected);

    public static void RecordProviderResponse(ScmProvider provider, double durationSeconds, long? contentLength, string outcome)
    {
        var tags = new TagList { { "provider", provider.ToString().ToLowerInvariant() }, { "outcome", outcome } };
        ProviderCalls.Add(1, tags);
        ProviderDuration.Record(durationSeconds, tags);
        tags.Add("size_source", "response_content_length");
        if (contentLength.HasValue)
        {
            ProviderBytes.Add(contentLength.Value, tags);
        }
        else
        {
            UnknownBytes.Add(1, tags);
        }
    }

    public sealed class Measurement : IDisposable
    {
        private readonly ScmProvider _provider;
        private readonly Measurement? _previous;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _reads, _writes, _rowsRead, _rowsAffected;
        private string _outcome = "failed";

        internal Measurement(ScmProvider provider)
        {
            this._provider = provider;
            this._previous = Current.Value;
            Current.Value = this;
        }

        public void Complete(int changed, int unchanged, string outcome = "completed")
        {
            this._outcome = outcome;
            Threads.Add(changed, new TagList { { "provider", this._provider.ToString().ToLowerInvariant() }, { "state", "changed" } });
            Threads.Add(unchanged, new TagList { { "provider", this._provider.ToString().ToLowerInvariant() }, { "state", "unchanged" } });
        }

        internal void Command(bool write)
        {
            if (write)
            {
                Interlocked.Increment(ref this._writes);
            }
            else
            {
                Interlocked.Increment(ref this._reads);
            }
        }

        internal void Rows(long read, long affected)
        {
            Interlocked.Add(ref this._rowsRead, read);
            Interlocked.Add(ref this._rowsAffected, affected);
        }

        public void Dispose()
        {
            Current.Value = this._previous;
            var tags = new TagList { { "provider", this._provider.ToString().ToLowerInvariant() }, { "outcome", this._outcome } };
            Passes.Add(1, tags);
            Duration.Record(this._clock.Elapsed.TotalSeconds, tags);
            DatabaseCommands.Add(this._reads, new TagList { { "provider", this._provider.ToString().ToLowerInvariant() }, { "operation", "read" } });
            DatabaseCommands.Add(this._writes, new TagList { { "provider", this._provider.ToString().ToLowerInvariant() }, { "operation", "write" } });
            ReaderReadOperations.Add(this._rowsRead, new TagList { { "provider", this._provider.ToString().ToLowerInvariant() } });
            DatabaseRows.Add(this._rowsAffected, new TagList { { "provider", this._provider.ToString().ToLowerInvariant() }, { "operation", "affected" } });
        }
    }
}
