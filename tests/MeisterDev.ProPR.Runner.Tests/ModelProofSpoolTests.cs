// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text.Json;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Runner.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Cryptography;

namespace MeisterDev.ProPR.Runner.Tests;

public sealed class ModelProofSpoolTests
{
    [Fact]
    public async Task LostAcknowledgementKeepsTheBatchFrozenBeforeSendingNewOutcomes()
    {
        var handler = new LostResponseHandler();
        using var http = new HttpClient(handler) { BaseAddress = new("https://control.invalid/") };
        var spool = new JobSpool(http, Guid.NewGuid(), 1, NullLogger<JobSpool>.Instance);
        spool.Add(new RunnerFileOutcome("first.cs", true, false, "first", null, []));
        spool.Add(new RunnerSpendRecord("reviewer", 1, 1, null));
        Assert.False(await spool.FlushAsync(CancellationToken.None));
        spool.Add(new RunnerFileOutcome("second.cs", true, false, "second", null, []));
        spool.Add(new RunnerSpendRecord("reviewer", 2, 2, null));
        Assert.True(await spool.FlushAsync(CancellationToken.None));

        Assert.Equal(new[] { "first.cs", "second.cs" }, handler.Applied.SelectMany(batch => batch.FileResults).Select(file => file.FilePath));
        Assert.Equal(3, handler.Applied.SelectMany(batch => batch.Spend).Sum(spend => spend.InputTokens));
        Assert.Equal(handler.Bodies[0], handler.Bodies[1]);
    }

    [Fact]
    public async Task PayloadAndItemBackpressureSplitsLargeProofWithoutDroppingItsOrdinaryOutcome()
    {
        var handler = new LimitedHandler();
        using var http = new HttpClient(handler) { BaseAddress = new("https://control.invalid/") };
        var id = Guid.NewGuid();
        var spool = new JobSpool(http, id, 1, NullLogger<JobSpool>.Instance);
        var proof = new ModelReviewEvidence
        {
            JobId = id, FilePath = "f.cs", Passes =
            [
                new()
                {
                    PassKey = "a", ModelId = "a",
                    Disposition = ModelGenerationDisposition.Completed, CandidateSetComplete = true, StartedAt = DateTimeOffset.UtcNow.AddSeconds(-1),
                    CompletedAt = DateTimeOffset.UtcNow,
                    Candidates = Enumerable.Range(0, 800).Select(index => new ModelReviewCandidate(index.ToString(), "f.cs", 1, new string('x', 180))).ToArray()
                }
            ]
        };
        Assert.True(proof.IsBounded);
        spool.Add(
            new RunnerFileOutcome(
                "f.cs", true, false, "done", null, [], Comments: [new("f.cs", 1, MeisterDev.ProPR.Domain.Enums.CommentSeverity.Warning, "published")],
                ModelEvidence: proof));
        spool.Add(new RunnerSpendRecord("reviewer", 1, 1, null));

        for (var attempt = 0; attempt < 32 && !await spool.FlushAsync(CancellationToken.None); attempt++)
        {
        }

        Assert.Equal("published", Assert.Single(handler.AcceptedFiles.Where(file => file.IsComplete)).Comments![0].Message);
        Assert.Single(handler.Spend);
        Assert.NotEmpty(handler.Fragments);
        var ordered = handler.Fragments.OrderBy(fragment => fragment.Offset).ToArray();
        var bytes = ordered.SelectMany(fragment => Convert.FromBase64String(fragment.Data)).ToArray();
        Assert.Equal(ordered[0].Digest, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        var retained = JsonSerializer.Deserialize<ModelReviewEvidence>(bytes, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(800, Assert.Single(retained!.Passes).Candidates.Count);
        Assert.All(handler.AcceptedSizes, bytes => Assert.InRange(bytes, 1, 64 * 1024));
    }

    [Fact]
    public async Task IndependentModelCheckpointsAndFrozenManifestUseTheNonDroppableResultChannel()
    {
        var handler = new Handler();
        using var http = new HttpClient(handler) { BaseAddress = new("https://control.invalid/") };
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://host.invalid", "project", "repo", 1, 1);
        var spool = new JobSpool(http, job.Id, 2, NullLogger<JobSpool>.Instance);
        IReviewFileResultStore store = new SpoolingFileResultStore(job, spool);
        var file = new ReviewFileResult(job.Id, "f.cs");
        file.RecordModelEvidence(
            new()
            {
                JobId = job.Id, FilePath = "f.cs", Attempt = 1,
                Passes =
                [
                    new()
                    {
                        PassKey = "a", ModelId = "a", Disposition = ModelGenerationDisposition.Completed,
                        CandidateSetComplete = true, StartedAt = DateTimeOffset.UtcNow.AddSeconds(-1), CompletedAt = DateTimeOffset.UtcNow
                    }
                ]
            });
        await store.AddFileResultAsync(file);
        await store.RecordModelManifestAsync(job.Id, 1, new() { JobId = job.Id, Attempt = 1, Files = ["f.cs"], InventoryComplete = true });
        Assert.True(await spool.FlushAsync(CancellationToken.None));

        Assert.NotNull(handler.Batch);
        var retained = Assert.Single(handler.Batch.FileResults).ModelEvidence;
        Assert.NotNull(retained);
        Assert.True(retained.Passes[0].IsCertified);
        Assert.True(Assert.Single(handler.Batch.ModelManifests!).InventoryComplete);
        Assert.Empty(handler.Batch.Events);
    }

    private sealed class LostResponseHandler : HttpMessageHandler
    {
        internal List<RunnerIngestBatch> Applied { get; } = [];
        internal List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            this.Bodies.Add(body);
            var batch = JsonSerializer.Deserialize<RunnerIngestBatch>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            if (this.Applied.All(existing => existing.Sequence != batch.Sequence))
            {
                this.Applied.Add(batch);
            }

            if (this.Bodies.Count == 1)
            {
                throw new HttpRequestException("Response lost after retention.");
            }

            return new(HttpStatusCode.OK);
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        internal RunnerIngestBatch? Batch { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.Batch = JsonSerializer.Deserialize<RunnerIngestBatch>(
                await request.Content!.ReadAsStringAsync(cancellationToken), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return new(HttpStatusCode.OK);
        }
    }

    private sealed class LimitedHandler : HttpMessageHandler
    {
        internal List<RunnerFileOutcome> AcceptedFiles { get; } = [];
        internal List<ModelReviewProofFragment> Fragments { get; } = [];
        internal List<RunnerSpendRecord> Spend { get; } = [];
        internal List<int> AcceptedSizes { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var bytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            var batch = JsonSerializer.Deserialize<RunnerIngestBatch>(bytes, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            if (bytes.Length > 64 * 1024 || batch.ItemCount > 1)
            {
                return new(HttpStatusCode.RequestEntityTooLarge);
            }

            this.AcceptedSizes.Add(bytes.Length);
            this.AcceptedFiles.AddRange(batch.FileResults);
            this.Fragments.AddRange(batch.FileResults.Where(file => file.ModelEvidenceFragment is not null).Select(file => file.ModelEvidenceFragment!));
            this.Spend.AddRange(batch.Spend);
            return new(HttpStatusCode.OK);
        }
    }
}
