// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Events;

namespace MeisterDev.ProPR.Application.Features.ReviewArchive;

/// <summary>
///     Passive consumer that captures observed pull-request comment threads into the review-archive
///     store. It is a side-channel observer: it never participates in review decisions, deduplication,
///     memory, or the scope snapshot, and only runs when the producing connection opted in to retention.
/// </summary>
public interface IReviewArchiveIngestionService
{
    /// <summary>Retains changed thread content and records pull-request freshness once.</summary>
    async Task HandleThreadsUpdatedAsync(
        PullRequestRetentionKey key, string state, DateTimeOffset observedAt,
        IReadOnlyList<RetainedThreadSnapshot> threads, CancellationToken ct = default)
    {
        foreach (var thread in threads)
        {
            if (thread.IsDeleted)
            {
                throw new NotSupportedException("The archive consumer cannot retain thread deletions.");
            }

            await this.HandleThreadUpdatedAsync(
                new ThreadUpdatedEvent(
                    key.ClientId, key.ConnectionId, key.RepositoryId,
                    key.PullRequestId, thread.ThreadId, thread.FilePath, thread.Line, thread.Status, thread.UpdatedAt,
                    thread.Comments.Select(comment => new ThreadUpdatedComment(
                        comment.CommentId, comment.AuthorIdentity,
                        comment.IsAiAuthored, comment.PublishedAt, comment.Text, comment.OriginatingJobId)).ToArray(), observedAt), ct);
        }
    }

    /// <summary>
    ///     Persists the full thread snapshot carried by <paramref name="evt" /> into the review-archive
    ///     store, touching the parent pull request and upserting the thread with its comments.
    /// </summary>
    Task HandleThreadUpdatedAsync(ThreadUpdatedEvent evt, CancellationToken ct = default);

    /// <summary>
    ///     Persists the per-file unified diffs carried by <paramref name="evt" /> into the review-archive
    ///     store, touching the parent pull request and saving the diffs under the increment's revision key.
    /// </summary>
    Task HandleReviewIncrementDiffsAsync(ReviewIncrementCompletedEvent evt, CancellationToken ct = default);
}
