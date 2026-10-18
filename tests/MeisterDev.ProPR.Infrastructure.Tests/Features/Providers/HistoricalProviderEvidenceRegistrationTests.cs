// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;
using MeisterDev.ProPR.Application.Features.ReviewArchive;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.CodeInsights;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.CodeInsights.Ports;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Features.Crawling;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Providers;

public sealed class HistoricalProviderEvidenceRegistrationTests
{
    [Fact]
    public async Task OwningModulesResolveAndInvokeHistoricalPortsAlongsideOrdinaryClose()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(LocalScmPolicies.Registry);
        services.AddHttpClient();
        var databaseName = Guid.NewGuid().ToString();
        services.AddDbContextFactory<MeisterProPRDbContext>(options => options.UseInMemoryDatabase(databaseName));
        services.AddSingleton(Substitute.For<ISecretProtectionCodec>());
        services.AddSingleton(Substitute.For<IAiRuntimeResolver>());
        services.AddSingleton(Substitute.For<IModelUsageRecorder>());
        services.AddSingleton(Substitute.For<IClientScmConnectionRepository>());
        services.AddSingleton(Substitute.For<IPostedCommentOriginStore>());
        services.AddSingleton(Substitute.For<IPullRequestFetcher>());
        services.AddSingleton(Substitute.For<IReviewerThreadStatusFetcher>());
        var statuses = Substitute.For<IProviderReviewerThreadStatusFetcher>();
        statuses.Provider.Returns(ScmProvider.AzureDevOps);
        services.AddSingleton(statuses);
        var threads = Substitute.For<IProviderPullRequestFetcher>();
        threads.Provider.Returns(ScmProvider.AzureDevOps);
        services.AddSingleton(threads);
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["DB_CONNECTION_STRING"] = "Host=localhost;Database=unused;Username=unused",
                ["CODE_INSIGHTS_PROVIDER_EVIDENCE_BACKFILL_MAX_PULL_REQUESTS"] = "9",
            }).Build();
        services.AddCrawlingModule(config);
        services.AddCodeInsightsModule(config);
        var gate = Substitute.For<ICodeInsightsCollectionGate>();
        services.AddSingleton(gate);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.IsType<PullRequestCloseObserver>(scope.ServiceProvider.GetRequiredService<ICodeInsightCloseObserver>());
        var historical = scope.ServiceProvider.GetRequiredService<ICodeInsightHistoricalObserver>();
        Assert.IsType<PullRequestCloseObserver>(historical);
        Assert.IsType<CodeInsightProviderFeedbackReader>(scope.ServiceProvider.GetRequiredService<ICodeInsightProviderFeedbackReader>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ICodeInsightHistoricalRefreshStore>());
        Assert.Equal(9, provider.GetRequiredService<IOptions<CodeInsightsOptions>>().Value.EffectiveProviderEvidenceBackfillMaxPullRequests);
        Assert.Equal(
            CodeInsightObservationStatus.Disabled, (await historical.RefreshAsync(
                new CodeInsightProviderEvidenceTarget(
                    new CodeInsightPullRequestKey(Guid.NewGuid(), "repo", 1),
                    ScmProvider.AzureDevOps, "https://ado.example.test/collection", "project", "AzureDevOps:https://ado.example.test/collection"))).Status);
        Assert.Equal(0, (await scope.ServiceProvider.GetRequiredService<ICodeInsightProviderEvidenceBackfill>().SweepAsync(9)).Attempted);
    }
}
