// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Api.Features.Crawling.Configuration.Controllers;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Crawling.Configuration;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using MeisterDev.ProPR.Api.Features.IdentityAndAccess.Authentication;
using Swashbuckle.AspNetCore.SwaggerGen;
using System.Text.Json;

namespace MeisterDev.ProPR.Api.Tests.Features.Crawling;

public sealed class ReviewTargetAutomationControllerTests
{
    [Fact]
    public async Task Pause_ReturnsItsCommittedSnapshotAfterAnotherMutation()
    {
        var configurations = Substitute.For<ICrawlConfigurationRepository>();
        var clientId = Guid.NewGuid();
        var current = new CrawlConfigurationDto(
            Guid.NewGuid(), clientId, ScmProvider.GitHub, "https://github.com", "owner", 60, true,
            DateTimeOffset.UtcNow, [new(Guid.NewGuid(), "repo", [], new("github", "123"))]);
        var targetId = current.Id;
        configurations.GetReviewTargetPolicySnapshotAsync(targetId, clientId, Arg.Any<CancellationToken>()).Returns(_ => current);
        configurations.AcquireReviewTargetAdmissionAsync(clientId, Arg.Any<CancellationToken>()).Returns(
            new MutationLease(() => current = current with { IsActive = true, ReviewTargetRevision = 3 }));
        configurations.ChangeReviewTargetAutomationUnderAdmissionAsync(targetId, clientId, 1, false, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            current = current with { IsActive = false, ReviewTargetRevision = 2 };
            return true;
        });
        var automation = new ReviewTargetAutomationService(configurations, Substitute.For<IScheduledReviewAuthorityService>());

        var result = Assert.IsType<OkObjectResult>(await Controller(configurations, automation).ChangeAutomation(clientId, targetId, new("1", false)));

        var saved = Assert.IsType<ClientReviewTargetResponse>(result.Value);
        Assert.False(saved.IsActive);
        Assert.Equal("2", saved.Revision);
        Assert.True(current.IsActive);
        Assert.Equal(3, current.ReviewTargetRevision);
    }

    private sealed class MutationLease(Action mutation) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            mutation();
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public void AutomationSchemaRequiresRevisionAndActivity()
    {
        var repository = new SchemaRepository();
        var generator = new SchemaGenerator(
            new SchemaGeneratorOptions(), new JsonSerializerDataContractResolver(new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        generator.GenerateSchema(typeof(ChangeClientReviewTargetAutomationRequest), repository);
        var schema = repository.Schemas[nameof(ChangeClientReviewTargetAutomationRequest)];
        Assert.Contains("expectedRevision", schema.Required ?? new HashSet<string>());
        Assert.Contains("isActive", schema.Required ?? new HashSet<string>());
    }

    [Fact]
    public void TenantMachineBoundary_AllowsMappedAutomationAction()
    {
        Assert.True(TenantMachineOperationPolicy.IsAllowed("ClientReviewTargets", nameof(ClientReviewTargetsController.ChangeAutomation)));
        Assert.False(TenantMachineOperationPolicy.IsAllowed("Clients", nameof(ClientReviewTargetsController.ChangeAutomation)));
    }

    [Fact]
    public async Task MissingActivity_ReturnsBadRequestWithoutInvokingPause()
    {
        var automation = Substitute.For<IReviewTargetAutomationService>();
        var controller = Controller(Substitute.For<ICrawlConfigurationRepository>(), automation);

        Assert.IsType<BadRequestObjectResult>(await controller.ChangeAutomation(Guid.NewGuid(), Guid.NewGuid(), new("1", null)));
        await automation.DidNotReceiveWithAnyArgs().ChangeAsync(default, default, default, default, default, default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActivityChange_UsesSavedRevisionAndReturnsConfirmedActivity(bool isActive)
    {
        var configurations = Substitute.For<ICrawlConfigurationRepository>();
        var automation = Substitute.For<IReviewTargetAutomationService>();
        var clientId = Guid.NewGuid();
        var target = new CrawlConfigurationDto(
            Guid.NewGuid(), clientId, ScmProvider.GitHub, "https://github.com", "owner", 60, isActive,
            DateTimeOffset.UtcNow, [new(Guid.NewGuid(), "repo", [], new("github", "123"))], ReviewTargetRevision: 2);
        var connectionId = isActive ? Guid.NewGuid() : (Guid?)null;
        automation.ChangeAsync(clientId, target.Id, 1, isActive, connectionId, Arg.Any<CancellationToken>())
            .Returns(new ReviewTargetAutomationOutcome(ReviewTargetAutomationResult.Changed, target));

        var result = Assert.IsType<OkObjectResult>(
            await Controller(configurations, automation)
                .ChangeAutomation(clientId, target.Id, new("1", isActive, connectionId)));

        var saved = Assert.IsType<ClientReviewTargetResponse>(result.Value);
        Assert.Equal(isActive, saved.IsActive);
        Assert.Equal("2", saved.Revision);
        await configurations.DidNotReceiveWithAnyArgs().GetReviewTargetPolicySnapshotAsync(default, default, default);
    }

    [Fact]
    public async Task ChangedResponse_WithUnexpectedActivityRemainsUnconfirmed()
    {
        var configurations = Substitute.For<ICrawlConfigurationRepository>();
        var automation = Substitute.For<IReviewTargetAutomationService>();
        var clientId = Guid.NewGuid();
        var target = new CrawlConfigurationDto(
            Guid.NewGuid(), clientId, ScmProvider.GitHub, "https://github.com", "owner", 60, true,
            DateTimeOffset.UtcNow, [new(Guid.NewGuid(), "repo", [], new("github", "123"))], ReviewTargetRevision: 2);
        configurations.GetReviewTargetPolicySnapshotAsync(target.Id, clientId, Arg.Any<CancellationToken>()).Returns(target);
        automation.ChangeAsync(clientId, target.Id, 1, false, null, Arg.Any<CancellationToken>())
            .Returns(new ReviewTargetAutomationOutcome(ReviewTargetAutomationResult.Changed, target));

        Assert.IsType<ConflictObjectResult>(await Controller(configurations, automation).ChangeAutomation(clientId, target.Id, new("1", false)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Enable_RequiresNonemptyConnectionWithoutCallingAutomation(bool empty)
    {
        var automation = Substitute.For<IReviewTargetAutomationService>();
        var request = new ChangeClientReviewTargetAutomationRequest("1", true, empty ? Guid.Empty : null);

        Assert.IsType<BadRequestObjectResult>(
            await Controller(Substitute.For<ICrawlConfigurationRepository>(), automation)
                .ChangeAutomation(Guid.NewGuid(), Guid.NewGuid(), request));
        await automation.DidNotReceiveWithAnyArgs().ChangeAsync(default, default, default, default, default, default);
    }

    [Fact]
    public async Task Enable_UnavailableAuthorityReturnsForbidden()
    {
        var automation = Substitute.For<IReviewTargetAutomationService>();
        automation.ChangeAsync(default, default, default, default, default, default).ReturnsForAnyArgs(
            new ReviewTargetAutomationOutcome(ReviewTargetAutomationResult.Unavailable));

        var result = Assert.IsType<ObjectResult>(
            await Controller(Substitute.For<ICrawlConfigurationRepository>(), automation)
                .ChangeAutomation(Guid.NewGuid(), Guid.NewGuid(), new("1", true, Guid.NewGuid())));

        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
    }

    [Theory]
    [InlineData("unauthenticated", StatusCodes.Status401Unauthorized)]
    [InlineData("foreign", StatusCodes.Status403Forbidden)]
    [InlineData("user", StatusCodes.Status403Forbidden)]
    public async Task CallerWithoutClientAdministration_CannotChangeActivity(string caller, int status)
    {
        var automation = Substitute.For<IReviewTargetAutomationService>();
        var clientId = Guid.NewGuid();
        var controller = Controller(Substitute.For<ICrawlConfigurationRepository>(), automation);
        controller.HttpContext.Items["IsAdmin"] = false;
        if (caller != "unauthenticated")
        {
            controller.HttpContext.Items["UserId"] = Guid.NewGuid().ToString();
            controller.HttpContext.Items["ClientRoles"] = new Dictionary<Guid, ClientRole>
            {
                [caller == "foreign" ? Guid.NewGuid() : clientId] = caller == "foreign" ? ClientRole.ClientAdministrator : ClientRole.ClientUser,
            };
        }

        var result = Assert.IsType<ObjectResult>(await controller.ChangeAutomation(clientId, Guid.NewGuid(), new("1", false)));

        Assert.Equal(status, result.StatusCode);
        await automation.DidNotReceiveWithAnyArgs().ChangeAsync(default, default, default, default, default, default);
    }

    private static ClientReviewTargetsController Controller(ICrawlConfigurationRepository configurations, IReviewTargetAutomationService automation)
    {
        var context = new DefaultHttpContext();
        context.Items["IsAdmin"] = true;
        return new(configurations, Substitute.For<IClientScmConnectionRepository>(), automation: automation)
        {
            ControllerContext = new() { HttpContext = context },
        };
    }
}
