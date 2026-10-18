// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.CodeInsights.Contracts;

/// <summary>One pull-request finding and configuration snapshot shared by collection consumers.</summary>
public sealed record CodeInsightDiscussionBatch(
    CodeInsightPullRequestKey Key,
    string ProviderScope,
    IReadOnlyList<CodeInsightFindingView> Findings,
    string DependencyVersion,
    Guid ConnectionId = default);

public interface ICodeInsightDiscussionBatchFactory
{
    Task<CodeInsightDiscussionBatch?> CreateAsync(CodeInsightPullRequestKey key, string providerScope, CancellationToken ct = default);
}
