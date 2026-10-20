// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Extensibility.Execution;

/// <summary>The kind of AI call a usage report describes.</summary>
public enum AiOperation
{
    /// <summary>A chat completion.</summary>
    Chat = 0,

    /// <summary>An embedding request.</summary>
    Embedding = 1,
}

/// <summary>The usage of one completed AI call, as ProPR stored it.</summary>
/// <param name="Sequence">
///     The position of the report in ProPR's stored usage sequence. Sequences increase; gaps are possible.
/// </param>
/// <param name="Execution">
///     The execution the call belongs to, or null for a call outside a review, a thread pass or a mention reply,
///     for example an index update or a connection check.
/// </param>
/// <param name="TenantId">The tenant that owns the connection or the execution, when ProPR knows it.</param>
/// <param name="ClientId">The client that owns the connection or the execution, when ProPR knows it.</param>
/// <param name="Connection">The AI connection the call used and its configuration revision.</param>
/// <param name="ProviderKind">The provider family of the connection, by its identity key.</param>
/// <param name="ModelId">The model the provider was asked for.</param>
/// <param name="Operation">Whether the call was a chat completion or an embedding request.</param>
/// <param name="InputTokens">Input tokens the provider reported, including cached input.</param>
/// <param name="OutputTokens">Output tokens the provider reported, including reasoning output.</param>
/// <param name="CachedInputTokens">Input tokens the provider served from its prompt cache.</param>
/// <param name="CacheWriteTokens">Input tokens the provider wrote to its prompt cache.</param>
/// <param name="ReasoningTokens">Output tokens the provider spent on reasoning.</param>
/// <param name="UsageIncomplete">
///     True when the provider's response carried no usage, so the token counts are zero and were not measured.
/// </param>
/// <param name="Duration">How long the call took.</param>
/// <param name="CompletedAt">When the call completed.</param>
public sealed record UsageReport(
    long Sequence,
    ExecutionIdentity? Execution,
    Guid? TenantId,
    Guid? ClientId,
    AiConnectionRevision Connection,
    string ProviderKind,
    string ModelId,
    AiOperation Operation,
    long InputTokens,
    long OutputTokens,
    long CachedInputTokens,
    long CacheWriteTokens,
    long ReasoningTokens,
    bool UsageIncomplete,
    TimeSpan Duration,
    DateTimeOffset CompletedAt);

/// <summary>Whether an execution may continue after an AI call.</summary>
public enum UsageAction
{
    /// <summary>The execution continues.</summary>
    Continue = 0,

    /// <summary>The execution makes no further AI call and ends.</summary>
    Stop = 1,
}

/// <summary>An execution authority's answer to a usage report.</summary>
/// <param name="Action">Whether the execution continues.</param>
/// <param name="Reason">A short machine-readable code for a stop. Null when the execution continues.</param>
/// <param name="Message">Text ProPR records with a stopped execution. Null when the execution continues.</param>
public sealed record UsageAnswer(UsageAction Action, string? Reason = null, string? Message = null)
{
    /// <summary>An answer that lets the execution continue.</summary>
    public static UsageAnswer Continue { get; } = new(UsageAction.Continue);

    /// <summary>Creates an answer that stops the execution.</summary>
    /// <param name="reason">A short machine-readable code.</param>
    /// <param name="message">Text ProPR records with the stopped execution.</param>
    public static UsageAnswer Stop(string reason, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return new UsageAnswer(UsageAction.Stop, reason, message);
    }
}

/// <summary>Reads the usage reports ProPR stored in sequence, so a reader that missed reports can catch up.</summary>
/// <remarks>ProPR registers an implementation whenever an execution authority is registered.</remarks>
public interface IUsageReportFeed
{
    /// <summary>
    ///     Returns stored usage reports with a sequence greater than <paramref name="afterSequence" />, in sequence
    ///     order. A report is returned once it was stored at least ten seconds ago, so a reader that advances its
    ///     cursor does not pass a report whose storage had not completed when it read.
    /// </summary>
    /// <param name="afterSequence">The last sequence the caller has processed; 0 reads from the start.</param>
    /// <param name="limit">The most reports to return, between 1 and 1000.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<IReadOnlyList<UsageReport>> ReadAsync(long afterSequence, int limit, CancellationToken cancellationToken);
}
