// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.ExecutionAuthority;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Extensibility.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace MeisterDev.ProPR.Application.Tests.Features.ExecutionAuthority;

public sealed class ExecutionUsageSinkTests
{
    private static readonly DateTimeOffset CompletedAt = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
    private readonly ExecutionScopeAccessor _scopes = new();
    private readonly IExecutionStopper _stopper = Substitute.For<IExecutionStopper>();
    private readonly IUsageReportStore _store = Substitute.For<IUsageReportStore>();
    private readonly IClientRegistry _clients = Substitute.For<IClientRegistry>();

    private readonly ExecutionScope _scope = new(
        new ExecutionIdentity(ExecutionKind.Review, Guid.NewGuid(), 1),
        Guid.NewGuid(),
        Guid.NewGuid());

    public ExecutionUsageSinkTests()
    {
        this._store.AppendAsync(Arg.Any<UsageReport>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<UsageReport>() with { Sequence = 42 });
    }

    [Fact]
    public async Task WithoutAnAuthority_NothingIsStored()
    {
        var sink = new ExecutionUsageSink(this._scopes, this._stopper, NullLogger<ExecutionUsageSink>.Instance, this._store, this._clients);

        await sink.RecordAsync(Usage());

        Assert.False(sink.IsActive);
        Assert.Empty(this._store.ReceivedCalls());
    }

    [Fact]
    public async Task AReport_IsStoredAndPassedToTheAuthorityWithTheExecutionItBelongsTo()
    {
        var authority = new AnsweringAuthority(UsageAnswer.Continue);
        using var _ = this._scopes.BeginScope(this._scope);

        await this.Sink(authority).RecordAsync(Usage());

        var report = Assert.Single(authority.Reports);
        Assert.Equal(42, report.Sequence);
        Assert.Equal(this._scope.Identity, report.Execution);
        Assert.Equal(this._scope.TenantId, report.TenantId);
        Assert.Equal(this._scope.ClientId, report.ClientId);
        Assert.Equal(7, report.Connection.Revision);
        Assert.Equal(1_000, report.InputTokens);
        Assert.Equal(200, report.OutputTokens);
        Assert.Equal(100, report.CachedInputTokens);
        Assert.False(report.UsageIncomplete);
        Assert.False(this._scope.StopRequested);
    }

    [Fact]
    public async Task AStop_EndsTheExecutionOnceAndRefusesItsNextCall()
    {
        var authority = new AnsweringAuthority(UsageAnswer.Stop("credit_cap_reached", "The plan's usage cap is reached."));
        using var _ = this._scopes.BeginScope(this._scope);
        var sink = this.Sink(authority);

        await sink.RecordAsync(Usage());
        await sink.RecordAsync(Usage());

        Assert.True(this._scope.StopRequested);
        Assert.Equal("The plan's usage cap is reached.", this._scope.StopMessage);
        await this._stopper.Received(1).StopAsync(this._scope.Identity, "The plan's usage cap is reached.", Arg.Any<CancellationToken>());
        var refused = Assert.Throws<ExecutionStoppedException>(this._scope.ThrowIfStopped);
        Assert.Equal(this._scope.Identity, refused.Execution);
    }

    [Fact]
    public async Task AnAuthorityThatFails_LetsTheExecutionContinue()
    {
        var authority = Substitute.For<IExecutionAuthority>();
        authority.ReportUsageAsync(Arg.Any<UsageReport>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<UsageAnswer>>(_ => throw new HttpRequestException("unreachable"));
        using var _ = this._scopes.BeginScope(this._scope);

        await this.Sink(authority).RecordAsync(Usage());

        Assert.False(this._scope.StopRequested);
        await this._store.Received(1).AppendAsync(Arg.Any<UsageReport>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ACallOutsideAnExecution_TakesItsOwnerFromTheConnection()
    {
        var clientId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        this._clients.GetTenantIdAsync(clientId, Arg.Any<CancellationToken>()).Returns(tenantId);
        var authority = new AnsweringAuthority(UsageAnswer.Stop("ignored", "Nothing to stop."));

        await this.Sink(authority).RecordAsync(Usage(clientId));

        var report = Assert.Single(authority.Reports);
        Assert.Null(report.Execution);
        Assert.Equal(clientId, report.ClientId);
        Assert.Equal(tenantId, report.TenantId);
        Assert.Empty(this._stopper.ReceivedCalls());
    }

    private ExecutionUsageSink Sink(IExecutionAuthority authority)
    {
        return new ExecutionUsageSink(
            this._scopes,
            this._stopper,
            NullLogger<ExecutionUsageSink>.Instance,
            this._store,
            this._clients,
            authority);
    }

    private static AiCallUsage Usage(Guid? clientId = null)
    {
        return new AiCallUsage(
            Guid.NewGuid(),
            7,
            clientId,
            null,
            "openai",
            "gpt-test",
            AiOperation.Chat,
            new AiTokenUsage(1_000, 200, CachedInputTokens: 100),
            TimeSpan.FromSeconds(2),
            CompletedAt);
    }

    private sealed class AnsweringAuthority(UsageAnswer answer) : IExecutionAuthority
    {
        public List<UsageReport> Reports { get; } = [];

        public ValueTask<AdmissionDecision> AdmitAsync(AdmissionRequest request, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(AdmissionDecision.Allow);
        }

        public ValueTask<UsageAnswer> ReportUsageAsync(UsageReport report, CancellationToken cancellationToken)
        {
            this.Reports.Add(report);
            return ValueTask.FromResult(answer);
        }
    }
}
