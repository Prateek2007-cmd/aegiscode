// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Extensibility.Execution;

/// <summary>The kind of work an execution performs.</summary>
public enum ExecutionKind
{
    /// <summary>A review of one pull request revision.</summary>
    Review = 0,

    /// <summary>A pass over the reviewer's threads on one pull request: judging fixes and answering replies.</summary>
    ThreadPass = 1,

    /// <summary>An answer to a comment that mentions the reviewer.</summary>
    MentionReply = 2,
}

/// <summary>Identifies one attempt at one unit of work.</summary>
/// <param name="Kind">The kind of work.</param>
/// <param name="Id">
///     The identifier ProPR assigned to the work. A restarted review receives a new identifier; a restarted thread
///     pass keeps its identifier and increases <paramref name="Attempt" />.
/// </param>
/// <param name="Attempt">
///     The attempt number, starting at 1. It increases each time ProPR runs the same work again, and stays the same
///     when ProPR only retries a transport or recovers an interrupted lease.
/// </param>
public sealed record ExecutionIdentity(ExecutionKind Kind, Guid Id, int Attempt);
