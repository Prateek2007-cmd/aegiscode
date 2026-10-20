// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Extensibility.Execution;

/// <summary>What an execution authority decides about an execution.</summary>
public enum AdmissionOutcome
{
    /// <summary>The execution may start.</summary>
    Allow = 0,

    /// <summary>The execution waits, and ProPR asks again after the retry delay.</summary>
    Hold = 1,

    /// <summary>The execution does not run. ProPR ends it without an AI call.</summary>
    Refuse = 2,
}

/// <summary>An execution authority's answer to an admission request.</summary>
/// <param name="Outcome">Whether the execution starts, waits or ends.</param>
/// <param name="Reason">
///     A short machine-readable code the authority chooses, for example <c>author_limit_reached</c>. Null for an
///     allowed execution.
/// </param>
/// <param name="Message">
///     Text for the people who see the outcome. ProPR shows it with a held or refused execution and posts the text
///     of a refusal on the pull request.
/// </param>
/// <param name="RetryAfter">
///     How long a held execution waits before ProPR asks again. ProPR applies a minimum and a maximum delay.
/// </param>
public sealed record AdmissionDecision(
    AdmissionOutcome Outcome,
    string? Reason = null,
    string? Message = null,
    TimeSpan? RetryAfter = null)
{
    /// <summary>An answer that lets the execution start.</summary>
    public static AdmissionDecision Allow { get; } = new(AdmissionOutcome.Allow);

    /// <summary>Creates an answer that holds the execution.</summary>
    /// <param name="reason">A short machine-readable code.</param>
    /// <param name="message">Text for the people who see the hold.</param>
    /// <param name="retryAfter">How long to wait before ProPR asks again.</param>
    public static AdmissionDecision Hold(string reason, string message, TimeSpan retryAfter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return new AdmissionDecision(AdmissionOutcome.Hold, reason, message, retryAfter);
    }

    /// <summary>Creates an answer that ends the execution without an AI call.</summary>
    /// <param name="reason">A short machine-readable code.</param>
    /// <param name="message">Text for the people who see the refusal, posted on the pull request.</param>
    public static AdmissionDecision Refuse(string reason, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return new AdmissionDecision(AdmissionOutcome.Refuse, reason, message);
    }
}
