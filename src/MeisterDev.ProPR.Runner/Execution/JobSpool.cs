// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Security.Cryptography;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Runner.Contracts;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Runner.Execution;

/// <summary>
///     Everything one job produces on its way back to the control plane, held until it can be sent.
///     <para>
///         A review emits far more than its findings: a trace event per stage, a result per file, and a
///         spend record per completion. Sending each one as it happens would make a review's latency the
///         sum of its network round trips, and would lose whatever was in flight when the control plane
///         became unreachable. Batching is what lets a short outage be survived.
///     </para>
///     <para>
///         Bounded on purpose. An unbounded buffer turns a long outage into memory exhaustion, so past the
///         ceiling the oldest trace events are dropped and counted. File results and spend are never
///         dropped: they are the review's output and its cost, where a trace event is a description of how
///         it got there. The count is reported so a trace with gaps reports them rather than reading as
///         complete.
///     </para>
///     <para>
///         The sequence advances on acknowledgement, not on attempt. The ledger requires contiguity, so a
///         batch that failed in transit must be resent under its own number. Incrementing per attempt leaves
///         a permanent gap, after which every later batch is refused as out of order for the rest of the
///         job.
///     </para>
/// </summary>
public sealed partial class JobSpool(
    HttpClient http,
    Guid jobId,
    int leaseGeneration,
    ILogger<JobSpool> logger)
{
    /// <summary>
    ///     How many buffered items trigger a flush. Small enough that an outage loses little, large enough
    ///     that a file-by-file review is not one request per stage.
    /// </summary>
    private const int FlushThreshold = 50;

    private readonly ConcurrentQueue<RunnerTraceEvent> _events = new();
    private readonly ConcurrentQueue<RunnerFileOutcome> _fileResults = new();
    private readonly ConcurrentQueue<RunnerSpendRecord> _spend = new();
    private readonly ConcurrentQueue<ModelReviewManifest> _modelManifests = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);

    /// <summary>
    ///     How many buffered items may accumulate before the oldest trace events are dropped. Set high,
    ///     because the ordinary case is a short outage. The ceiling is here so a long outage cannot exhaust
    ///     memory.
    /// </summary>
    private const int MaxBufferedItems = 5000;

    /// <summary>The last sequence the control plane acknowledged. The next batch is this plus one.</summary>
    private int _acknowledgedSequence;

    private List<object>? _unacknowledgedBatch;

    /// <summary>Trace events dropped at the ceiling, so a shortened trace can report the loss.</summary>
    private int _droppedEvents;

    /// <summary>
    ///     Set when the control plane says this job is no longer ours to write to. Retrying then is not
    ///     backpressure, it is a loop: the answer will not change, and every attempt costs a round trip.
    /// </summary>
    private bool _refusedForGood;

    /// <summary>Buffers one trace event.</summary>
    /// <param name="name">Event name, matching what the in-process path records.</param>
    /// <param name="details">Structured detail, already serialised.</param>
    /// <param name="occurredAt">When it happened.</param>
    public void Add(string name, string? details, DateTimeOffset occurredAt)
    {
        this._events.Enqueue(new RunnerTraceEvent(occurredAt, name, details));
    }

    /// <summary>Buffers one file's outcome, which is also this job's resume checkpoint.</summary>
    /// <param name="outcome">The outcome.</param>
    public void Add(RunnerFileOutcome outcome)
    {
        this._fileResults.Enqueue(outcome);
    }

    /// <summary>Buffers what one relayed completion cost.</summary>
    /// <param name="spend">The spend record.</param>
    public void Add(RunnerSpendRecord spend)
    {
        this._spend.Enqueue(spend);
    }

    /// <summary>Buffers a frozen inventory through the non-droppable checkpoint channel.</summary>
    public void Add(ModelReviewManifest manifest) => this._modelManifests.Enqueue(manifest);

    /// <summary>Whether enough has accumulated to be worth a round trip.</summary>
    public bool ShouldFlush =>
        this._unacknowledgedBatch is not null ||
        this._events.Count + this._fileResults.Count + this._spend.Count + this._modelManifests.Count >= FlushThreshold;

    /// <summary>
    ///     Whether the control plane has refused this job for good, so there is nothing left to flush to.
    /// </summary>
    public bool RefusedForGood => this._refusedForGood;

    /// <summary>Trace events dropped because the buffer reached its ceiling.</summary>
    public int DroppedEvents => this._droppedEvents;

    /// <summary>The job whose output this spool carries.</summary>
    public Guid JobId => jobId;

    /// <summary>
    ///     Sends everything buffered, in one batch, and keeps it buffered if the send fails.
    ///     <para>
    ///         Items are only dropped once the control plane has acknowledged them. A flush that throws
    ///         away its batch on a transport failure would lose exactly the trace an operator needs to
    ///         understand why the review was interrupted.
    ///     </para>
    /// </summary>
    /// <param name="ct">The cancellation token.</param>
    public async Task<bool> FlushAsync(CancellationToken ct)
    {
        await this._flushGate.WaitAsync(ct);
        var pending = new List<object>();
        try
        {
            pending.AddRange(Drain(this._events));
            pending.AddRange(Drain(this._fileResults));
            pending.AddRange(Drain(this._spend));
            pending.AddRange(Drain(this._modelManifests));
            if (this._unacknowledgedBatch is { } retry)
            {
                pending.InsertRange(0, retry);
            }

            while (pending.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var batch = this._unacknowledgedBatch ?? this.TakeBoundedBatch(pending);
                var sequence = this._acknowledgedSequence + 1;
                var request = new
                {
                    jobId, leaseGeneration, contractVersion = RunnerContractVersion.Current, sequence,
                    idempotencyKey = $"{jobId:N}-{sequence}",
                    events = batch.OfType<RunnerTraceEvent>().ToArray(),
                    fileResults = batch.OfType<RunnerFileOutcome>().ToArray(),
                    spend = batch.OfType<RunnerSpendRecord>().ToArray(),
                    modelManifests = batch.OfType<ModelReviewManifest>().ToArray(),
                };
                try
                {
                    // A lost response can follow a successful server commit. Its sequence must retain the same payload.
                    this._unacknowledgedBatch = batch;
                    using var response = await http.PostAsJsonAsync("ingest", request, ct);
                    if (response.IsSuccessStatusCode)
                    {
                        this._acknowledgedSequence = sequence;
                        this._unacknowledgedBatch = null;
                        pending.RemoveRange(0, batch.Count);
                        continue;
                    }

                    if (response.StatusCode == HttpStatusCode.RequestEntityTooLarge)
                    {
                        this._unacknowledgedBatch = null;
                        if (batch.Count > 1)
                        {
                            this._maximumBatchItems = Math.Max(1, batch.Count / 2);
                            continue;
                        }

                        if (TrySplitOptionalProof(batch[0], out var split))
                        {
                            pending.RemoveAt(0);
                            pending.InsertRange(0, split);
                            continue;
                        }
                    }

                    LogFlushRefused(logger, jobId, sequence, (int)response.StatusCode);
                    if (response.StatusCode == HttpStatusCode.Conflict)
                    {
                        var conflict = await ReadConflictAsync(response, ct);
                        if (conflict.LeaseLost)
                        {
                            this._refusedForGood = true;
                        }
                        else if (conflict.ExpectedSequence is { } expected)
                        {
                            this._acknowledgedSequence = expected - 1;
                        }
                    }
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    LogFlushFailed(logger, jobId, sequence, exception.GetType().Name);
                }

                return false;
            }

            return true;
        }
        finally
        {
            if (this._unacknowledgedBatch is { } retry && pending.Count >= retry.Count)
            {
                pending.RemoveRange(0, retry.Count);
            }

            Requeue(this._events, pending.OfType<RunnerTraceEvent>().ToList());
            Requeue(this._fileResults, pending.OfType<RunnerFileOutcome>().ToList());
            Requeue(this._spend, pending.OfType<RunnerSpendRecord>().ToList());
            Requeue(this._modelManifests, pending.OfType<ModelReviewManifest>().ToList());
            this.TrimToCeiling();
            this._flushGate.Release();
        }
    }

    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);
    private int _maximumBatchItems = 500;

    private List<object> TakeBoundedBatch(List<object> pending)
    {
        const int maximumBytes = 8 * 1024 * 1024;
        var batch = new List<object>();
        var bytes = 1024;
        foreach (var item in pending.Take(this._maximumBatchItems))
        {
            var size = JsonSerializer.SerializeToUtf8Bytes(item, item.GetType(), WireJson).Length;
            if (batch.Count > 0 && bytes + size > maximumBytes)
            {
                break;
            }

            batch.Add(item);
            bytes += size;
        }

        return batch;
    }

    private static bool TrySplitOptionalProof(object item, out IReadOnlyList<object> split)
    {
        var items = new List<object>();
        if (item is RunnerFileOutcome { ModelEvidence: { } proof } file)
        {
            var ordinary = file with { ModelEvidence = null };
            if (file.IsComplete || file.IsFailed || file.IsExcluded || file.Summary is not null || file.ErrorMessage is not null
                || file.Comments?.Count > 0 || file.WithheldFindings?.Count > 0 || file.ReviewedPassKeys.Count > 0)
            {
                items.Add(ordinary);
            }

            var bounded = proof.BoundedOrUnknown() with { PendingFragments = [], RetainedTransportDigests = [] };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(bounded, WireJson);
            if (bytes.Length > ModelReviewProofFragment.MaximumProofBytes)
            {
                bounded = new() { JobId = proof.JobId, FilePath = file.FilePath, UnavailableReason = "model-proof-limit-exceeded" };
                bytes = JsonSerializer.SerializeToUtf8Bytes(bounded, WireJson);
            }

            var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
            const int chunkBytes = ModelReviewProofFragment.MaximumDataCharacters / 4 * 3;
            for (var offset = 0; offset < bytes.Length; offset += chunkBytes)
            {
                var fragment = new ModelReviewProofFragment(
                    digest, offset, bytes.Length,
                    Convert.ToBase64String(bytes, offset, Math.Min(chunkBytes, bytes.Length - offset)));
                items.Add(new RunnerFileOutcome(file.FilePath, false, false, null, null, [], ModelEvidenceFragment: fragment));
            }
        }
        else if (item is ModelReviewManifest manifest && (manifest.Files.Count > 0 || manifest.AllowedModels.Count > 0 || manifest.PullRequestPasses.Count > 0))
        {
            // The control plane already retained the dispatched inventory and model bindings.
            items.Add(manifest with { Files = [], AllowedModels = [], PullRequestPasses = [], InventoryComplete = false });
            items.AddRange(
                manifest.PullRequestPasses.Select(proof =>
                    new RunnerFileOutcome(proof.FilePath, false, false, null, null, [], ModelEvidence: proof)));
        }

        split = items;
        return items.Count > 0;
    }

    /// <summary>
    ///     Reads what a 409 actually said. The ingest conflict carries an expected sequence; a lease refusal
    ///     carries a contract error code instead, and the difference decides whether resending is sensible.
    /// </summary>
    private static async Task<ConflictAnswer> ReadConflictAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = document.RootElement;

            if (root.TryGetProperty("code", out var code)
                && code.GetString() is RunnerContractError.LeaseNotHeld or RunnerContractError.RegistrationRevoked)
            {
                return new ConflictAnswer(true, null);
            }

            return root.TryGetProperty("expectedSequence", out var expected)
                   && expected.TryGetInt32(out var value)
                ? new ConflictAnswer(false, value)
                : new ConflictAnswer(false, null);
        }
        catch (JsonException)
        {
            // An answer this client cannot read is not an answer to act on; the batch stays buffered and
            // the next attempt asks again.
            return new ConflictAnswer(false, null);
        }
    }

    /// <summary>
    ///     Drops the oldest trace events once the buffer is past its ceiling, and counts what went. File
    ///     results and spend are never dropped.
    /// </summary>
    private void TrimToCeiling()
    {
        var total = this._events.Count + this._fileResults.Count + this._spend.Count + this._modelManifests.Count + (this._unacknowledgedBatch?.Count ?? 0);
        while (total > MaxBufferedItems && this._events.TryDequeue(out _))
        {
            this._droppedEvents++;
            total--;
        }
    }

    private static List<T> Drain<T>(ConcurrentQueue<T> queue)
    {
        var drained = new List<T>(queue.Count);
        while (queue.TryDequeue(out var item))
        {
            drained.Add(item);
        }

        return drained;
    }

    private static void Requeue<T>(ConcurrentQueue<T> queue, List<T> items)
    {
        var later = Drain(queue);
        foreach (var item in items.Concat(later))
        {
            queue.Enqueue(item);
        }
    }

    private readonly record struct ConflictAnswer(bool LeaseLost, int? ExpectedSequence);

    [LoggerMessage(EventId = 6301, Level = LogLevel.Warning, Message = "Batch {Sequence} for job {JobId} was refused with {StatusCode}; it stays buffered")]
    private static partial void LogFlushRefused(ILogger logger, Guid jobId, int sequence, int statusCode);

    [LoggerMessage(EventId = 6302, Level = LogLevel.Warning, Message = "Batch {Sequence} for job {JobId} could not be shipped and stays buffered: {Reason}")]
    private static partial void LogFlushFailed(ILogger logger, Guid jobId, int sequence, string reason);
}
