// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Parsing;

namespace MeisterDev.ProPR.Infrastructure.Tests.AzureDevOps;

public sealed class AdoWebhookPayloadParserTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FlatAndNestedCommentPayloadsPreserveValidatedNativeIdentifiers(bool nested)
    {
        var resource = Resource(nested, "https://dev.azure.com/org/project/_apis/git/repositories/repo/pullRequests/42/threads/5/comments/2");
        var parsed = new AdoWebhookPayloadParser().Parse(
            "key", JsonSerializer.SerializeToElement(
                new
                    { eventType = "ms.vss-code.git-pullrequest-comment-event", resource }));
        Assert.Equal("repo", parsed.RepositoryId);
        Assert.Equal(42, parsed.PullRequestId);
        Assert.Equal("5", parsed.ThreadId);
        Assert.Equal("2", parsed.CommentId);
    }

    [Theory]
    [InlineData("https://dev.azure.com/org/project/_apis/git/repositories/other/pullRequests/42/threads/5/comments/2")]
    [InlineData("https://dev.azure.com/org/project/_apis/git/repositories/repo/pullRequests/99/threads/5/comments/2")]
    [InlineData("https://dev.azure.com/org/project/_apis/git/repositories/repo/pullRequests/42/threads/5/comments/99")]
    [InlineData("https://dev.azure.com/org/anything/5")]
    public void UnmatchedLinkCoordinatesCannotAddressAThread(string href)
    {
        var parsed = new AdoWebhookPayloadParser().Parse(
            "key", JsonSerializer.SerializeToElement(
                new
                    { eventType = "ms.vss-code.git-pullrequest-comment-event", resource = Resource(true, href) }));
        Assert.Null(parsed.ThreadId);
        Assert.Equal("2", parsed.CommentId);
    }

    private static Dictionary<string, object> Resource(bool nested, string href)
    {
        var review = new Dictionary<string, object>
        {
            ["repository"] = new { id = "repo" }, ["pullRequestId"] = 42, ["status"] = "active",
            ["sourceRefName"] = "refs/heads/feature", ["targetRefName"] = "refs/heads/main",
        };
        var resource = nested ? new Dictionary<string, object> { ["pullRequest"] = review } : review;
        resource["comment"] = new { id = 2, _links = new { self = new { href } } };
        return resource;
    }
}
