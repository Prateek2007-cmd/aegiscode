// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.ThreadOwnership;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

/// <summary>Selects provider capabilities for ordinary close and strict historical feedback.</summary>
internal sealed class CodeInsightProviderFeedbackReader(
    IEnumerable<IProviderPullRequestFetcher> threadFetchers,
    IEnumerable<IProviderReviewerThreadStatusFetcher> statusFetchers) : ICodeInsightProviderFeedbackReader
{
    public async Task<CodeInsightProviderFeedback> ReadAsync(
        CodeInsightProviderEvidenceTarget target,
        ThreadOwnershipResolver ownership, CancellationToken ct = default, CodeInsightObservationMode mode = CodeInsightObservationMode.HistoricalBackfill)
    {
        ct.ThrowIfCancellationRequested();
        if (target.Provider == ScmProvider.Forgejo && mode == CodeInsightObservationMode.HistoricalBackfill)
        {
            throw new CodeInsightProviderIdentityException();
        }

        var statuses = statusFetchers.SingleOrDefault(adapter => adapter.Provider == target.Provider)
                       ?? throw new InvalidOperationException("Provider feedback status adapter is unavailable.");
        var threads = threadFetchers.SingleOrDefault(adapter => adapter.Provider == target.Provider)
                      ?? throw new InvalidOperationException("Provider feedback thread adapter is unavailable.");
        if (target.Provider == ScmProvider.Forgejo)
        {
            return await ReadCurrentAsync(target, ownership, statuses, threads, ct);
        }

        var reviewerThreads = await statuses.GetFeedbackStatusesAsync(
            target.ProviderScopePath, target.ProviderProjectKey,
            target.Key.RepositoryId, checked((int)target.Key.PullRequestId), ownership, target.Key.ClientId, ct);
        ct.ThrowIfCancellationRequested();
        var codeThreads = await threads.FetchFeedbackThreadsAsync(
            target.ProviderScopePath, target.ProviderProjectKey,
            target.Key.RepositoryId, checked((int)target.Key.PullRequestId), target.Key.ClientId, ct);
        ct.ThrowIfCancellationRequested();
        return new CodeInsightProviderFeedback(reviewerThreads, codeThreads);
    }

    private static async Task<CodeInsightProviderFeedback> ReadCurrentAsync(
        CodeInsightProviderEvidenceTarget target, ThreadOwnershipResolver ownership,
        IProviderReviewerThreadStatusFetcher statuses, IProviderPullRequestFetcher threads, CancellationToken ct)
    {
        IReadOnlyList<MeisterDev.ProPR.Application.DTOs.PrThreadStatusEntry> reviewerThreads;
        var reviewerEnumerationComplete = true;
        try
        {
            reviewerThreads = await statuses.GetReviewerThreadStatusesAsync(
                target.ProviderScopePath, target.ProviderProjectKey,
                target.Key.RepositoryId, checked((int)target.Key.PullRequestId), ownership, target.Key.ClientId, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            reviewerThreads = [];
            reviewerEnumerationComplete = false;
        }

        ct.ThrowIfCancellationRequested();
        var codeThreads = await threads.FetchThreadsAsync(
            target.ProviderScopePath, target.ProviderProjectKey,
            target.Key.RepositoryId, checked((int)target.Key.PullRequestId), target.Key.ClientId, ct);
        ct.ThrowIfCancellationRequested();
        return new CodeInsightProviderFeedback(reviewerThreads, codeThreads, reviewerEnumerationComplete);
    }
}
