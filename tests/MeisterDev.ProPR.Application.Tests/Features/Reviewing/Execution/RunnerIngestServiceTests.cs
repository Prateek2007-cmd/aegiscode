// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using System.Text.Json;
using MeisterDev.ProPR.Application.Options;
using Microsoft.Extensions.Options;
using NSubstitute;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Tests.Features.Reviewing.Execution;

public sealed class RunnerIngestServiceTests
{
    [Theory]
    [InlineData("42")]
    [InlineData("{\"digest\":null,\"offset\":0,\"totalBytes\":3,\"data\":null}")]
    [InlineData("{\"digest\":\"bad\",\"offset\":-1,\"totalBytes\":4,\"data\":\"AQIDBA==\"}")]
    public void InvalidOptionalFragmentCannotDiscardAnOrdinaryOutcome(string fragment)
    {
        var json = "{\"filePath\":\"f.cs\",\"isComplete\":true,\"isFailed\":false,\"reviewedPassKeys\":[],\"modelEvidenceFragment\":" + fragment + "}";
        RunnerFileOutcome? outcome = null;
        Assert.Null(
            Record.Exception(() => outcome = JsonSerializer.Deserialize<RunnerFileOutcome>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
        Assert.True(outcome!.IsComplete);
        Assert.NotNull(outcome.ModelEvidenceFragment);
        Assert.False(outcome.ModelEvidenceFragment.IsBounded);
    }

    [Theory]
    [InlineData("42")]
    [InlineData("[null]")]
    [InlineData("[{\"pullRequestPasses\":[{\"passes\":[{\"passKey\":\"p\",\"disposition\":\"not-an-enum\"}]}]}]")]
    public void MalformedManifestCannotDiscardOtherwiseValidRunnerFileResults(string manifests)
    {
        var json =
            "{\"sequence\":1,\"idempotencyKey\":\"b\",\"events\":[],\"spend\":[],\"fileResults\":[{\"filePath\":\"f.cs\",\"isComplete\":true,\"isFailed\":false,\"reviewedPassKeys\":[]}],\"modelManifests\":" +
            manifests + "}";
        RunnerIngestBatch? batch = null;
        Assert.Null(Record.Exception(() => batch = JsonSerializer.Deserialize<RunnerIngestBatch>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
        Assert.True(Assert.Single(batch!.FileResults).IsComplete);
        Assert.Equal("model-proof-limit-exceeded", Assert.Single(batch.ModelManifests!).UnavailableReason);
    }

    [Theory]
    [InlineData("42")]
    [InlineData("{\"passes\":42}")]
    [InlineData("{\"passes\":[null]}")]
    public void MalformedOptionalProofCannotDiscardOtherwiseValidRunnerFindings(string proof)
    {
        var json =
            "{\"filePath\":\"a.cs\",\"isComplete\":true,\"isFailed\":false,\"summary\":\"done\",\"reviewedPassKeys\":[],\"comments\":[{\"filePath\":\"a.cs\",\"lineNumber\":1,\"severity\":1,\"message\":\"finding\"}],\"modelEvidence\":" +
            proof + "}";
        RunnerFileOutcome? result = null;
        Assert.Null(
            Record.Exception(() => result = JsonSerializer.Deserialize<RunnerFileOutcome>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
        Assert.Equal("finding", Assert.Single(result!.Comments!).Message);
        Assert.NotNull(result.ModelEvidence);
        Assert.Equal("model-proof-limit-exceeded", result.ModelEvidence.UnavailableReason);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ModelProofIsRetainedBeforeReceiptAndRetriedEvenWhenAnEarlierReceiptExists(int expected)
    {
        var service = this.CreateService(expectedSequence: expected);
        var batch = Batch(1) with { FileResults = [new("a.cs", false, false, null, null, [], ModelEvidence: new() { JobId = JobId })] };
        await service.IngestAsync(Call, batch);
        await this._writer.Received(1).WriteModelEvidenceAsync(
            Call, batch.FileResults, Arg.Any<IReadOnlyList<ModelReviewManifest>>(), Arg.Any<CancellationToken>());
        if (expected == 1)
        {
            Received.InOrder(() =>
            {
                this._writer.WriteModelEvidenceAsync(Call, batch.FileResults, Arg.Any<IReadOnlyList<ModelReviewManifest>>(), Arg.Any<CancellationToken>());
                this._ledger.TryRecordAsync(JobId, 1, batch.IdempotencyKey, Arg.Any<CancellationToken>());
            });
        }
    }

    private static readonly Guid JobId = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly RunnerCallContext Call = new(JobId, 1, "runner-a");

    private readonly IRunnerCallAuthorizer _authorizer = Substitute.For<IRunnerCallAuthorizer>();
    private readonly IRunnerIngestLedger _ledger = Substitute.For<IRunnerIngestLedger>();
    private readonly IRunnerIngestWriter _writer = Substitute.For<IRunnerIngestWriter>();

    private static RunnerIngestBatch Batch(int sequence, string key = "batch", int events = 1)
    {
        return new RunnerIngestBatch(
            sequence,
            key,
            [
                .. Enumerable.Range(0, events).Select(i =>
                    new RunnerTraceEvent(DateTimeOffset.UtcNow, $"event_{i}", null))
            ],
            [],
            []);
    }

    private RunnerIngestService CreateService(
        bool authorized = true,
        int expectedSequence = 1,
        bool recordWins = true,
        int maxItems = 500)
    {
        this._authorizer.AuthorizeAsync(Arg.Any<RunnerCallContext>(), Arg.Any<CancellationToken>())
            .Returns(
                authorized
                    ? RunnerCallAuthorization.Allow(Guid.NewGuid())
                    : RunnerCallAuthorization.Refuse(RunnerCallRefusal.SupersededGeneration));
        this._ledger.GetExpectedSequenceAsync(JobId, Arg.Any<CancellationToken>()).Returns(expectedSequence);
        this._ledger.TryRecordAsync(JobId, Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(recordWins);
        this._writer.WriteModelEvidenceAsync(
            Arg.Any<RunnerCallContext>(), Arg.Any<IReadOnlyList<RunnerFileOutcome>>(),
            Arg.Any<IReadOnlyList<ModelReviewManifest>>(), Arg.Any<CancellationToken>()).Returns(true);

        return new RunnerIngestService(
            this._authorizer,
            this._ledger,
            this._writer,
            Microsoft.Extensions.Options.Options.Create(new RunnerIngestOptions { MaxItemsPerBatch = maxItems }));
    }

    [Fact]
    public async Task AnOlderCheckpointPayload_RetriesWithTheSameSequenceIdentity()
    {
        var payload = """
                      {"sequence":1,"idempotencyKey":"older-batch","events":[],"spend":[],"fileResults":[
                       {"filePath":"src/a.cs","isComplete":true,"isFailed":false,"summary":"Summary","reviewedPassKeys":[],"comments":[]}]}
                      """;
        var batch = JsonSerializer.Deserialize<RunnerIngestBatch>(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var service = this.CreateService();
        this._ledger.GetExpectedSequenceAsync(JobId, Arg.Any<CancellationToken>()).Returns(1, 2);

        var first = await service.IngestAsync(Call, batch);
        var replay = await service.IngestAsync(Call, batch);

        Assert.Equal(RunnerIngestOutcome.Applied, first.Outcome);
        Assert.Equal(RunnerIngestOutcome.AlreadyApplied, replay.Outcome);
        await this._writer.Received(1).WriteFileResultsAsync(
            JobId,
            Arg.Is<IReadOnlyList<RunnerFileOutcome>>(rows => rows.Count == 1 && rows[0].WithheldFindings == null), Arg.Any<CancellationToken>());
        await this._ledger.Received(1).TryRecordAsync(JobId, 1, "older-batch", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheNextBatchInSequence_IsApplied()
    {
        var result = await this.CreateService(expectedSequence: 3).IngestAsync(Call, Batch(3));

        Assert.Equal(RunnerIngestOutcome.Applied, result.Outcome);
        Assert.Equal(4, result.ExpectedSequence);
        await this._writer.Received(1).WriteEventsAsync(JobId, Arg.Any<IReadOnlyList<RunnerTraceEvent>>(), Arg.Any<CancellationToken>());
    }

    // A resend after a network failure is the most ordinary thing that happens to an executor. Answering it
    // as an error would make every executor carry error handling for the normal case.
    [Fact]
    public async Task AResendOfAnAppliedBatch_IsSuccessWithNothingWrittenAgain()
    {
        var result = await this.CreateService(expectedSequence: 5).IngestAsync(Call, Batch(3));

        Assert.Equal(RunnerIngestOutcome.AlreadyApplied, result.Outcome);
        Assert.True(result.MaySpoolBeTrimmed);
        await this._writer.DidNotReceive().WriteEventsAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<RunnerTraceEvent>>(), Arg.Any<CancellationToken>());
    }

    // Idempotency alone does not give ordering, and a trace with a gap in it cannot be read, so the gap is
    // named and the executor is told where to resume from.
    [Fact]
    public async Task ABatchThatSkipsAhead_IsRefusedAndTheGapIsNamed()
    {
        var result = await this.CreateService(expectedSequence: 4).IngestAsync(Call, Batch(7));

        Assert.Equal(RunnerIngestOutcome.OutOfOrder, result.Outcome);
        Assert.Equal(4, result.ExpectedSequence);
        Assert.False(result.MaySpoolBeTrimmed);
        await this._writer.DidNotReceive().WriteEventsAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<RunnerTraceEvent>>(), Arg.Any<CancellationToken>());
    }

    // Two deliveries of the same batch arriving together both try to record it; the one that loses the
    // uniqueness race must not also write, or the trace doubles.
    [Fact]
    public async Task WhenTwoDeliveriesRace_OnlyTheWinnerWrites()
    {
        var result = await this.CreateService(recordWins: false).IngestAsync(Call, Batch(1));

        Assert.Equal(RunnerIngestOutcome.AlreadyApplied, result.Outcome);
        await this._writer.DidNotReceive().WriteEventsAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<RunnerTraceEvent>>(), Arg.Any<CancellationToken>());
    }

    // Refused whole rather than partly applied: a half-applied batch leaves the executor unable to say what
    // still needs sending, which the sequence exists to prevent.
    [Fact]
    public async Task AnOversizedBatch_IsRefusedWholeWithTheExpectedSequenceToResumeFrom()
    {
        var result = await this.CreateService(expectedSequence: 2, maxItems: 3)
            .IngestAsync(Call, Batch(2, events: 10));

        Assert.Equal(RunnerIngestOutcome.TooLarge, result.Outcome);
        Assert.Equal(2, result.ExpectedSequence);
        await this._writer.DidNotReceive().WriteEventsAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<RunnerTraceEvent>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnUnauthorizedBatch_IsNotApplied()
    {
        var result = await this.CreateService(authorized: false).IngestAsync(Call, Batch(1));

        Assert.Equal(RunnerIngestOutcome.NotAuthorized, result.Outcome);
        Assert.Equal(RunnerCallRefusal.SupersededGeneration, result.CallRefusal);
        await this._ledger.DidNotReceive().TryRecordAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EmptySectionsOfABatch_AreNotWrittenAtAll()
    {
        var service = this.CreateService();

        await service.IngestAsync(
            Call,
            new RunnerIngestBatch(1, "k", [new RunnerTraceEvent(DateTimeOffset.UtcNow, "e", null)], [], []));

        await this._writer.DidNotReceive().WriteFileResultsAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<RunnerFileOutcome>>(), Arg.Any<CancellationToken>());
        await this._writer.DidNotReceive().WriteSpendAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<RunnerSpendRecord>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AFullBatch_WritesEventsResultsAndSpend()
    {
        var service = this.CreateService();
        var batch = new RunnerIngestBatch(
            1,
            "k",
            [new RunnerTraceEvent(DateTimeOffset.UtcNow, "e", null)],
            [new RunnerFileOutcome("src/a.cs", true, false, "done", null, ["1:m:-:-:None"])],
            [new RunnerSpendRecord("reviewer-medium", 100, 20, 0.5m)]);

        var result = await service.IngestAsync(Call, batch);

        Assert.Equal(RunnerIngestOutcome.Applied, result.Outcome);
        await this._writer.Received(1).WriteEventsAsync(JobId, Arg.Any<IReadOnlyList<RunnerTraceEvent>>(), Arg.Any<CancellationToken>());
        await this._writer.Received(1).WriteFileResultsAsync(JobId, Arg.Any<IReadOnlyList<RunnerFileOutcome>>(), Arg.Any<CancellationToken>());
        await this._writer.Received(1).WriteSpendAsync(JobId, Arg.Any<IReadOnlyList<RunnerSpendRecord>>(), Arg.Any<CancellationToken>());
    }
}
