// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.SecurityScanning.Models;
using MeisterDev.ProPR.SecurityScanning.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.SecurityScanning.Workers;

/// <summary>
///     Background worker that runs the Dependency Radar on a schedule: about
///     once an hour it checks every client whose watch is enabled and due, and
///     runs one radar pass for it. One client failing never aborts the rest of
///     the sweep, and a failed sweep never tears down the loop. The worker only
///     does real work when the module and the radar are enabled; otherwise
///     sweeps are skipped. True 24/7 watching needs the API hosted and always
///     on — on a laptop that sleeps, the radar simply runs whenever the API is up.
/// </summary>
public sealed partial class RadarWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<SecurityScanningOptions> options,
    ILogger<RadarWorker> logger) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MinSweepInterval = TimeSpan.FromSeconds(60);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(options.Value.RadarSweepSeconds, MinSweepInterval.TotalSeconds));
        LogWorkerStarted(logger, interval.TotalSeconds);

        try
        {
            // Let the API finish starting before the first sweep.
            await Task.Delay(StartupDelay, stoppingToken);
            await this.SweepOnceAsync(stoppingToken);

            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await this.SweepOnceAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }

        LogWorkerStopped(logger);
    }

    private async Task SweepOnceAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        if (!opts.Enabled || !opts.RadarEnabled)
        {
            LogRadarDisabled(logger);
            return;
        }

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
            var radar = scope.ServiceProvider.GetRequiredService<IRadarService>();

            var clientIds = await db.SecurityScans
                .AsNoTracking()
                .Where(s => s.Status == (int)SecurityScanStatus.Succeeded
                            || s.Status == (int)SecurityScanStatus.PartiallySucceeded)
                .Select(s => s.ClientId)
                .Distinct()
                .ToListAsync(stoppingToken);

            var passes = 0;
            var alerts = 0;
            foreach (var clientId in clientIds)
            {
                stoppingToken.ThrowIfCancellationRequested();
                try
                {
                    var result = await radar.RunScheduledPassAsync(clientId, stoppingToken);
                    if (result is null)
                    {
                        continue;
                    }

                    passes++;
                    alerts += result.NewAlerts;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One client failing must not abort the rest of the sweep.
                    LogClientPassFailed(logger, clientId, ex);
                }
            }

            LogSweepCompleted(logger, clientIds.Count, passes, alerts);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failed sweep must not tear down the worker loop.
            LogSweepFailed(logger, ex);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Dependency Radar worker started (sweep interval: {IntervalSeconds:F0}s)")]
    private static partial void LogWorkerStarted(ILogger logger, double intervalSeconds);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Dependency Radar worker stopped")]
    private static partial void LogWorkerStopped(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Dependency Radar sweep completed (clients with scans: {ClientCount}, passes run: {PassCount}, new alerts: {AlertCount})")]
    private static partial void LogSweepCompleted(ILogger logger, int clientCount, int passCount, int alertCount);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Dependency Radar is disabled — sweep skipped")]
    private static partial void LogRadarDisabled(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Dependency Radar pass failed for client {ClientId}")]
    private static partial void LogClientPassFailed(ILogger logger, Guid clientId, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Dependency Radar sweep cycle failed")]
    private static partial void LogSweepFailed(ILogger logger, Exception ex);
}
