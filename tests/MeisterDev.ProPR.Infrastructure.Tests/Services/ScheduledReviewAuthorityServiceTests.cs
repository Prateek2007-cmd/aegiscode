// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Features.Crawling.Execution;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Security;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Support;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Services;

public sealed class ScheduledReviewAuthorityServiceTests
{
    [Fact]
    public async Task CanonicalRepository_WithOnlyAnotherRepositoryGrantRefusesAuthority()
    {
        var target = new CrawlConfigurationDto(
            Guid.NewGuid(), Guid.NewGuid(), ScmProvider.GitHub, "https://github.com", "owner", 60, true, DateTimeOffset.UtcNow,
            [new(Guid.NewGuid(), "repo", [], new("github", "123"))]);
        var now = DateTimeOffset.UtcNow;
        var connection = new ClientScmConnectionDto(
            Guid.NewGuid(), target.ClientId, target.Provider, target.ProviderScopePath,
            ScmAuthenticationKind.PersonalAccessToken, "Fixture", true, "verified", null, null, null, now, now);
        var connections = Substitute.For<IClientScmConnectionRepository>();
        connections.GetOperationalSelectionCandidatesAsync(target.ClientId, Arg.Any<CancellationToken>()).Returns([connection]);
        var scopes = Substitute.For<IClientScmScopeRepository>();
        scopes.GetByConnectionIdAsync(target.ClientId, connection.Id, Arg.Any<CancellationToken>()).Returns(
        [
            new ClientScmScopeDto(
                Guid.NewGuid(), target.ClientId, connection.Id, "repository", "456", "owner/other", "other", "verified", true, null, null, now, now)
        ]);
        var registry = Substitute.For<IScmProviderRegistry>();
        registry.GetConnectionConfigurationPolicy(target.Provider).Returns(new GitHubConnectionConfigurationPolicy());
        registry.GetReviewSourcePolicy(target.Provider).Returns(new GitHubReviewSourcePolicy());
        registry.GetReviewDiscoveryProvider(target.Provider).Returns(Substitute.For<IReviewDiscoveryPageProvider>());
        var activation = Substitute.For<IProviderActivationService>();
        activation.IsEnabledAsync(target.Provider, Arg.Any<CancellationToken>()).Returns(true);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ScheduledReviewAuthorityService(connections, scopes, Substitute.For<IClientReviewerIdentityRepository>(), registry, activation).ResolveAsync(
                target));
    }

    [Theory]
    [InlineData(false, "unknown", ScmProvider.GitHub)]
    [InlineData(false, "verified", ScmProvider.GitHub)]
    [InlineData(true, "verified", ScmProvider.GitLab)]
    public async Task ActiveAlternative_RefusesAuthorityEvenWithoutVerificationOrGrants(bool hasGrants, string verification, ScmProvider alternateProvider)
    {
        var config = new CrawlConfigurationDto(
            Guid.NewGuid(), Guid.NewGuid(), ScmProvider.GitHub, "https://github.com", "owner", 60, true, DateTimeOffset.UtcNow,
            [new(Guid.NewGuid(), "repo", [], new("github", "123"))]);
        var now = DateTimeOffset.UtcNow;
        var selected = new ClientScmConnectionDto(
            Guid.NewGuid(), config.ClientId, config.Provider, config.ProviderScopePath,
            ScmAuthenticationKind.PersonalAccessToken, "Fixture", true, "verified", null, null, null, now, now);
        var alternate = selected with { Id = Guid.NewGuid(), VerificationStatus = verification, ProviderFamily = alternateProvider };
        var connections = Substitute.For<IClientScmConnectionRepository>();
        connections.GetOperationalSelectionCandidatesAsync(config.ClientId, Arg.Any<CancellationToken>()).Returns([selected, alternate]);
        var scopes = Substitute.For<IClientScmScopeRepository>();
        if (hasGrants)
        {
            scopes.GetByConnectionIdAsync(config.ClientId, alternate.Id, Arg.Any<CancellationToken>()).Returns(
            [
                new ClientScmScopeDto(
                    Guid.NewGuid(), config.ClientId, alternate.Id, "repository", "123", "owner/repo", "repo", "verified", true, null, null, now, now)
            ]);
        }

        var registry = Substitute.For<IScmProviderRegistry>();
        registry.GetConnectionConfigurationPolicy(config.Provider).Returns(new GitHubConnectionConfigurationPolicy());
        registry.GetReviewSourcePolicy(config.Provider).Returns(new GitHubReviewSourcePolicy());
        registry.GetReviewDiscoveryProvider(config.Provider).Returns(Substitute.For<IReviewDiscoveryPageProvider>());
        var activation = Substitute.For<IProviderActivationService>();
        activation.IsEnabledAsync(config.Provider, Arg.Any<CancellationToken>()).Returns(true);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ScheduledReviewAuthorityService(connections, scopes, Substitute.For<IClientReviewerIdentityRepository>(), registry, activation).ResolveAsync(
                config));
        await connections.DidNotReceiveWithAnyArgs().GetOperationalConnectionAsync(default, default!, default);
    }

    [Fact]
    public async Task UnfilteredScope_SelectsOwnedAuthorityWithoutIndexingAnAbsentFilter()
    {
        var config = new CrawlConfigurationDto(
            Guid.NewGuid(), Guid.NewGuid(), ScmProvider.GitHub, "https://github.com", "owner", 60, true, DateTimeOffset.UtcNow, []);
        var connection = new ClientScmConnectionDto(
            Guid.NewGuid(), config.ClientId, config.Provider, config.ProviderScopePath,
            ScmAuthenticationKind.PersonalAccessToken, "Fixture", true, "verified", null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var scopes = Substitute.For<IClientScmScopeRepository>();
        scopes.GetByConnectionIdAsync(config.ClientId, connection.Id, Arg.Any<CancellationToken>()).Returns(
        [
            new ClientScmScopeDto(
                Guid.NewGuid(), config.ClientId, connection.Id, "repository", "123", "owner/repo", "repo", "verified", true,
                null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        ]);
        var connections = Substitute.For<IClientScmConnectionRepository>();
        connections.GetOperationalSelectionCandidatesAsync(config.ClientId, Arg.Any<CancellationToken>()).Returns([connection]);
        var registry = Substitute.For<IScmProviderRegistry>();
        registry.GetConnectionConfigurationPolicy(config.Provider).Returns(new GitHubConnectionConfigurationPolicy());
        registry.GetReviewSourcePolicy(config.Provider).Returns(new GitHubReviewSourcePolicy());
        registry.GetReviewDiscoveryProvider(config.Provider).Returns(Substitute.For<IReviewDiscoveryPageProvider>());
        var activation = Substitute.For<IProviderActivationService>();
        activation.IsEnabledAsync(config.Provider, Arg.Any<CancellationToken>()).Returns(true);

        var result = await new ScheduledReviewAuthorityService(connections, scopes, Substitute.For<IClientReviewerIdentityRepository>(), registry, activation)
            .ResolveAsync(config);

        Assert.Equal(connection.Id, result.Connection.Id);
        Assert.Single(result.Grants);
        Assert.Null(result.Reviewer);
    }
}
