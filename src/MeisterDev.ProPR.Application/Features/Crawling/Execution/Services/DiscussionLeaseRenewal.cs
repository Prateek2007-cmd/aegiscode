// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Crawling.Execution.Ports;

namespace MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;

/// <summary>Renews an observation lease and cancels its work when ownership is lost.</summary>
internal sealed class DiscussionLeaseRenewal : IAsyncDisposable
{
    private readonly IDiscussionSynchronizationLease _lease;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _operation;
    private readonly CancellationTokenSource _stop;
    private readonly Task _renewal;

    public DiscussionLeaseRenewal(IDiscussionSynchronizationLease lease, TimeProvider timeProvider, CancellationToken ct)
    {
        this._lease = lease;
        this._timeProvider = timeProvider;
        this._operation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        this._stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        this._renewal = this.RenewAsync();
    }

    public CancellationToken Token => this._operation.Token;
    public Exception? Failure { get; private set; }

    public async Task StopAsync()
    {
        await this._stop.CancelAsync();
        await this._renewal;
    }

    public void ThrowIfLost()
    {
        if (this.Failure is not null)
        {
            throw new InvalidOperationException("The discussion observation lease was lost.", this.Failure);
        }
    }

    private async Task RenewAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), this._timeProvider, this._stop.Token);
                await this._lease.EnsureCurrentAsync(this._stop.Token);
            }
        }
        catch (OperationCanceledException) when (this._stop.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            this.Failure = exception;
            await this._operation.CancelAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await this.StopAsync();
        this._stop.Dispose();
        this._operation.Dispose();
    }
}
