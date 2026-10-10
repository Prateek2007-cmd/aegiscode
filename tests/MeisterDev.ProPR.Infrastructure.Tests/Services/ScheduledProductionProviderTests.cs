// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Json;
using Azure.Core;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Services;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Models;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Ports;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Features.Clients;
using MeisterDev.ProPR.Infrastructure.Features.Crawling;
using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Reviewing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.VisualStudio.Services.Common;
using NSubstitute;
using Microsoft.AspNetCore.DataProtection;
using MeisterDev.ProPR.Infrastructure.Tests.GitHub;
using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Discovery;
using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Support;
using Microsoft.TeamFoundation.Core.WebApi;
using Microsoft.VisualStudio.Services.WebApi;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MeisterDev.ProPR.Application.Features.Crawling.Configuration;

namespace MeisterDev.ProPR.Infrastructure.Tests.Services;

public sealed class ScheduledProductionProviderTests
{
    [Theory]
    [InlineData(ScmProvider.AzureDevOps, "canonical")]
    [InlineData(ScmProvider.GitHub, "canonical")]
    [InlineData(ScmProvider.GitLab, "canonical")]
    [InlineData(ScmProvider.Forgejo, "canonical")]
    [InlineData(ScmProvider.GitHub, "legacy")]
    [InlineData(ScmProvider.GitLab, "legacy")]
    [InlineData(ScmProvider.Forgejo, "legacy")]
    [InlineData(ScmProvider.GitHub, "mixed")]
    [InlineData(ScmProvider.GitLab, "mixed")]
    [InlineData(ScmProvider.Forgejo, "mixed")]
    [InlineData(ScmProvider.GitHub, "enumeration404")]
    [InlineData(ScmProvider.GitLab, "enumeration404")]
    [InlineData(ScmProvider.Forgejo, "enumeration404")]
    [InlineData(ScmProvider.GitHub, "installationCap")]
    [InlineData(ScmProvider.AzureDevOps, "repositoryGrant")]
    [InlineData(ScmProvider.AzureDevOps, "stub")]
    public async Task RegisteredAdapter_ScheduledCycleAdmitsLaterPageWithConfiguredReviewerAndNativeRevision(ScmProvider provider, string mode)
    {
        var clientId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var repositoryId = provider == ScmProvider.AzureDevOps ? Guid.NewGuid().ToString("D") : "123";
        var scopePath = provider == ScmProvider.AzureDevOps ? "https://dev.azure.com/fixture" : "https://scm.example.test";
        var config = new CrawlConfigurationDto(
            Guid.NewGuid(), clientId, provider, scopePath, "owner", 60, true, DateTimeOffset.UtcNow,
            [new(Guid.NewGuid(), "repo", ["main"], new(provider.ToString(), repositoryId))]);
        if (mode is "legacy" or "repositoryGrant")
        {
            config = config with { RepoFilters = [config.RepoFilters[0] with { CanonicalSourceRef = null }] };
        }
        else if (mode == "mixed")
        {
            config = config with { RepoFilters = [config.RepoFilters[0], new(Guid.NewGuid(), "repo", ["main"])] };
        }
        else if (mode is "enumeration404" or "installationCap")
        {
            config = config with { RepoFilters = [] };
        }

        var now = DateTimeOffset.UtcNow;
        var selected = new ClientScmConnectionDto(
            connectionId, clientId, provider, scopePath, ScmAuthenticationKind.PersonalAccessToken,
            "Fixture", true, "verified", null, null, null, now, now);
        var connections = Substitute.For<IClientScmConnectionRepository>();
        connections.GetOperationalSelectionCandidatesAsync(clientId, Arg.Any<CancellationToken>()).Returns([selected]);
        connections.GetOperationalConnectionByIdAsync(clientId, connectionId, Arg.Any<CancellationToken>()).Returns(
            new ClientScmConnectionCredentialDto(
                connectionId, clientId, provider, scopePath, ScmAuthenticationKind.PersonalAccessToken, "Fixture", "fixture-token", true));
        if (mode == "installationCap")
        {
            selected = selected with { AuthenticationKind = ScmAuthenticationKind.AppInstallation };
            connections.GetOperationalSelectionCandidatesAsync(clientId, Arg.Any<CancellationToken>()).Returns([selected]);
            connections.GetOperationalConnectionByIdAsync(clientId, connectionId, Arg.Any<CancellationToken>()).Returns(
                GitHubAppTestHelpers.CreateAppInstallationConnection(clientId, new(provider, scopePath)) with { Id = connectionId });
        }

        var scopes = Substitute.For<IClientScmScopeRepository>();
        scopes.GetByConnectionIdAsync(clientId, connectionId, Arg.Any<CancellationToken>()).Returns(
        [
            new ClientScmScopeDto(
                Guid.NewGuid(), clientId, connectionId, provider == ScmProvider.AzureDevOps && mode != "repositoryGrant" ? "organization" : "repository",
                repositoryId, provider == ScmProvider.AzureDevOps && mode != "repositoryGrant" ? scopePath : "owner/repo", "repo", "verified", true, null, null,
                now, now)
        ]);
        var reviewerId = Guid.NewGuid().ToString("D");
        var reviewers = Substitute.For<IClientReviewerIdentityRepository>();
        reviewers.GetByConnectionIdAsync(clientId, connectionId, Arg.Any<CancellationToken>()).Returns(
            new ClientReviewerIdentityDto(Guid.NewGuid(), clientId, connectionId, provider, reviewerId, "configured", "Configured reviewer", true, now));
        var activation = Substitute.For<IProviderActivationService>();
        activation.IsEnabledAsync(provider, Arg.Any<CancellationToken>()).Returns(true);
        var factory = Substitute.For<IHttpClientFactory>();
        var pages = 0;
        var head = "head";
        var nativeIteration = 7;
        var failSecondPage = false;
        var hideReviews = false;
        var queryUnavailable = false;
        var repositoryPages = 0;
        factory.CreateClient(Arg.Any<string>()).Returns(
            new HttpClient(
                new Handler(request =>
                {
                    if (request.RequestUri!.AbsolutePath.Contains("/app/installations/", StringComparison.Ordinal))
                    {
                        return request.RequestUri.AbsolutePath.EndsWith("/access_tokens", StringComparison.Ordinal)
                            ? Json(new { token = "fixture-installation-token", expires_at = DateTimeOffset.UtcNow.AddHours(1) })
                            : Json(new { account = new { login = "owner" } });
                    }

                    if (request.RequestUri!.AbsolutePath.EndsWith("/user", StringComparison.Ordinal))
                    {
                        return Json(new { login = "authentication-actor", username = "authentication-actor" });
                    }

                    if (request.RequestUri.AbsolutePath.EndsWith("/repos", StringComparison.Ordinal) ||
                        request.RequestUri.AbsolutePath.EndsWith("/projects", StringComparison.Ordinal) ||
                        request.RequestUri.AbsolutePath.EndsWith("/installation/repositories", StringComparison.Ordinal))
                    {
                        repositoryPages++;
                        if (mode == "enumeration404" && repositoryPages % 2 == 0)
                        {
                            return new(HttpStatusCode.NotFound);
                        }

                        var count = mode is "enumeration404" or "installationCap" ? 100 : 1;
                        var nativeRepositories = Enumerable.Range(0, count).Select(index => new
                        {
                            id = mode == "installationCap" ? (repositoryPages - 1) * 100 + index + 123 : index + 123,
                            name = "repo", full_name = "owner/repo", path = "repo", path_with_namespace = "owner/repo",
                            owner = new { login = "owner" }, @namespace = new { full_path = "owner" },
                        }).ToArray();
                        return mode == "installationCap" ? Json(new { repositories = nativeRepositories }) : Json(nativeRepositories);
                    }

                    if (hideReviews)
                    {
                        if (request.RequestUri.AbsolutePath.EndsWith("/237", StringComparison.Ordinal))
                        {
                            return queryUnavailable ? new(HttpStatusCode.NotFound) : Json(new { id = 237, number = 237, iid = 237, state = "unknown" });
                        }

                        return Json(Array.Empty<object>());
                    }

                    pages++;
                    var pageValue = request.RequestUri.Query.TrimStart('?').Split('&')
                        .FirstOrDefault(part => part.StartsWith("page=", StringComparison.Ordinal));
                    var page = pageValue is null ? 1 : int.Parse(pageValue[5..], System.Globalization.CultureInfo.InvariantCulture);
                    if (failSecondPage && page == 2)
                    {
                        return new(HttpStatusCode.TooManyRequests);
                    }

                    return Json(
                        Enumerable.Range((page - 1) * 100 + 1, page == 3 ? 37 : 100)
                            .Select(number => failSecondPage && number == 1 ? 237 : number).Select(number => new
                            {
                                id = number, number, iid = number, state = provider == ScmProvider.GitLab ? "opened" : "open", title = "Fixture",
                                head = new { @ref = "topic", sha = head }, @base = new { @ref = number == 237 ? "main" : "other", sha = "base" },
                                source_branch = "topic", target_branch = number == 237 ? "main" : "other", sha = head,
                                diff_refs = new { head_sha = head, base_sha = "base", start_sha = "start" },
                                requested_reviewers = new[] { new { id = 19, login = "configured", full_name = "Configured reviewer" } },
                                reviewers = new[] { new { id = 19, username = "configured", name = "Configured reviewer", re_requested = true } },
                            }).ToArray());
                })));
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["DB_CONNECTION_STRING"] = "Host=127.0.0.1;Database=fixture;Username=fixture",
                ["ADO_STUB_PR"] = mode == "stub" ? "true" : null,
            }).Build();
        services.AddLogging();
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddInfrastructureSupport(configuration);
        services.AddCrawlingModule(configuration);
        services.AddClientsModule(configuration);
        services.AddSingleton(connections);
        services.AddSingleton(scopes);
        services.AddSingleton(reviewers);
        services.AddSingleton(activation);
        services.AddSingleton(factory);
        services.AddSingleton(Substitute.For<IClientRegistry>());
        services.AddSingleton(Substitute.For<TokenCredential>());
        if (mode == "stub")
        {
            services.AddSingleton(new VssConnectionFactory(Substitute.For<TokenCredential>()));
        }

        if (mode == "repositoryGrant")
        {
            var nativeDiscovery = Substitute.For<AdoDiscoveryService>(new VssConnectionFactory(Substitute.For<TokenCredential>()), connections, scopes);
            nativeDiscovery.GetProjectsAsync(Arg.Any<VssConnection>(), Arg.Any<CancellationToken>()).Returns(
            [
                new TeamProjectReference { Id = Guid.NewGuid(), Name = "owner" }
            ]);
            nativeDiscovery.GetRepositoriesAsync(Arg.Any<VssConnection>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(
            [
                new GitRepository { Id = Guid.Parse(repositoryId), Name = "repo" }
            ]);
            services.Replace(ServiceDescriptor.Singleton<IProviderAdminDiscoveryService>(nativeDiscovery));
        }

        using var container = services.BuildServiceProvider();
        using var serviceScope = container.CreateScope();
        var registry = serviceScope.ServiceProvider.GetRequiredService<IScmProviderRegistry>();
        GitHttpClient? nativeGit = null;
        if (provider == ScmProvider.AzureDevOps)
        {
            var git = Substitute.For<GitHttpClient>(new Uri(scopePath), new VssCredentials());
            nativeGit = git;
            git.GetPullRequestsAsync(
                Arg.Any<string>(), repositoryId, Arg.Any<GitPullRequestSearchCriteria>(),
                Arg.Any<int?>(), Arg.Any<int?>(), 100, Arg.Any<object>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                if (hideReviews)
                {
                    return [];
                }

                if (failSecondPage && call.ArgAt<int?>(4) == 100)
                {
                    throw new HttpRequestException("Synthetic later-page throttle.", null, HttpStatusCode.TooManyRequests);
                }

                return Enumerable.Range((call.ArgAt<int?>(4) ?? 0) + 1, call.ArgAt<int?>(4) == 200 ? 37 : 100)
                    .Select(number => failSecondPage && number == 1 ? 237 : number).Select(number => new GitPullRequest
                    {
                        PullRequestId = number, Status = PullRequestStatus.Active, Title = "Fixture", SourceRefName = "refs/heads/topic",
                        TargetRefName = number == 237 ? "refs/heads/main" : "refs/heads/other",
                        Repository = new GitRepository { Id = Guid.Parse(repositoryId), Name = "repo" },
                        LastMergeSourceCommit = new GitCommitRef { CommitId = head }, LastMergeTargetCommit = new GitCommitRef { CommitId = "base" },
                        Reviewers = [new IdentityRefWithVote { Id = reviewerId, DisplayName = "Configured reviewer" }],
                    }).ToList();
            });
            git.GetPullRequestAsync(Arg.Any<string>(), repositoryId, 237, cancellationToken: Arg.Any<CancellationToken>()).Returns(_ =>
            {
                if (hideReviews && queryUnavailable)
                {
                    throw new HttpRequestException("Synthetic missing review.", null, HttpStatusCode.NotFound);
                }

                return new GitPullRequest
                {
                    PullRequestId = 237, Status = hideReviews ? PullRequestStatus.NotSet : PullRequestStatus.Active,
                    Repository = new GitRepository { Id = Guid.Parse(repositoryId), Name = "repo" }, TargetRefName = "refs/heads/main"
                };
            });
            git.GetPullRequestIterationsAsync(Arg.Any<string>(), repositoryId, 237, false, null, Arg.Any<CancellationToken>())
                .Returns(_ => [new GitPullRequestIteration { Id = nativeIteration }]);
            git.GetPullRequestIterationAsync(Arg.Any<string>(), repositoryId, 237, Arg.Any<int>(), cancellationToken: Arg.Any<CancellationToken>()).Returns(_ =>
                new GitPullRequestIteration
                    { Id = nativeIteration, SourceRefCommit = new GitCommitRef { CommitId = head }, CommonRefCommit = new GitCommitRef { CommitId = "base" } });
            Assert.IsType<AdoReviewDiscoveryProvider>(registry.GetReviewDiscoveryProvider(provider)).GitClientResolver = (_, _) => Task.FromResult(git);
            Assert.IsType<AdoCodeReviewQueryService>(registry.GetCodeReviewQueryService(provider)).GitClientResolver = (_, _) => Task.FromResult(git);
        }

        var targets = Substitute.For<ICrawlConfigurationRepository>();
        targets.GetAllActiveAsync(Arg.Any<CancellationToken>()).Returns([config]);
        targets.GetReviewTargetPolicySnapshotAsync(config.Id, clientId, Arg.Any<CancellationToken>()).Returns(config);
        targets.AcquireReviewTargetAdmissionAsync(clientId, Arg.Any<CancellationToken>()).Returns(new Lease());
        var jobs = Substitute.For<IJobRepository>();
        var accepted = new List<ReviewJob>();
        var superseded = new HashSet<Guid>();
        jobs.GetActiveJobsForConfigAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
            accepted.Where(job => job.ClientId == call.Arg<Guid>() && job.OrganizationUrl == call.ArgAt<string>(1) &&
                                  job.ProjectId == call.ArgAt<string>(2) && !superseded.Contains(job.Id)).ToArray());
        jobs.SetSupersededAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            superseded.Add(call.Arg<Guid>());
            return Task.CompletedTask;
        });
        jobs.TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            accepted.Add(call.Arg<ReviewJob>());
            return new TryAddReviewJobResult(true, null, 0);
        });
        var synchronizer = new CapturingSynchronization(
            new PullRequestSynchronizationService(registry, jobs, NullLogger<PullRequestSynchronizationService>.Instance));
        if (mode == "stub")
        {
            var existing = new ReviewJob(Guid.NewGuid(), clientId, scopePath, "owner", repositoryId, 237, 7);
            existing.SetProviderReviewContext(
                new(new(new(provider, scopePath), repositoryId, "owner", "owner", "repo"), CodeReviewPlatformKind.PullRequest, "237", 237));
            accepted.Add(existing);
            services.AddSingleton(targets);
            services.AddSingleton(jobs);
            services.AddSingleton<IPullRequestSynchronizationService>(synchronizer);
            services.AddSingleton(registry);
            using var stubContainer = services.BuildServiceProvider();
            using var stubScope = stubContainer.CreateScope();
            Assert.IsType<ReviewTargetAutomationService>(stubScope.ServiceProvider.GetRequiredService<IReviewTargetAutomationService>());
            await stubScope.ServiceProvider.GetRequiredService<IPrCrawlService>().CrawlAsync();
            Assert.Equal(PrStatus.Active, Assert.Single(synchronizer.Maintenance).PullRequestStatus);
            await nativeGit!.DidNotReceive().GetPullRequestAsync(Arg.Any<string>(), repositoryId, 237, cancellationToken: Arg.Any<CancellationToken>());
            return;
        }

        var crawler = new PrCrawlService(
            targets, serviceScope.ServiceProvider.GetRequiredService<IAssignedReviewDiscoveryService>(), jobs,
            Substitute.For<IPrStatusFetcher>(), NullLogger<PrCrawlService>.Instance, synchronizer, activation,
            providerRegistry: registry, scheduledAuthority: serviceScope.ServiceProvider.GetRequiredService<IScheduledReviewAuthorityService>());

        await crawler.CrawlAsync();

        if (mode is "enumeration404" or "installationCap")
        {
            Assert.Empty(accepted);
            Assert.Empty(synchronizer.Requests);
            await jobs.DidNotReceiveWithAnyArgs().GetActiveJobsForConfigAsync(default, default!, default!, default);
            return;
        }

        var job = Assert.Single(accepted);
        Assert.Equal(clientId, job.ClientId);
        Assert.Equal(237, job.PullRequestId);
        Assert.Equal("configured", Assert.Single(synchronizer.Requests).RequestedReviewerIdentity!.Login);
        Assert.Equal(reviewerId, synchronizer.Requests[0].RequestedReviewerIdentity!.ExternalUserId);
        Assert.Equal("head", job.ReviewRevisionReference!.HeadSha);
        Assert.Equal(provider == ScmProvider.AzureDevOps ? "7" : "head", job.ReviewRevisionReference.ProviderRevisionId);
        if (provider != ScmProvider.AzureDevOps)
        {
            Assert.Equal(3, pages);
        }

        if (mode != "canonical")
        {
            Assert.True(repositoryPages > 0 || provider == ScmProvider.AzureDevOps);
            Assert.Equal(scopePath, job.OrganizationUrl);
            Assert.Equal("owner", job.ProjectId);
            return;
        }

        await crawler.CrawlAsync();
        Assert.Single(accepted);
        head = "new-head";
        nativeIteration = 8;
        await crawler.CrawlAsync();
        Assert.Equal(2, accepted.Count);
        Assert.Contains(job.Id, superseded);
        Assert.Equal("new-head", accepted[1].ReviewRevisionReference!.HeadSha);

        var otherClient = Guid.NewGuid();
        var otherConnection = Guid.NewGuid();
        var otherConfig = config with { Id = Guid.NewGuid(), ClientId = otherClient };
        connections.GetOperationalSelectionCandidatesAsync(otherClient, Arg.Any<CancellationToken>())
            .Returns([selected with { Id = otherConnection, ClientId = otherClient }]);
        connections.GetOperationalConnectionByIdAsync(otherClient, otherConnection, Arg.Any<CancellationToken>()).Returns(
            new ClientScmConnectionCredentialDto(
                otherConnection, otherClient, provider, scopePath, ScmAuthenticationKind.PersonalAccessToken, "Fixture", "fixture-token", true));
        var originalGrant = (await scopes.GetByConnectionIdAsync(clientId, connectionId))[0];
        scopes.GetByConnectionIdAsync(otherClient, otherConnection, Arg.Any<CancellationToken>()).Returns(
            [originalGrant with { Id = Guid.NewGuid(), ClientId = otherClient, ConnectionId = otherConnection }]);
        var originalReviewer = await reviewers.GetByConnectionIdAsync(clientId, connectionId);
        reviewers.GetByConnectionIdAsync(otherClient, otherConnection, Arg.Any<CancellationToken>()).Returns(
            originalReviewer! with { Id = Guid.NewGuid(), ClientId = otherClient, ConnectionId = otherConnection });
        targets.GetAllActiveAsync(Arg.Any<CancellationToken>()).Returns([config, otherConfig]);
        targets.GetReviewTargetPolicySnapshotAsync(otherConfig.Id, otherClient, Arg.Any<CancellationToken>()).Returns(otherConfig);
        targets.AcquireReviewTargetAdmissionAsync(otherClient, Arg.Any<CancellationToken>()).Returns(new Lease());
        await crawler.CrawlAsync();
        Assert.Equal(3, accepted.Count);
        Assert.Equal("new-head", Assert.Single(accepted.Where(item => item.ClientId == otherClient)).ReviewRevisionReference!.HeadSha);
        var preparedCount = synchronizer.Requests.Count;
        jobs.ClearReceivedCalls();
        failSecondPage = true;
        await crawler.CrawlAsync();
        Assert.Equal(3, accepted.Count);
        Assert.Equal(preparedCount, synchronizer.Requests.Count);
        await jobs.DidNotReceiveWithAnyArgs().GetActiveJobsForConfigAsync(default, default!, default!, default);
        await jobs.DidNotReceiveWithAnyArgs().SetCancelledAsync(default, default);
        failSecondPage = false;
        hideReviews = true;
        foreach (var missing in new[] { false, true })
        {
            queryUnavailable = missing;
            jobs.ClearReceivedCalls();
            await crawler.CrawlAsync();
            Assert.Equal(3, accepted.Count);
            Assert.Equal(preparedCount, synchronizer.Requests.Count);
            await jobs.Received(2).GetActiveJobsForConfigAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
            await jobs.DidNotReceiveWithAnyArgs().SetCancelledAsync(default, default);
        }

        await connections.DidNotReceiveWithAnyArgs().GetOperationalConnectionAsync(default, default!, default);
    }

    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }

    private sealed class Lease : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CapturingSynchronization(IPullRequestSynchronizationService inner) : IPullRequestSynchronizationService
    {
        public List<PullRequestSynchronizationRequest> Requests { get; } = [];
        public List<PullRequestSynchronizationRequest> Maintenance { get; } = [];

        public Task<PreparedPullRequestSynchronization> PrepareAsync(PullRequestSynchronizationRequest request, CancellationToken ct = default)
        {
            this.Requests.Add(request);
            return inner.PrepareAsync(request, ct);
        }

        public Task<PullRequestSynchronizationOutcome> SynchronizeAsync(PullRequestSynchronizationRequest request, CancellationToken ct = default)
        {
            this.Maintenance.Add(request);
            return inner.SynchronizeAsync(request, ct);
        }
    }
}
