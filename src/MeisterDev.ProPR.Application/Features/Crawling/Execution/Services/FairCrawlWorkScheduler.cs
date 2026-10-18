// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Crawling.Execution.Models;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Ports;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;

/// <summary>Dispatches eligible client lanes fairly without occupying slots while waiting for host capacity.</summary>
public sealed class FairCrawlWorkScheduler(IOptions<DiscussionSynchronizationOptions> options) : ICrawlWorkScheduler
{
    private readonly SemaphoreSlim _dispatch = new(1, 1);
    private readonly Dictionary<string, int> _hosts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _clients = new(StringComparer.Ordinal);
    private int _active;
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task RunAsync<T>(
        IReadOnlyList<T> work, Func<T, CrawlWorkScope> scope, Func<T, CancellationToken, Task> execute, CancellationToken ct = default)
    {
        var lanes = new Queue<Queue<T>>(work.GroupBy(item => ClientKey(scope(item))).Select(group => new Queue<T>(group)));
        var running = new List<Task>();
        var failures = new List<Exception>();
        try
        {
            while (lanes.Count > 0 || running.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var changed = await this.DispatchAvailableAsync(lanes, running, scope, execute, ct);
                await ObserveCompletedAsync(running, failures);

                if (lanes.Count > 0 || running.Count > 0)
                {
                    await Task.WhenAny(running.Append(changed)).WaitAsync(ct);
                }
            }
        }
        finally
        {
            await DrainAsync(running, failures);
        }

        ct.ThrowIfCancellationRequested();
        if (failures.Count > 0)
        {
            throw new AggregateException("One or more crawl operations failed.", failures);
        }
    }

    private async Task<Task> DispatchAvailableAsync<T>(
        Queue<Queue<T>> lanes, List<Task> running, Func<T, CrawlWorkScope> scope,
        Func<T, CancellationToken, Task> execute, CancellationToken ct)
    {
        await this._dispatch.WaitAsync(ct);
        try
        {
            var admitted = 0;
            do
            {
                admitted = 0;
                var candidates = lanes.Count;
                while (candidates-- > 0 && lanes.Count > 0 && this._active < Math.Max(1, options.Value.GlobalConcurrency))
                {
                    var lane = lanes.Dequeue();
                    var item = lane.Peek();
                    var identity = scope(item);
                    var host = HostKey(identity);
                    var client = ClientKey(identity);
                    if (this.HasCapacity(host, client))
                    {
                        lane.Dequeue();
                        this._active++;
                        this._hosts[host] = this._hosts.GetValueOrDefault(host) + 1;
                        this._clients[client] = this._clients.GetValueOrDefault(client) + 1;
                        running.Add(this.ExecuteAsync(item, host, client, execute, ct));
                        admitted++;
                    }

                    if (lane.Count > 0)
                    {
                        lanes.Enqueue(lane);
                    }
                }
            } while (admitted > 0 && lanes.Count > 0 && this._active < Math.Max(1, options.Value.GlobalConcurrency));

            return this._changed.Task;
        }
        finally
        {
            this._dispatch.Release();
        }
    }

    private bool HasCapacity(string host, string client) =>
        this._hosts.GetValueOrDefault(host) < Math.Max(1, options.Value.ProviderHostConcurrency) &&
        this._clients.GetValueOrDefault(client) < Math.Max(1, options.Value.ClientProviderHostConcurrency);

    private static async Task ObserveCompletedAsync(List<Task> running, List<Exception> failures)
    {
        foreach (var task in running.Where(task => task.IsCompleted).ToArray())
        {
            running.Remove(task);
            try
            {
                await task;
            }
            catch (Exception failure)
            {
                failures.Add(failure);
            }
        }
    }

    private static async Task DrainAsync(List<Task> running, List<Exception> failures)
    {
        try
        {
            await Task.WhenAll(running);
        }
        catch (Exception failure)
        {
            failures.Add(failure);
        }
    }

    private async Task ExecuteAsync<T>(T item, string host, string client, Func<T, CancellationToken, Task> execute, CancellationToken ct)
    {
        // Yield so the dispatcher releases its synchronization lock before user code can complete.
        await Task.Yield();
        try
        {
            await execute(item, ct);
        }
        finally
        {
            await this._dispatch.WaitAsync(CancellationToken.None);
            try
            {
                this._active--;
                this._hosts[host]--;
                this._clients[client]--;
                var changed = this._changed;
                this._changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                changed.TrySetResult();
            }
            finally
            {
                this._dispatch.Release();
            }
        }
    }

    private static string HostKey(CrawlWorkScope scope) => scope.Provider + ":" +
                                                           (Uri.TryCreate(scope.Host, UriKind.Absolute, out var uri)
                                                               ? uri.Authority.ToLowerInvariant()
                                                               : scope.Host.ToLowerInvariant());

    private static string ClientKey(CrawlWorkScope scope) => scope.ClientId + ":" + HostKey(scope);
}
