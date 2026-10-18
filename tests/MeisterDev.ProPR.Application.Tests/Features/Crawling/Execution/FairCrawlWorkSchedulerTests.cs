// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Crawling.Execution.Models;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;
using MeisterDev.ProPR.Domain.Enums;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Application.Tests.Features.Crawling.Execution;

public sealed class FairCrawlWorkSchedulerTests
{
    [Fact]
    public async Task ASlowClientDoesNotBlockAnIndependentClient()
    {
        var scheduler = Create();
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var otherStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clients = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var run = scheduler.RunAsync(
            clients, id => new(id, ScmProvider.AzureDevOps, "https://host.example/org"), async (id, _) =>
            {
                if (id == clients[0])
                {
                    await blocked.Task;
                }
                else
                {
                    otherStarted.SetResult();
                }
            });
        try
        {
            await otherStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            blocked.TrySetResult();
        }

        await run;
    }

    [Fact]
    public async Task FailedItemsDoNotPreventQueuedClientsFromRunning()
    {
        var scheduler = Create();
        var clients = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()).ToArray();
        var completed = 0;
        await Assert.ThrowsAsync<AggregateException>(() => scheduler.RunAsync(
            clients, id => new(id, ScmProvider.AzureDevOps, "https://host.example"),
            (id, _) =>
            {
                if (id == clients[0])
                {
                    throw new InvalidOperationException("failure");
                }

                Interlocked.Increment(ref completed);
                return Task.CompletedTask;
            }));
        Assert.Equal(19, completed);
    }

    [Fact]
    public async Task LimitsAndClientOrderingApplyAcrossAllWork()
    {
        var scheduler = Create();
        var clients = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()).ToArray();
        var work = clients.SelectMany((client, index) => Enumerable.Range(0, 3).Select(order => (Client: client, Host: index % 2, Order: order))).ToArray();
        var activeClients = new HashSet<Guid>();
        var activeHosts = new int[2];
        var orderByClient = new Dictionary<Guid, List<int>>();
        var active = 0;
        var sync = new object();
        await scheduler.RunAsync(
            work, item => new(item.Client, ScmProvider.AzureDevOps, $"https://host{item.Host}.example"), async (item, ct) =>
            {
                lock (sync)
                {
                    Assert.True(activeClients.Add(item.Client));
                    Assert.True(++active <= 8);
                    Assert.True(++activeHosts[item.Host] <= 4);
                    if (!orderByClient.TryGetValue(item.Client, out var order))
                    {
                        orderByClient[item.Client] = order = [];
                    }

                    order.Add(item.Order);
                }

                await Task.Delay(2, ct);
                lock (sync)
                {
                    activeClients.Remove(item.Client);
                    active--;
                    activeHosts[item.Host]--;
                }
            });
        Assert.All(orderByClient.Values, order => Assert.Equal([0, 1, 2], order));
        Assert.Equal(20, orderByClient.Count);
    }

    [Fact]
    public async Task CancellationDrainsStartedWork()
    {
        var scheduler = Create();
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var run = scheduler.RunAsync(
            new[] { Guid.NewGuid(), Guid.NewGuid() }, id => new(id, ScmProvider.AzureDevOps, "https://host.example"), async (_, ct) =>
            {
                Interlocked.Increment(ref active);
                started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            }, cancellation.Token);
        await started.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(0, active);
    }

    [Fact]
    public async Task AvailableClientCapacityStartsBothItemsBeforeEitherCompletes()
    {
        var options = Microsoft.Extensions.Options.Options.Create(
            new DiscussionSynchronizationOptions
            {
                GlobalConcurrency = 2, ProviderHostConcurrency = 2, ClientProviderHostConcurrency = 2,
            });
        var scheduler = new FairCrawlWorkScheduler(options);
        var client = Guid.NewGuid();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        var run = scheduler.RunAsync(
            new[] { 1, 2 }, _ => new(client, ScmProvider.AzureDevOps, "https://host.example"), async (_, ct) =>
            {
                if (Interlocked.Increment(ref count) == 2)
                {
                    started.TrySetResult();
                }

                await release.Task.WaitAsync(ct);
            });
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.TrySetResult();
            await run;
        }
    }

    private static FairCrawlWorkScheduler Create() => new(Microsoft.Extensions.Options.Options.Create(new DiscussionSynchronizationOptions()));
}
