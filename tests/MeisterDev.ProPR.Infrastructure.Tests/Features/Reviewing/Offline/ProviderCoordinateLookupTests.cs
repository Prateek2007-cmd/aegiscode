// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Offline;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Offline;

public sealed class ProviderCoordinateLookupTests
{
    [Fact]
    public async Task EquivalentCoordinatesFindStoredBlocksWithoutCrossingClientsOrHosts()
    {
        IBlockedPullRequestStore store = new InMemoryBlockedPullRequestStore();
        var client = Guid.NewGuid();
        await store.BlockAsync(client, "https://dev.azure.com/Org/", "Project", "Repo", 42, Guid.NewGuid(), null);
        Assert.True(await store.IsBlockedByProviderCoordinatesAsync(client, ScmProvider.AzureDevOps, "https://dev.azure.com/org", "project", "repo", 42));
        Assert.False(
            await store.IsBlockedByProviderCoordinatesAsync(Guid.NewGuid(), ScmProvider.AzureDevOps, "https://dev.azure.com/org", "project", "repo", 42));
        Assert.False(await store.IsBlockedByProviderCoordinatesAsync(client, ScmProvider.AzureDevOps, "https://dev.azure.com/another", "project", "repo", 42));
    }

    [Fact]
    public async Task EquivalentCoordinatesFindActiveJobsWithoutReturningCompletedJobs()
    {
        IJobRepository store = new InMemoryReviewJobRepository();
        var client = Guid.NewGuid();
        var active = new ReviewJob(Guid.NewGuid(), client, "https://dev.azure.com/Org/", "Project", "Repo", 42, 1);
        var completed = new ReviewJob(Guid.NewGuid(), client, "https://dev.azure.com/Org/", "Project", "Repo", 42, 2) { Status = JobStatus.Completed };
        await store.AddAsync(active);
        await store.AddAsync(completed);
        Assert.Equal(
            active.Id, Assert.Single(
                await store.FindActiveByProviderCoordinatesAsync(
                    client, ScmProvider.AzureDevOps,
                    "https://dev.azure.com/org", "project", "repo", 42)).Id);
        Assert.Empty(
            await store.FindActiveByProviderCoordinatesAsync(Guid.NewGuid(), ScmProvider.AzureDevOps, "https://dev.azure.com/org", "project", "repo", 42));
        Assert.Empty(await store.FindActiveByProviderCoordinatesAsync(client, ScmProvider.AzureDevOps, "https://dev.azure.com/another", "project", "repo", 42));
    }
}
