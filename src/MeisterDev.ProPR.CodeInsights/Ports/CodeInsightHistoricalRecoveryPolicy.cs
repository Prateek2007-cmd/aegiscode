// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.Domain.Entities;

namespace MeisterDev.ProPR.CodeInsights.Ports;

/// <summary>Shares automatic retry eligibility with captured recovery diagnostics.</summary>
internal static class CodeInsightHistoricalRecoveryPolicy
{
    internal static readonly string[] RepairRequiredReasons =
    [
        nameof(CodeInsightObservationUnavailableReason.SourceCoordinatesUnavailable),
        nameof(CodeInsightObservationUnavailableReason.ProviderConnectionUnavailable),
        nameof(CodeInsightObservationUnavailableReason.ProviderIdentityUnavailable),
        nameof(CodeInsightObservationUnavailableReason.HistoricalCapabilityUnsupported),
        nameof(CodeInsightObservationUnavailableReason.ObserverUnavailable),
    ];

    internal static bool RequiresRepair(string? reason) => reason is not null && RepairRequiredReasons.Contains(reason, StringComparer.Ordinal);

    internal static CodeInsightObservationResult PreferFailure(CodeInsightObservationResult? retained, CodeInsightObservationResult candidate) =>
        retained is null || (!RequiresRepair(retained.UnavailableReason?.ToString()) && RequiresRepair(candidate.UnavailableReason?.ToString()))
            ? candidate
            : retained;

    internal static string? SafeReason(string? reason) => reason is null ? null
        : Enum.TryParse<CodeInsightObservationUnavailableReason>(reason, out var known) && Enum.IsDefined(known) && known.ToString() == reason ? reason
        : reason == "CollectionDisabled" ? reason : "Unknown";

    internal static string Disposition(CodeInsightPullRequest? aggregate, bool? enabled, DateTimeOffset now)
    {
        var reason = aggregate?.LastHistoricalRefreshReason;
        if (enabled == false || (enabled is null && reason == "CollectionDisabled"))
        {
            return "collection-disabled";
        }

        if (RequiresRepair(reason))
        {
            return "repair-required";
        }

        if (reason is not null)
        {
            return "retryable";
        }

        if (aggregate?.LastHistoricalRefreshAttemptId is null && aggregate?.HistoricalRefreshNotBefore is not null)
        {
            return "queued";
        }

        if (aggregate?.LastHistoricalRefreshAttemptAt is { } started && aggregate.LastHistoricalRefreshCompletedAt >= started)
        {
            return "complete";
        }

        return aggregate?.LastHistoricalRefreshAttemptId is not null
               && aggregate.LastHistoricalRefreshAttemptAt is { } attempted
               && !(aggregate.LastHistoricalRefreshCompletedAt >= attempted)
               && aggregate.HistoricalRefreshNotBefore > now
            ? "in-progress"
            : "ready";
    }
}
