// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Crawling.Execution;
using NSubstitute;
using MeisterDev.ProPR.Infrastructure.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Features.Crawling;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Stub;

namespace MeisterDev.ProPR.Infrastructure.Tests.Services;

public sealed class ScheduledReviewDiscoveryServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StubComposition_PreservesCredentialFreeDiscovery(bool databaseConfigured)
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["DB_CONNECTION_STRING"] = databaseConfigured ? "Host=127.0.0.1;Database=fixture;Username=fixture" : null,
                ["ADO_STUB_PR"] = "true",
            }).Build();
        services.AddLogging();
        services.AddInfrastructureSupport(configuration);
        services.AddCrawlingModule(configuration);
        services.AddSingleton(Substitute.For<IScmProviderRegistry>());
        services.AddSingleton(Substitute.For<IScheduledReviewAuthorityService>());
        using var container = services.BuildServiceProvider();
        using var scope = container.CreateScope();

        Assert.IsType<StubAssignedPrFetcher>(scope.ServiceProvider.GetRequiredService<IAssignedReviewDiscoveryService>());
    }

    [Fact]
    public void ProductionComposition_ReplacesEarlierAzureOnlyDiscoveryBinding()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["DB_CONNECTION_STRING"] = "Host=127.0.0.1;Database=fixture;Username=fixture",
            }).Build();
        services.AddLogging();
        services.AddInfrastructureSupport(configuration);
        services.AddCrawlingModule(configuration);
        services.AddSingleton(Substitute.For<IScmProviderRegistry>());
        services.AddSingleton(Substitute.For<IScheduledReviewAuthorityService>());
        services.AddSingleton(Substitute.For<IClientScmConnectionRepository>());
        using var container = services.BuildServiceProvider();
        using var scope = container.CreateScope();

        Assert.IsType<ScheduledReviewDiscoveryService>(scope.ServiceProvider.GetRequiredService<IAssignedReviewDiscoveryService>());
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("count")]
    [InlineData("observations")]
    [InlineData("continuation")]
    public async Task Discovery_TraversesLaterPagesAndRefusesIncompleteEvidence(string scenario)
    {
        var registry = Substitute.For<IScmProviderRegistry>();
        var authority = Substitute.For<IScheduledReviewAuthorityService>();
        var provider = Substitute.For<IReviewDiscoveryPageProvider>();
        var policy = Substitute.For<IReviewSourcePolicy>();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var repository = new RepositoryRef(host, "123", "owner", "owner/repo", "repo");
        var config = new CrawlConfigurationDto(Guid.NewGuid(), Guid.NewGuid(), host.Provider, host.HostBaseUrl, "owner", 60, true, DateTimeOffset.UtcNow, []);
        var connection = new ClientScmConnectionDto(
            Guid.NewGuid(), config.ClientId, host.Provider, host.HostBaseUrl, ScmAuthenticationKind.PersonalAccessToken, "Fixture", true, "verified", null,
            null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var grant = new ClientScmScopeDto(
            Guid.NewGuid(), config.ClientId, connection.Id, "repository", "123", "owner/repo", "repo", "verified", true, null, null, DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        var evidence = new ScheduledReviewAuthority(connection, [grant], null, config);
        policy.MatchesScheduledRepositoryGrant(config, repository, grant).Returns(true);
        authority.ResolveAsync(config, null, Arg.Any<CancellationToken>()).Returns(evidence);
        registry.GetReviewDiscoveryProvider(host.Provider).Returns(provider);
        registry.GetReviewSourcePolicy(host.Provider).Returns(policy);
        var discovery = Substitute.For<IRepositoryDiscoveryProvider>();
        registry.GetRepositoryDiscoveryProvider(host.Provider).Returns(discovery);
        discovery.ListRepositoriesAsync(
            Arg.Any<ConnectionDiscoveryContext>(), config.ProviderScopePath, config.ProviderProjectKey, Arg.Any<CancellationToken>()).Returns([repository]);
        var revision = new ReviewRevision("head", "base", "start", "native", "patch");

        ReviewDiscoveryItemDto Item(int number) => new(
            host.Provider, repository, new(repository, CodeReviewPlatformKind.PullRequest, number.ToString(), number), CodeReviewState.Open, revision, null,
            "Fixture", null, "topic", "main");

        provider.ListOpenReviewsPageAsync(config.ClientId, repository, null, null, Arg.Any<CancellationToken>(), Arg.Any<ReviewDiscoveryContext>()).Returns(
            new ReviewDiscoveryPageDto(
                Enumerable.Range(1, 100).Select(Item).ToArray(), "2", 100, Enumerable.Range(1, 100).Select(n => n.ToString()).ToArray()));
        provider.ListOpenReviewsPageAsync(config.ClientId, repository, null, "2", Arg.Any<CancellationToken>(), Arg.Any<ReviewDiscoveryContext>()).Returns(
            new ReviewDiscoveryPageDto(
                Enumerable.Range(101, 100).Select(Item).ToArray(), scenario == "continuation" ? "2" : "3", scenario == "count" ? 99 : 100,
                Enumerable.Range(scenario == "observations" ? 1 : 101, 100).Select(n => n.ToString()).ToArray()));
        provider.ListOpenReviewsPageAsync(config.ClientId, repository, null, "3", Arg.Any<CancellationToken>(), Arg.Any<ReviewDiscoveryContext>()).Returns(
            new ReviewDiscoveryPageDto(
                Enumerable.Range(201, 37).Select(Item).ToArray(), null, 37, Enumerable.Range(201, 37).Select(n => n.ToString()).ToArray()));

        var service = new ScheduledReviewDiscoveryService(registry, authority);
        if (scenario != "complete")
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ListAssignedOpenReviewsAsync(config));
            await provider.Received(1).ListOpenReviewsPageAsync(
                config.ClientId, repository, null, "2", Arg.Any<CancellationToken>(), Arg.Any<ReviewDiscoveryContext>());
            await provider.DidNotReceive().ListOpenReviewsPageAsync(
                config.ClientId, repository, null, "3", Arg.Any<CancellationToken>(), Arg.Any<ReviewDiscoveryContext>());
            return;
        }

        var result = await service.ListAssignedOpenReviewsAsync(config);

        Assert.Equal(237, result.Count);
        Assert.Equal(revision, result[^1].ReviewRevision);
        Assert.Equal(evidence, result[^1].Authority);
    }
}
