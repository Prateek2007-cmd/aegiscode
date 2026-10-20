// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Extensibility.Execution;

namespace MeisterDev.ProPR.Application.Features.ExecutionAuthority;

/// <summary>What ProPR does with an execution after asking the execution authority.</summary>
/// <param name="Outcome">Whether the execution starts, waits or ends.</param>
/// <param name="Reason">The authority's machine-readable code, or ProPR's own when the authority could not be asked.</param>
/// <param name="Message">The text recorded with a held or refused execution.</param>
/// <param name="RetryAt">When a held execution is asked about again. Null unless the execution is held.</param>
public sealed record ExecutionAdmissionResult(
    AdmissionOutcome Outcome,
    string? Reason,
    string? Message,
    DateTimeOffset? RetryAt)
{
    /// <summary>The result that lets the execution start.</summary>
    public static ExecutionAdmissionResult Allowed { get; } = new(AdmissionOutcome.Allow, null, null, null);

    /// <summary>Creates a result that holds the execution until <paramref name="retryAt" />.</summary>
    public static ExecutionAdmissionResult HoldUntil(DateTimeOffset retryAt, string reason, string message) =>
        new(AdmissionOutcome.Hold, reason, message, retryAt);

    /// <summary>Creates a result that ends the execution without an AI call.</summary>
    public static ExecutionAdmissionResult Refused(string reason, string message) =>
        new(AdmissionOutcome.Refuse, reason, message, null);
}
