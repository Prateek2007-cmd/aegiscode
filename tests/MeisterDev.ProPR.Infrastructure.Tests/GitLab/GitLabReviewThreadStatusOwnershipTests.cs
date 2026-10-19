// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using MeisterDev.ProPR.Application.Features.ReviewArchive;
using MeisterDev.ProPR.Application.Features.ThreadOwnership;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Reviewing;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Security;

namespace MeisterDev.ProPR.Infrastructure.Tests.GitLab;

/// <summary>
///     Which merge-request discussions GitLab reports as ProPR's: provenance first, then the username the
///     connection's token authenticates as, and nothing else.
/// </summary>
public sealed class GitLabReviewThreadStatusOwnershipTests
{
    private const string AuthenticatedUsername = "meister-dev";

    [Theory]
    [InlineData(7, null, "RIGHT", "head")]
    [InlineData(null, 5, "LEFT", "base")]
    public async Task ConcernAnchorRetainsTheRootDiffRevisionInsteadOfAReplyPosition(int? newLine, int? oldLine, string side, string commit)
    {
        var origin = DateTimeOffset.Parse("2026-10-01T12:00:00Z");
        var sut = BuildSut(
            out var client, new
            {
                id = "discussion-abc", individual_note = false, notes = new object[]
                {
                    new
                    {
                        id = 501, body = "Concern", system = false, resolved = true, created_at = origin, author = new { id = 1, username = "human" },
                        position = new
                        {
                            new_path = "new.cs", old_path = "old.cs", new_line = newLine, old_line = oldLine,
                            head_sha = "head", base_sha = "base", start_sha = "start", position_type = "text"
                        }
                    },
                    new
                    {
                        id = 502, body = "Done", system = false, resolved = true, created_at = origin.AddDays(1), author = new { id = 2, username = "other" },
                        position = new { new_path = "later.cs", new_line = 99, head_sha = "later-head" }
                    },
                }
            });
        var snapshot = await sut.ReadSnapshotAsync("https://gitlab.example.com", "acme/platform", "101", 42, ThreadOwnershipResolver.None, client);
        var anchor = Assert.Single(snapshot.Threads).ConcernAnchor;
        Assert.NotNull(anchor);
        Assert.True(anchor.IsKnown);
        Assert.Equal(origin, anchor.OriginAt);
        Assert.Equal(commit, anchor.CommitSha);
        Assert.Equal(side, anchor.Side);
        Assert.Equal(newLine ?? oldLine, anchor.StartLine);
        Assert.Equal(newLine.HasValue ? "new.cs" : "old.cs", anchor.FilePath);
    }

    [Fact]
    public async Task GetReviewerThreadStatusesAsync_ProvenanceRecordsTheFirstNote_IncludesTheDiscussion()
    {
        // GitLab note ids are unique within the merge request, so provenance resolves on the note id even
        // though what was recorded as the thread id is the discussion id the crawl never reports.
        var sut = BuildSut(out var clientId, Discussion(501, "retired-service-account", 502, "jane"));

        var result = await sut.GetReviewerThreadStatusesAsync(
            "https://gitlab.example.com",
            "acme/platform",
            "101",
            42,
            ThreadOwnershipResolver.Create(
                [new PostedCommentOriginRow("discussion-abc", "501", Guid.NewGuid())],
                ThreadOwnerIdentity.None,
                ProviderCommentIdScope.PullRequest),
            clientId,
            CancellationToken.None);

        var entry = Assert.Single(result);
        Assert.Equal("discussion-abc", entry.ThreadId);
        Assert.Equal(1, entry.NonReviewerReplyCount);
    }

    [Fact]
    public async Task GetReviewerThreadStatusesAsync_NoProvenanceButFirstNoteIsTheAuthenticatedUser_IncludesTheDiscussion()
    {
        var sut = BuildSut(out var clientId, Discussion(501, AuthenticatedUsername, 502, "jane"));

        var result = await sut.GetReviewerThreadStatusesAsync(
            "https://gitlab.example.com",
            "acme/platform",
            "101",
            42,
            ThreadOwnershipResolver.None,
            clientId,
            CancellationToken.None);

        var entry = Assert.Single(result);
        Assert.Equal("discussion-abc", entry.ThreadId);
        Assert.Equal(1, entry.NonReviewerReplyCount);
    }

    [Fact]
    public async Task GetReviewerThreadStatusesAsync_NoProvenanceAndAHumanRaisedIt_ExcludesTheDiscussion()
    {
        var sut = BuildSut(out var clientId, Discussion(501, "jane", 502, AuthenticatedUsername));

        var result = await sut.GetReviewerThreadStatusesAsync(
            "https://gitlab.example.com",
            "acme/platform",
            "101",
            42,
            ThreadOwnershipResolver.None,
            clientId,
            CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetReviewerThreadStatusesAsync_DiscussionRaisedByTheConfiguredReviewer_IsExcluded()
    {
        // The deliberate narrowing. A client can configure one account as its review trigger and connect
        // with another; only the account the token authenticates as owns threads now, so a discussion the
        // configured reviewer raised with nothing recorded against it stays out.
        var sut = BuildSut(out var clientId, Discussion(501, "configured-review-bot", 502, "jane"));

        var result = await sut.GetReviewerThreadStatusesAsync(
            "https://gitlab.example.com",
            "acme/platform",
            "101",
            42,
            ThreadOwnershipResolver.None,
            clientId,
            CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetReviewerThreadStatusesAsync_ReplyRecordedAsProPRs_IsNotCountedAsANonReviewerReply()
    {
        // The reply arrived under an account the connection does not recognise, but ProPR recorded posting
        // it, so it is not a reply waiting for an answer.
        var sut = BuildSut(out var clientId, Discussion(501, AuthenticatedUsername, 502, "retired-service-account"));

        var result = await sut.GetReviewerThreadStatusesAsync(
            "https://gitlab.example.com",
            "acme/platform",
            "101",
            42,
            ThreadOwnershipResolver.Create(
                [new PostedCommentOriginRow("discussion-abc", "502", Guid.NewGuid())],
                ThreadOwnerIdentity.None,
                ProviderCommentIdScope.PullRequest),
            clientId,
            CancellationToken.None);

        Assert.Equal(0, Assert.Single(result).NonReviewerReplyCount);
    }

    private static object Discussion(int firstNoteId, string firstAuthor, int replyNoteId, string replyAuthor)
    {
        return new
        {
            id = "discussion-abc",
            individual_note = false,
            notes = new object[]
            {
                new
                {
                    id = firstNoteId,
                    body = "Please handle null.",
                    system = false,
                    resolved = false,
                    author = new { id = 99, username = firstAuthor },
                    position = new { new_path = "src/feature.ts", old_path = "src/feature.ts" },
                },
                new
                {
                    id = replyNoteId,
                    body = "Done.",
                    system = false,
                    resolved = false,
                    author = new { id = 7, username = replyAuthor },
                    position = new { new_path = "src/feature.ts", old_path = "src/feature.ts" },
                },
            },
        };
    }

    private static GitLabReviewThreadStatusProvider BuildSut(out Guid clientId, object discussion)
    {
        clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitLab, "https://gitlab.example.com");
        var connectionRepository = GitLabTestHelpers.CreateConnectionRepository(clientId, host);
        var httpClientFactory = GitLabTestHelpers.CreateHttpClientFactory(request =>
            request.RequestUri!.AbsoluteUri switch
            {
                "https://gitlab.example.com/api/v4/user" =>
                    GitLabTestHelpers.CreateJsonResponse(new { username = AuthenticatedUsername }),
                "https://gitlab.example.com/api/v4/projects/101/merge_requests/42/discussions?per_page=100" =>
                    GitLabTestHelpers.CreateJsonResponse(new[] { discussion }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });

        return new GitLabReviewThreadStatusProvider(
            new GitLabConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);
    }
}
