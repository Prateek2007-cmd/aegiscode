// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.CodeInsights.Ports;
using MeisterDev.ProPR.CodeInsights.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace MeisterDev.ProPR.CodeInsights.Tests.Controllers;

public sealed class ReviewerPerformanceRecoveryControllerTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task ModelRetryRequiresTenantAdministrationAndLicense(bool tenantAdmin, bool licensed)
    {
        var maintenance = Substitute.For<IModelEvidenceMaintenance>();
        var harness = new CodeInsightAudienceHarness(tenantAdmin: tenantAdmin, licensed: licensed, modelEvidenceMaintenance: maintenance);
        Assert.Equal(
            403,
            Assert.IsAssignableFrom<ObjectResult>(
                await harness.ReviewerPerformance.RetryModelEvidence(Guid.NewGuid(), new("GitHub:https://github.com", Guid.NewGuid()))).StatusCode);
        await maintenance.DidNotReceiveWithAnyArgs().RetryAsync(default, default!);
    }

    [Theory]
    [InlineData(ModelEvidenceRetryResult.Retried, 200)]
    [InlineData(ModelEvidenceRetryResult.NotFound, 404)]
    [InlineData(ModelEvidenceRetryResult.Disabled, 409)]
    [InlineData(ModelEvidenceRetryResult.Busy, 409)]
    [InlineData(ModelEvidenceRetryResult.Cooldown, 409)]
    [InlineData(ModelEvidenceRetryResult.Unreconstructable, 409)]
    public async Task ModelRetryUsesAuthorizedScopeAndReportsTheActionOutcome(ModelEvidenceRetryResult outcome, int status)
    {
        var maintenance = Substitute.For<IModelEvidenceMaintenance>();
        var id = Guid.NewGuid();
        maintenance.RetryAsync(
                Arg.Is<ModelEvidenceRetryTarget>(target => target.AggregateId == id), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(outcome);
        var harness = new CodeInsightAudienceHarness(tenantAdmin: true, modelEvidenceMaintenance: maintenance);
        var request = new ReviewerPerformanceRetryModelRequest("GitHub:https://github.com", Guid.NewGuid());
        var result = await harness.ReviewerPerformance.RetryModelEvidence(id, request);
        Assert.Equal(status, result is StatusCodeResult code ? code.StatusCode : Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
        if (status == 409)
        {
            Assert.False(string.IsNullOrWhiteSpace(Assert.IsType<ProblemDetails>(Assert.IsAssignableFrom<ObjectResult>(result).Value).Detail));
        }

        await maintenance.Received(1).RetryAsync(
            Arg.Is<ModelEvidenceRetryTarget>(target =>
                target.AggregateId == id && target.ProviderScope == request.ProviderScope && target.ConnectionId == request.ConnectionId),
            Arg.Is<IReadOnlyCollection<Guid>>(clients => clients.Count == 2
                                                         && clients.Contains(CodeInsightAudienceHarness.MineA) &&
                                                         clients.Contains(CodeInsightAudienceHarness.MineB)), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false, true, StatusCodes.Status403Forbidden)]
    [InlineData(true, false, StatusCodes.Status403Forbidden)]
    public async Task RetryRequiresTenantAdministrationAndLicense(bool tenantAdmin, bool licensed, int expected)
    {
        var store = Substitute.For<ICodeInsightHistoricalRefreshStore>();
        var h = new CodeInsightAudienceHarness(tenantAdmin: tenantAdmin, licensed: licensed, historicalRefreshStore: store);
        Assert.Equal(expected, Assert.IsAssignableFrom<ObjectResult>(await h.ReviewerPerformance.RetryEvidence(Guid.NewGuid())).StatusCode);
        await store.DidNotReceiveWithAnyArgs().RequeueAsync(default, default!, default);
    }

    [Theory]
    [InlineData(CodeInsightHistoricalRequeueResult.Queued, StatusCodes.Status202Accepted)]
    [InlineData(CodeInsightHistoricalRequeueResult.NotFound, StatusCodes.Status404NotFound)]
    [InlineData(CodeInsightHistoricalRequeueResult.InProgress, StatusCodes.Status409Conflict)]
    [InlineData(CodeInsightHistoricalRequeueResult.CollectionDisabled, StatusCodes.Status409Conflict)]
    public async Task RetryUsesOnlyAuthorizedClientScope(CodeInsightHistoricalRequeueResult outcome, int status)
    {
        var store = Substitute.For<ICodeInsightHistoricalRefreshStore>();
        var id = Guid.NewGuid();
        store.RequeueAsync(id, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(outcome);
        var h = new CodeInsightAudienceHarness(tenantAdmin: true, historicalRefreshStore: store);
        var result = await h.ReviewerPerformance.RetryEvidence(id);
        Assert.Equal(status, result is StatusCodeResult code ? code.StatusCode : Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
        if (status == StatusCodes.Status409Conflict)
        {
            var problem = Assert.IsType<ProblemDetails>(Assert.IsAssignableFrom<ObjectResult>(result).Value);
            Assert.Equal(
                outcome == CodeInsightHistoricalRequeueResult.CollectionDisabled
                    ? "Enable Code Insights collection for this client, then retry."
                    : "Wait for the current evidence observation to finish, then retry.", problem.Detail);
        }

        await store.Received(1).RequeueAsync(
            id,
            Arg.Is<IReadOnlyCollection<Guid>>(clients => clients.Count == 2 && clients.Contains(CodeInsightAudienceHarness.MineA)
                                                                            && clients.Contains(CodeInsightAudienceHarness.MineB)), Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetryWithoutDatabaseServicesIsUnavailable()
    {
        var h = new CodeInsightAudienceHarness(tenantAdmin: true);
        Assert.Equal(
            StatusCodes.Status503ServiceUnavailable,
            Assert.IsType<StatusCodeResult>(await h.ReviewerPerformance.RetryEvidence(Guid.NewGuid())).StatusCode);
    }
}
