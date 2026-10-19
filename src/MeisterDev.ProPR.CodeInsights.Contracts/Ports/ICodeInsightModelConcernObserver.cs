// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Events;

namespace MeisterDev.ProPR.CodeInsights.Contracts;

/// <summary>Independent concern retention and model attribution have separate completeness results.</summary>
public sealed record ModelConcernAcknowledgment(bool ConcernRetained, bool ModelsCurrent, bool Changed = false);

public interface ICodeInsightModelConcernObserver
{
    string DependencyVersion { get; }
    Task<string> PrepareAsync(CodeInsightDiscussionBatch batch, CancellationToken ct = default);
    Task<ModelConcernAcknowledgment> ObserveAsync(ThreadUpdatedEvent observation, CancellationToken ct = default);

    Task<bool> CompleteEnumerationAsync(
        CodeInsightPullRequestKey key, string providerScope, Guid connectionId,
        DateTimeOffset observedAt, bool complete, IReadOnlyList<string> observedThreadIds, CancellationToken ct = default);

    Task<bool> ReplayRetainedAsync(CodeInsightPullRequestKey key, string providerScope, Guid connectionId, CancellationToken ct = default) =>
        Task.FromResult(false);
}
