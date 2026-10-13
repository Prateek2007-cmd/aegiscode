// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;
using MeisterDev.ProPR.Application.Features.ReviewArchive;
using MeisterDev.ProPR.Application.Features.ThreadOwnership;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.Events;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MeisterDev.ProPR.Application.Tests.Features.Crawling.Execution;

public sealed class HistoricalProviderObserverTests
{
    [Fact]
    public async Task MissingRetainedHumanConcernIsUnavailableAfterAnEmptyProviderRead()
    {
        var h = new Harness();
        h.Findings.GetHistoricalMissThreadIdsAsync(Arg.Any<CodeInsightPullRequestKey>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(["missing-human"]);
        var result = await h.Observer.RefreshAsync(h.Target);
        Assert.Equal(CodeInsightObservationStatus.Unavailable, result.Status);
        Assert.Equal(CodeInsightObservationUnavailableReason.ProviderIdentityUnavailable, result.UnavailableReason);
        await h.Coverage.DidNotReceiveWithAnyArgs().RecordAcknowledgedAsync(default!, default!, false, default);
    }

    [Fact]
    public async Task NativePublicationUsesRealCommentIdentityWhenProviderReviewIdDiffersFromThreadId()
    {
        var h = new Harness();
        var target = h.Target with { Provider = ScmProvider.GitHub, ProviderScopePath = "https://github.com", ProviderScope = "GitHub:https://github.com" };
        h.Connections.GetByClientIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns([h.Connection with { ProviderFamily = ScmProvider.GitHub, HostBaseUrl = target.ProviderScopePath }]);
        var finding = h.Finding("501");
        finding = finding with { Finding = finding.Finding with { ProviderThreadId = "review-99", ProviderScope = target.ProviderScope } };
        h.Findings.GetNativeFeedbackFindingsAsync(Arg.Any<CodeInsightPullRequestKey>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([finding]);
        h.Reader.ReadAsync(
                Arg.Any<CodeInsightProviderEvidenceTarget>(), Arg.Any<ThreadOwnershipResolver>(), Arg.Any<CancellationToken>(),
                CodeInsightObservationMode.HistoricalBackfill)
            .Returns(
                new CodeInsightProviderFeedback(
                    [new PrThreadStatusEntry("PRRT_501", "Closed", "file.cs", "discussion", 1, ThreadAnchorCodeChange.Unchanged)],
                    [new PrCommentThread("PRRT_501", "file.cs", 1, [new PrThreadComment("Bot", "Concern", CommentId: 501)], "Closed")]));
        Assert.Equal(CodeInsightObservationStatus.Observed, (await h.Observer.RefreshAsync(target)).Status);
        await h.Dispositions.Received(1).ObserveHistoricalAsync(
            finding,
            Arg.Is<ThreadResolvedDomainEvent>(e => e.ThreadId == "review-99" && e.NativeStatus == "Closed"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FindinglessSourceAcknowledgesCompleteEmptyEnumeration()
    {
        var h = new Harness();
        var result = await h.Observer.RefreshAsync(h.Target);
        Assert.Equal(CodeInsightObservationStatus.Observed, result.Status);
        Assert.NotNull(result.ObservedAt);
        await h.Coverage.Received(1).RecordAcknowledgedAsync(
            h.Target.Key, h.Target.ProviderScope, true,
            Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EveryReceiptFamilyIsAcknowledgedIncludingSharedThreadPublications()
    {
        var h = new Harness();
        h.Findings.GetNativeFeedbackFindingsAsync(Arg.Any<CodeInsightPullRequestKey>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([h.Finding("1"), h.Finding("2")]);
        h.Reader.ReadAsync(
                Arg.Any<CodeInsightProviderEvidenceTarget>(), Arg.Any<ThreadOwnershipResolver>(), Arg.Any<CancellationToken>(),
                CodeInsightObservationMode.HistoricalBackfill)
            .Returns(
                new CodeInsightProviderFeedback([new PrThreadStatusEntry("own", "Closed", "file.cs", "discussion", 1, ThreadAnchorCodeChange.Unchanged)], []));
        Assert.Equal(CodeInsightObservationStatus.Observed, (await h.Observer.RefreshAsync(h.Target)).Status);
        await h.Dispositions.Received(2).ObserveHistoricalAsync(
            Arg.Any<CodeInsightNativeFindingSnapshot>(),
            Arg.Is<ThreadResolvedDomainEvent>(e => e.ProviderScope == h.Target.ProviderScope), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(CodeInsightPublicationState.Published, CodeInsightObservationStatus.Unavailable)]
    [InlineData(CodeInsightPublicationState.PolicyWithheld, CodeInsightObservationStatus.Observed)]
    [InlineData(CodeInsightPublicationState.Unknown, CodeInsightObservationStatus.Observed)]
    public async Task OnlyExpectedNativePublicationsRequireReceipts(CodeInsightPublicationState state, CodeInsightObservationStatus expected)
    {
        var h = new Harness();
        var finding = h.Finding("1");
        h.Findings.GetNativeFeedbackFindingsAsync(Arg.Any<CodeInsightPullRequestKey>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([finding with { PublicationState = state, Finding = finding.Finding with { ProviderThreadId = null, ProviderCommentId = null } }]);
        var result = await h.Observer.RefreshAsync(h.Target);
        Assert.Equal(expected, result.Status);
        if (expected == CodeInsightObservationStatus.Unavailable)
        {
            Assert.Equal(CodeInsightObservationUnavailableReason.ProviderIdentityUnavailable, result.UnavailableReason);
        }
    }

    [Theory]
    [InlineData("provider", CodeInsightObservationUnavailableReason.ProviderReadFailed)]
    [InlineData("ownership", CodeInsightObservationUnavailableReason.OwnershipUnavailable)]
    [InlineData("write", CodeInsightObservationUnavailableReason.EvidenceRetentionFailed)]
    public async Task MissingAcknowledgementNeverCertifiesCoverage(string failure, CodeInsightObservationUnavailableReason reason)
    {
        var h = new Harness();
        if (failure == "provider")
        {
            h.Reader.ReadAsync(
                    Arg.Any<CodeInsightProviderEvidenceTarget>(), Arg.Any<ThreadOwnershipResolver>(), Arg.Any<CancellationToken>(),
                    CodeInsightObservationMode.HistoricalBackfill)
                .ThrowsAsync(new HttpRequestException());
        }
        else if (failure == "ownership")
        {
            h.Origins.GetOriginsForProviderSourceAsync(
                    Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<ScmProvider>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .ThrowsAsync(new InvalidOperationException());
        }
        else
        {
            h.Coverage.RecordAcknowledgedAsync(
                    Arg.Any<CodeInsightPullRequestKey>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
                .Returns(new CodeInsightEvidenceAcknowledgment(false));
        }

        var result = await h.Observer.RefreshAsync(h.Target);
        Assert.Equal(CodeInsightObservationStatus.Unavailable, result.Status);
        Assert.Equal(reason, result.UnavailableReason);
        Assert.Null(result.ObservedAt);
    }

    [Theory]
    [InlineData("connection", CodeInsightObservationUnavailableReason.ProviderConnectionUnavailable)]
    [InlineData("ownership", CodeInsightObservationUnavailableReason.OwnershipUnavailable)]
    [InlineData("provider", CodeInsightObservationUnavailableReason.ProviderReadFailed)]
    [InlineData("retention", CodeInsightObservationUnavailableReason.EvidenceRetentionFailed)]
    public async Task HistoricalFailuresLogSafeDiagnosticsAndPreserveTheirStage(string stage, CodeInsightObservationUnavailableReason reason)
    {
        const string secret = "private-provider-token";
        var message = $"https://svc:{secret}@provider.example.test/collection?token={secret}; response payload: {secret}";
        Exception failure = stage switch
        {
            "connection" => new InvalidOperationException(message),
            "ownership" => new UnauthorizedAccessException(message),
            "provider" => new HttpRequestException(message),
            _ => new IOException(message),
        };
        failure.Data["ProviderPayload"] = message;
        List<CapturedLog> logs = [];
        var h = new Harness(new CapturingLogger<PullRequestCloseObserver>(logs));
        switch (stage)
        {
            case "connection":
                h.Connections.GetByClientIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ThrowsAsync(failure);
                break;
            case "ownership":
                h.Origins.GetOriginsForProviderSourceAsync(
                        Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<ScmProvider>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .ThrowsAsync(failure);
                break;
            case "provider":
                h.Reader.ReadAsync(
                        Arg.Any<CodeInsightProviderEvidenceTarget>(), Arg.Any<ThreadOwnershipResolver>(), Arg.Any<CancellationToken>(),
                        CodeInsightObservationMode.HistoricalBackfill)
                    .ThrowsAsync(failure);
                break;
            default:
                h.Coverage.RecordAcknowledgedAsync(
                        Arg.Any<CodeInsightPullRequestKey>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
                    .ThrowsAsync(failure);
                break;
        }

        var result = await h.Observer.RefreshAsync(h.Target);

        Assert.Equal(CodeInsightObservationStatus.Unavailable, result.Status);
        Assert.Equal(reason, result.UnavailableReason);
        Assert.Null(result.ObservedAt);
        var log = Assert.Single(logs);
        Assert.Equal(LogLevel.Warning, log.Level);
        Assert.Equal(6106, log.EventId.Id);
        Assert.Equal(h.Target.Key.PullRequestId, Assert.IsType<long>(log.Fields["PullRequestId"]));
        Assert.Equal(h.Target.Key.ClientId, Assert.IsType<Guid>(log.Fields["ClientId"]));
        Assert.Equal(reason, Assert.IsType<CodeInsightObservationUnavailableReason>(log.Fields["Reason"]));
        Assert.Contains("ExceptionType", log.Fields.Keys);
        Assert.Equal(failure.GetType().FullName, Assert.IsType<string>(log.Fields["ExceptionType"]));
        Assert.Contains("ErrorCode", log.Fields.Keys);
        Assert.Equal(failure.HResult, Assert.IsType<int>(log.Fields["ErrorCode"]));
        Assert.Null(log.Exception);
        Assert.DoesNotContain(secret, log.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("provider.example.test", log.Message, StringComparison.Ordinal);
        Assert.All(
            log.Fields, field =>
            {
                Assert.False(field.Value is Exception);
                Assert.DoesNotContain(secret, field.Value?.ToString() ?? string.Empty, StringComparison.Ordinal);
                Assert.DoesNotContain("provider.example.test", field.Value?.ToString() ?? string.Empty, StringComparison.Ordinal);
            });
    }

    [Theory]
    [InlineData("connection", CodeInsightObservationUnavailableReason.ProviderConnectionUnavailable)]
    [InlineData("identity", CodeInsightObservationUnavailableReason.ProviderIdentityUnavailable)]
    [InlineData("enumeration", CodeInsightObservationUnavailableReason.IncompleteEnumeration)]
    public async Task UnavailableEvidenceKeepsItsReasonWithoutExceptionDiagnostics(string missingEvidence, CodeInsightObservationUnavailableReason reason)
    {
        List<CapturedLog> logs = [];
        var h = new Harness(new CapturingLogger<PullRequestCloseObserver>(logs));
        if (missingEvidence == "connection")
        {
            h.Connections.GetByClientIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns([]);
        }
        else if (missingEvidence == "identity")
        {
            h.Findings.GetNativeFeedbackFindingsAsync(Arg.Any<CodeInsightPullRequestKey>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns([h.Finding("1")]);
        }
        else
        {
            h.Reader.ReadAsync(
                    Arg.Any<CodeInsightProviderEvidenceTarget>(), Arg.Any<ThreadOwnershipResolver>(), Arg.Any<CancellationToken>(),
                    CodeInsightObservationMode.HistoricalBackfill)
                .Returns(new CodeInsightProviderFeedback([], [], false));
        }

        var result = await h.Observer.RefreshAsync(h.Target);

        Assert.Equal(CodeInsightObservationStatus.Unavailable, result.Status);
        Assert.Equal(reason, result.UnavailableReason);
        Assert.Null(result.ObservedAt);
        Assert.Empty(logs);
        await h.Coverage.DidNotReceiveWithAnyArgs().RecordAcknowledgedAsync(default!, default!, false, default);
    }

    [Fact]
    public async Task RequestedReaderCancellationPropagatesWithoutFailureDiagnostics()
    {
        List<CapturedLog> logs = [];
        var h = new Harness(new CapturingLogger<PullRequestCloseObserver>(logs));
        using var cancellation = new CancellationTokenSource();
        h.Reader.ReadAsync(
                Arg.Any<CodeInsightProviderEvidenceTarget>(), Arg.Any<ThreadOwnershipResolver>(), Arg.Any<CancellationToken>(),
                CodeInsightObservationMode.HistoricalBackfill)
            .Returns(_ =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<CodeInsightProviderFeedback>(cancellation.Token);
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Observer.RefreshAsync(h.Target, cancellation.Token));

        Assert.Empty(logs);
        await h.Coverage.DidNotReceiveWithAnyArgs().RecordAcknowledgedAsync(default!, default!, false, default);
    }

    [Fact]
    public async Task DisabledCollectionAvoidsAllSourceReads()
    {
        var h = new Harness();
        h.Gate.IsCollectionEnabledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(false);
        Assert.Equal(CodeInsightObservationStatus.Disabled, (await h.Observer.RefreshAsync(h.Target)).Status);
        await h.Reader.DidNotReceiveWithAnyArgs().ReadAsync(default!, default!);
    }

    [Fact]
    public async Task CapturedProviderDoesNotRebindToAnotherFamilyOnTheSameAuthority()
    {
        var h = new Harness();
        h.Connections.GetByClientIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns([h.Connection with { ProviderFamily = ScmProvider.GitHub }]);
        var result = await h.Observer.RefreshAsync(h.Target);
        Assert.Equal(CodeInsightObservationUnavailableReason.ProviderConnectionUnavailable, result.UnavailableReason);
        await h.Reader.DidNotReceiveWithAnyArgs().ReadAsync(default!, default!);
    }

    [Fact]
    public async Task RequestedCancellationPropagatesWithoutCoverage()
    {
        var h = new Harness();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Observer.RefreshAsync(h.Target, cancel.Token));
        await h.Coverage.DidNotReceiveWithAnyArgs().RecordAcknowledgedAsync(default!, default!, false, default);
    }

    [Fact]
    public async Task CancellationRequestedDuringAReaderThatReturnsLateCannotAcknowledgeCoverage()
    {
        var h = new Harness();
        using var cancellation = new CancellationTokenSource();
        h.Reader.ReadAsync(
                Arg.Any<CodeInsightProviderEvidenceTarget>(), Arg.Any<ThreadOwnershipResolver>(), Arg.Any<CancellationToken>(),
                CodeInsightObservationMode.HistoricalBackfill)
            .Returns(_ =>
            {
                cancellation.Cancel();
                return new CodeInsightProviderFeedback([], []);
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Observer.RefreshAsync(h.Target, cancellation.Token));
        await h.Coverage.DidNotReceiveWithAnyArgs().RecordAcknowledgedAsync(default!, default!, false, default);
        await h.Harvester.DidNotReceiveWithAnyArgs().ObserveHistoricalAsync(default!);
    }

    private sealed record CapturedLog(LogLevel Level, EventId EventId, IReadOnlyDictionary<string, object?> Fields, Exception? Exception, string Message);

    private sealed class CapturingLogger<T>(List<CapturedLog> logs) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var fields = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.ToDictionary(field => field.Key, field => field.Value)
                : new Dictionary<string, object?>();
            logs.Add(new CapturedLog(logLevel, eventId, fields, exception, formatter(state, exception)));
        }
    }

    private sealed class Harness
    {
        internal readonly ClientScmConnectionDto Connection;
        internal readonly IClientScmConnectionRepository Connections = Substitute.For<IClientScmConnectionRepository>();
        internal readonly ICodeInsightHarvestCoverageRecorder Coverage = Substitute.For<ICodeInsightHarvestCoverageRecorder>();
        internal readonly ICodeInsightDispositionService Dispositions = Substitute.For<ICodeInsightDispositionService>();
        internal readonly ICodeInsightFindingStore Findings = Substitute.For<ICodeInsightFindingStore>();
        internal readonly ICodeInsightsCollectionGate Gate = Substitute.For<ICodeInsightsCollectionGate>();
        internal readonly ICodeInsightMissHarvester Harvester = Substitute.For<ICodeInsightMissHarvester>();
        internal readonly PullRequestCloseObserver Observer;
        internal readonly IPostedCommentOriginStore Origins = Substitute.For<IPostedCommentOriginStore>();
        internal readonly ICodeInsightProviderFeedbackReader Reader = Substitute.For<ICodeInsightProviderFeedbackReader>();

        internal readonly CodeInsightProviderEvidenceTarget Target = new(
            new CodeInsightPullRequestKey(Guid.NewGuid(), "repo", 1), ScmProvider.AzureDevOps,
            "https://ado.example.test/collection", "project", "AzureDevOps:https://ado.example.test/collection");

        internal Harness(ILogger<PullRequestCloseObserver>? logger = null)
        {
            this.Connection = new ClientScmConnectionDto(
                Guid.NewGuid(), this.Target.Key.ClientId, ScmProvider.AzureDevOps,
                "https://ado.example.test/collection", ScmAuthenticationKind.PersonalAccessToken, "ADO", true,
                "verified", null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            this.Gate.IsCollectionEnabledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
            this.Origins.GetOriginsForProviderSourceAsync(
                    Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<ScmProvider>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new PostedCommentOriginSourceSnapshot([], []));
            this.Connections.GetByClientIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns([this.Connection]);
            this.Reader.ReadAsync(
                    Arg.Any<CodeInsightProviderEvidenceTarget>(), Arg.Any<ThreadOwnershipResolver>(), Arg.Any<CancellationToken>(),
                    CodeInsightObservationMode.HistoricalBackfill)
                .Returns(new CodeInsightProviderFeedback([], []));
            this.Dispositions
                .ObserveHistoricalAsync(Arg.Any<CodeInsightNativeFindingSnapshot>(), Arg.Any<ThreadResolvedDomainEvent>(), Arg.Any<CancellationToken>())
                .Returns(new CodeInsightEvidenceAcknowledgment(true));
            this.Harvester.ObserveHistoricalAsync(Arg.Any<ThreadUpdatedEvent>(), Arg.Any<CancellationToken>())
                .Returns(new CodeInsightEvidenceAcknowledgment(true));
            this.Coverage.RecordAcknowledgedAsync(
                    Arg.Any<CodeInsightPullRequestKey>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
                .Returns(new CodeInsightEvidenceAcknowledgment(true));
            this.Observer = new PullRequestCloseObserver(
                LocalScmPolicies.Registry, logger ?? NullLogger<PullRequestCloseObserver>.Instance,
                missHarvester: this.Harvester, findingStore: this.Findings, collectionGate: this.Gate,
                scmConnectionRepository: this.Connections, postedCommentOriginStore: this.Origins, dispositionService: this.Dispositions,
                harvestCoverageRecorder: this.Coverage, feedbackReader: this.Reader);
        }

        internal CodeInsightNativeFindingSnapshot Finding(string comment)
        {
            return new CodeInsightNativeFindingSnapshot(
                new CodeInsightFindingView(
                    Guid.NewGuid(), Guid.NewGuid(), "old", 0,
                    "file.cs", 1, CommentSeverity.Warning, "Concern", "own", DateTimeOffset.UtcNow,
                    ProviderScope: this.Target.ProviderScope, ProviderCommentId: comment), CodeInsightPublicationState.Published, null, null);
        }
    }
}
