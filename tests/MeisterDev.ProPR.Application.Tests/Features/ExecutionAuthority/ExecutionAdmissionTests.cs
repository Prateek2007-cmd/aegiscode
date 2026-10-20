// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.ExecutionAuthority;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Extensibility.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace MeisterDev.ProPR.Application.Tests.Features.ExecutionAuthority;

public sealed class ExecutionAdmissionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
    private readonly Guid _clientId = Guid.NewGuid();
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly IClientRegistry _clients = Substitute.For<IClientRegistry>();
    private readonly IScmProviderRegistry _providers = Substitute.For<IScmProviderRegistry>();
    private readonly ICodeReviewQueryService _reviews = Substitute.For<ICodeReviewQueryService>();
    private readonly IAiConnectionRepository _connections = Substitute.For<IAiConnectionRepository>();
    private readonly IReviewJobExecutionStore _jobs = Substitute.For<IReviewJobExecutionStore>();
    private readonly FakeTimeProvider _time = new(Now);

    public ExecutionAdmissionTests()
    {
        this._clients.GetTenantIdAsync(this._clientId, Arg.Any<CancellationToken>()).Returns(this._tenantId);
        this._providers.GetCodeReviewQueryService(Arg.Any<ScmProvider>()).Returns(this._reviews);
        this._connections.GetByClientAsync(this._clientId, Arg.Any<CancellationToken>()).Returns([]);
        this._connections.GetByTenantAsync(this._tenantId, Arg.Any<CancellationToken>()).Returns([]);
    }

    [Fact]
    public async Task WithoutAnAuthority_EveryReviewIsAllowedWithoutALookup()
    {
        var admission = new ExecutionAdmission(NullLogger<ExecutionAdmission>.Instance, this._clients, this._providers, this._connections);

        var result = await admission.AdmitReviewAsync(this.Job(), this._jobs);

        Assert.False(admission.IsActive);
        Assert.Equal(AdmissionOutcome.Allow, result.Outcome);
        Assert.Empty(this._clients.ReceivedCalls());
        Assert.Empty(this._reviews.ReceivedCalls());
    }

    [Fact]
    public async Task ARequest_CarriesTheReviewAttemptOwnerAuthorAndConnectionRevisions()
    {
        var job = this.Job();
        job.RetryCount = 2;
        var author = new PullRequestAuthor(job.ProviderHost, "user-7", "dev", "Dev Person", isBot: false);
        this._reviews.GetReviewAsync(this._clientId, Arg.Any<CodeReviewRef>(), Arg.Any<CancellationToken>(), Arg.Any<ReviewDiscoveryContext?>())
            .Returns(DiscoveryItem(job, author));
        var own = Connection(this._clientId, null, revision: 3);
        var inherited = Connection(null, this._tenantId, revision: 1);
        this._connections.GetByClientAsync(this._clientId, Arg.Any<CancellationToken>()).Returns([own]);
        this._connections.GetByTenantAsync(this._tenantId, Arg.Any<CancellationToken>()).Returns([inherited]);
        var authority = new RecordingAuthority(AdmissionDecision.Allow);

        var result = await this.Admission(authority).AdmitReviewAsync(job, this._jobs);

        Assert.Equal(AdmissionOutcome.Allow, result.Outcome);
        var request = Assert.Single(authority.Requests);
        Assert.Equal(new ExecutionIdentity(ExecutionKind.Review, job.Id, 3), request.Execution);
        Assert.Equal(this._tenantId, request.TenantId);
        Assert.Equal(this._clientId, request.ClientId);
        Assert.Equal(ScmProviderKind.AzureDevOps, request.Location.Provider);
        Assert.Equal(job.PullRequestId, request.Location.PullRequestNumber);
        Assert.Equal(new ScmAuthor("user-7", "dev", "Dev Person", false), request.Author);
        Assert.Equal(
            [new AiConnectionRevision(own.Id, 3), new AiConnectionRevision(inherited.Id, 1)],
            request.AiConnections);
        Assert.Equal(Now, request.RequestedAt);
        await this._jobs.Received(1).UpdatePullRequestAuthorAsync(job.Id, author, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AJobThatRecordedItsAuthor_IsNotLookedUpAgain()
    {
        var job = this.Job();
        job.SetPullRequestAuthor(new PullRequestAuthor(job.ProviderHost, "user-9", "known"));
        var authority = new RecordingAuthority(AdmissionDecision.Allow);

        await this.Admission(authority).AdmitReviewAsync(job, this._jobs);

        Assert.Equal("user-9", Assert.Single(authority.Requests).Author?.ExternalUserId);
        Assert.Empty(this._reviews.ReceivedCalls());
    }

    [Fact]
    public async Task AnAuthorTheProviderCannotReport_LeavesTheRequestWithoutAnAuthor()
    {
        this._reviews.GetReviewAsync(this._clientId, Arg.Any<CodeReviewRef>(), Arg.Any<CancellationToken>(), Arg.Any<ReviewDiscoveryContext?>())
            .ThrowsAsync(new InvalidOperationException("The provider did not answer."));
        var authority = new RecordingAuthority(AdmissionDecision.Allow);

        var result = await this.Admission(authority).AdmitReviewAsync(this.Job(), this._jobs);

        Assert.Equal(AdmissionOutcome.Allow, result.Outcome);
        Assert.Null(Assert.Single(authority.Requests).Author);
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(600, 600)]
    [InlineData(18_000, 3_600)]
    public async Task AHold_WaitsAtLeastTheMinimumAndAtMostTheMaximum(int requestedSeconds, int appliedSeconds)
    {
        var authority = new RecordingAuthority(
            AdmissionDecision.Hold("author_limit_reached", "The plan's author limit is reached.", TimeSpan.FromSeconds(requestedSeconds)));

        var result = await this.Admission(authority).AdmitReviewAsync(this.Job(), this._jobs);

        Assert.Equal(AdmissionOutcome.Hold, result.Outcome);
        Assert.Equal("author_limit_reached", result.Reason);
        Assert.Equal("The plan's author limit is reached.", result.Message);
        Assert.Equal(Now + TimeSpan.FromSeconds(appliedSeconds), result.RetryAt);
    }

    // A hold promises its delay from the moment ProPR learns of it. An answer that took 40 seconds still holds
    // for the full 30 seconds the authority asked for, while the request names the time ProPR asked.
    [Fact]
    public async Task AHold_CountsFromTheAnswer_NotFromTheRequest()
    {
        var authority = new SlowAuthority(
            this._time,
            TimeSpan.FromSeconds(40),
            () => AdmissionDecision.Hold("author_limit_reached", "The plan's author limit is reached.", TimeSpan.FromSeconds(30)));

        var result = await this.Admission(authority).AdmitReviewAsync(this.Job(), this._jobs);

        Assert.Equal(Now + TimeSpan.FromSeconds(70), result.RetryAt);
        Assert.Equal(Now, Assert.Single(authority.Requests).RequestedAt);
    }

    [Fact]
    public async Task AnAuthorityThatFailsSlowly_HoldsForTheMinimumFromTheFailure()
    {
        var authority = new SlowAuthority(this._time, TimeSpan.FromSeconds(40), () => throw new HttpRequestException("timed out"));

        var result = await this.Admission(authority).AdmitReviewAsync(this.Job(), this._jobs);

        Assert.Equal(ExecutionAdmission.AuthorityUnavailableReason, result.Reason);
        Assert.Equal(Now + TimeSpan.FromSeconds(40) + ExecutionAdmission.MinimumHold, result.RetryAt);
    }

    [Fact]
    public async Task ARefusal_KeepsTheAuthoritysCodeAndText()
    {
        var authority = new RecordingAuthority(AdmissionDecision.Refuse("repository_not_in_plan", "This repository is not part of the plan."));

        var result = await this.Admission(authority).AdmitReviewAsync(this.Job(), this._jobs);

        Assert.Equal(AdmissionOutcome.Refuse, result.Outcome);
        Assert.Equal("repository_not_in_plan", result.Reason);
        Assert.Equal("This repository is not part of the plan.", result.Message);
        Assert.Null(result.RetryAt);
    }

    [Fact]
    public async Task AnAuthorityThatFails_HoldsTheReviewForTheMinimumDelay()
    {
        var authority = Substitute.For<IExecutionAuthority>();
        authority.AdmitAsync(Arg.Any<AdmissionRequest>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<AdmissionDecision>>(_ => throw new HttpRequestException("unreachable"));

        var result = await this.Admission(authority).AdmitReviewAsync(this.Job(), this._jobs);

        Assert.Equal(AdmissionOutcome.Hold, result.Outcome);
        Assert.Equal(ExecutionAdmission.AuthorityUnavailableReason, result.Reason);
        Assert.Equal(Now + ExecutionAdmission.MinimumHold, result.RetryAt);
    }

    [Fact]
    public async Task AThreadPass_IsAskedAboutTheAttemptItIsAboutToBegin()
    {
        var job = new ThreadPassJob(Guid.NewGuid(), this._clientId, "https://dev.azure.com/org", "project", "repo", 7, 2, "revision", "trigger");
        job.AttemptCount = 1;
        var authority = new RecordingAuthority(AdmissionDecision.Allow);

        await this.Admission(authority).AdmitThreadPassAsync(job);

        Assert.Equal(new ExecutionIdentity(ExecutionKind.ThreadPass, job.Id, 2), Assert.Single(authority.Requests).Execution);
    }

    private ExecutionAdmission Admission(IExecutionAuthority authority)
    {
        return new ExecutionAdmission(
            NullLogger<ExecutionAdmission>.Instance,
            this._clients,
            this._providers,
            this._connections,
            this._time,
            authority);
    }

    private ReviewJob Job()
    {
        return new ReviewJob(Guid.NewGuid(), this._clientId, "https://dev.azure.com/org", "project", "repo", 42, 1);
    }

    private static ReviewDiscoveryItemDto DiscoveryItem(ReviewJob job, PullRequestAuthor author)
    {
        return new ReviewDiscoveryItemDto(
            ScmProvider.AzureDevOps,
            job.CodeReviewReference.Repository,
            job.CodeReviewReference,
            CodeReviewState.Open,
            null,
            null,
            "Title",
            null,
            "feature",
            "main",
            Creator: author);
    }

    private static AiConnectionDto Connection(Guid? clientId, Guid? tenantId, long revision)
    {
        return new AiConnectionDto(
            Guid.NewGuid(),
            clientId,
            "connection",
            "openai",
            "https://api.example.test",
            "ApiKey",
            AiDiscoveryMode.ProviderCatalog,
            true,
            [],
            [],
            AiVerificationResultDto.NeverVerified,
            Now,
            Now,
            TenantId: tenantId)
        {
            ConfigurationRevision = revision,
        };
    }

    private sealed class SlowAuthority(FakeTimeProvider time, TimeSpan delay, Func<AdmissionDecision> answer) : IExecutionAuthority
    {
        public List<AdmissionRequest> Requests { get; } = [];

        public ValueTask<AdmissionDecision> AdmitAsync(AdmissionRequest request, CancellationToken cancellationToken)
        {
            this.Requests.Add(request);
            time.Advance(delay);
            return ValueTask.FromResult(answer());
        }

        public ValueTask<UsageAnswer> ReportUsageAsync(UsageReport report, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(UsageAnswer.Continue);
        }
    }

    private sealed class RecordingAuthority(AdmissionDecision decision) : IExecutionAuthority
    {
        public List<AdmissionRequest> Requests { get; } = [];

        public ValueTask<AdmissionDecision> AdmitAsync(AdmissionRequest request, CancellationToken cancellationToken)
        {
            this.Requests.Add(request);
            return ValueTask.FromResult(decision);
        }

        public ValueTask<UsageAnswer> ReportUsageAsync(UsageReport report, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(UsageAnswer.Continue);
        }
    }
}
