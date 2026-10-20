// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Concurrent;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Extensibility.Execution;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Application.Features.ExecutionAuthority;

/// <summary>
///     Stores the usage of every AI call and passes it to the registered execution authority. Without a registered
///     authority it does nothing, and the AI runtime does not add the stage that calls it.
/// </summary>
/// <remarks>
///     The report is stored before the authority is called, so a report the authority did not receive can be read
///     again from the feed. An authority that answers with a stop ends the execution the call belongs to: the
///     ambient scope refuses the next call of that execution, and the stopper records the stop on the job.
/// </remarks>
public sealed partial class ExecutionUsageSink(
    IExecutionScopeAccessor scopes,
    IExecutionStopper stopper,
    ILogger<ExecutionUsageSink> logger,
    IUsageReportStore? store = null,
    IClientRegistry? clients = null,
    IExecutionAuthority? authority = null) : IExecutionUsageSink
{
    // A client never moves to another tenant, so its tenant is read once per process.
    private static readonly ConcurrentDictionary<Guid, Guid> TenantByClient = new();

    /// <inheritdoc />
    public bool IsActive => authority is not null;

    /// <inheritdoc />
    public async Task RecordAsync(AiCallUsage usage, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(usage);
        if (authority is null)
        {
            return;
        }

        var scope = scopes.Current;
        var clientId = scope?.ClientId ?? usage.ConnectionClientId;
        var tenantId = scope?.TenantId ?? usage.ConnectionTenantId ?? await this.TenantOfAsync(clientId, ct);

        var report = new UsageReport(
            0,
            scope?.Identity,
            tenantId,
            clientId,
            new AiConnectionRevision(usage.ConnectionId, usage.ConnectionRevision),
            usage.ProviderKind,
            usage.ModelId,
            usage.Operation,
            usage.Usage.InputTokens,
            usage.Usage.OutputTokens,
            usage.Usage.CachedInputTokens,
            usage.Usage.CacheWriteTokens,
            usage.Usage.ReasoningTokens,
            usage.Usage.IsEstimated,
            usage.Duration,
            usage.CompletedAt);

        // A host without a database has no store and no feed; the authority then receives the report unsequenced.
        var stored = store is null ? report : await store.AppendAsync(report, ct);

        UsageAnswer answer;
        try
        {
            answer = await authority.ReportUsageAsync(stored, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Work already admitted continues while the authority cannot be reached; the stored report remains in
            // the feed for the authority to read once it can.
            LogReportFailed(logger, stored.Sequence, ex);
            return;
        }

        if (answer.Action != UsageAction.Stop)
        {
            return;
        }

        var message = string.IsNullOrWhiteSpace(answer.Message) ? "The execution authority stopped this execution." : answer.Message;
        if (scope is null)
        {
            // A call outside an execution has nothing to stop: index updates and connection checks end on their own.
            LogStopWithoutExecution(logger, stored.Sequence);
            return;
        }

        if (scope.RequestStop(message))
        {
            LogExecutionStopped(logger, scope.Identity.Kind, scope.Identity.Id, answer.Reason ?? string.Empty);
            await stopper.StopAsync(scope.Identity, message, ct);
        }
    }

    private async Task<Guid?> TenantOfAsync(Guid? clientId, CancellationToken ct)
    {
        if (clientId is not { } client || clients is null)
        {
            return null;
        }

        if (TenantByClient.TryGetValue(client, out var known))
        {
            return known;
        }

        var tenant = await clients.GetTenantIdAsync(client, ct);
        if (tenant is { } found)
        {
            TenantByClient[client] = found;
        }

        return tenant;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Usage report {Sequence} could not be passed to the execution authority; it remains in the feed")]
    private static partial void LogReportFailed(ILogger logger, long sequence, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning, Message = "The execution authority answered usage report {Sequence} with a stop, but the call belongs to no execution")]
    private static partial void LogStopWithoutExecution(ILogger logger, long sequence);

    [LoggerMessage(Level = LogLevel.Information, Message = "The execution authority stopped {Kind} {ExecutionId} ({Reason})")]
    private static partial void LogExecutionStopped(ILogger logger, ExecutionKind kind, Guid executionId, string reason);
}
