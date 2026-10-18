// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.Application.Features.Crawling.Webhooks.Models;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Parsing;

/// <summary>Parses Azure DevOps webhook payloads into normalized delivery models.</summary>
public sealed class AdoWebhookPayloadParser : IAdoWebhookPayloadParser
{
    /// <inheritdoc />
    public IncomingWebhookDelivery Parse(string pathKey, JsonElement payload)
    {
        var eventType = RequireString(payload, "eventType");
        var resource = PullRequestResource(payload);
        var repositoryId = RequireString(resource, "repository", "id");
        var sourceBranch = RequireString(resource, "sourceRefName");
        var targetBranch = RequireString(resource, "targetRefName");
        var pullRequestStatus = RequireString(resource, "status");
        if (!TryReadInt32(resource, out var pullRequestId, "pullRequestId") || pullRequestId <= 0)
        {
            throw new InvalidOperationException("Payload is missing a valid pullRequestId.");
        }

        var eventResource = payload.GetProperty("resource");
        var commentId = OptionalIdentifier(eventResource, "comment", "id");
        var threadId = OptionalIdentifier(eventResource, "threadId") ?? OptionalIdentifier(eventResource, "comment", "threadId");
        if (threadId is null)
        {
            foreach (var link in new[]
                     {
                         ReadString(eventResource, "comment", "_links", "threads", "href"),
                         ReadString(eventResource, "comment", "_links", "self", "href"), ReadString(eventResource, "_links", "threads", "href")
                     })
            {
                threadId = ReadThreadLink(link, repositoryId, pullRequestId, commentId);
                if (threadId is not null)
                {
                    break;
                }
            }
        }

        return new IncomingWebhookDelivery(
            pathKey, eventType, NormalizeEventType(eventType), repositoryId, pullRequestId,
            sourceBranch, targetBranch, pullRequestStatus, ReadReviewerIds(resource), threadId, commentId);
    }

    internal static JsonElement PullRequestResource(JsonElement payload)
    {
        if (!payload.TryGetProperty("resource", out var resource) || resource.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Payload is missing a resource.");
        }

        return resource.TryGetProperty("pullRequest", out var review) && review.ValueKind == JsonValueKind.Object ? review : resource;
    }

    private static string? ReadThreadLink(string? value, string repositoryId, int pullRequestId, string? commentId)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        for (var index = 0; index + 5 < parts.Length; index++)
        {
            if (!parts[index].Equals("repositories", StringComparison.OrdinalIgnoreCase) ||
                !parts[index + 1].Equals(repositoryId, StringComparison.OrdinalIgnoreCase) ||
                !parts[index + 2].Equals("pullRequests", StringComparison.OrdinalIgnoreCase) ||
                !int.TryParse(parts[index + 3], out var number) || number != pullRequestId ||
                !parts[index + 4].Equals("threads", StringComparison.OrdinalIgnoreCase) ||
                !int.TryParse(parts[index + 5], out var thread) || thread <= 0)
            {
                continue;
            }

            if (parts.Length > index + 6 && (parts.Length != index + 8 || !parts[index + 6].Equals("comments", StringComparison.OrdinalIgnoreCase) ||
                                             commentId is not null && parts[index + 7] != commentId))
            {
                return null;
            }

            return thread.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return null;
    }

    private static string? OptionalIdentifier(JsonElement element, params string[] path)
    {
        foreach (var part in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(part, out element))
            {
                return null;
            }
        }

        return element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var number) && number > 0
            ? number.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : element.ValueKind == JsonValueKind.String && long.TryParse(element.GetString(), out number) && number > 0
                ? number.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : null;
    }

    private static string? ReadString(JsonElement element, params string[] path)
    {
        foreach (var part in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(part, out element))
            {
                return null;
            }
        }

        return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    }

    private static WebhookEventType? NormalizeEventType(string eventType)
    {
        return eventType switch
        {
            "git.pullrequest.created" => WebhookEventType.PullRequestCreated,
            "git.pullrequest.updated" => WebhookEventType.PullRequestUpdated,
            "ms.vss-code.git-pullrequest-comment-event" => WebhookEventType.PullRequestCommented,
            "git.pullrequest.commented" => WebhookEventType.PullRequestCommented,
            _ => null,
        };
    }

    private static bool TryReadInt32(JsonElement element, out int value, params string[] path)
    {
        value = default;
        var current = element;

        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                return false;
            }
        }

        return current.ValueKind == JsonValueKind.Number && current.TryGetInt32(out value);
    }

    private static IReadOnlyList<Guid> ReadReviewerIds(JsonElement payload)
    {
        if (!payload.TryGetProperty("reviewers", out var reviewers) ||
            reviewers.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var reviewerIds = new List<Guid>();
        foreach (var reviewer in reviewers.EnumerateArray())
        {
            if (!reviewer.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            if (Guid.TryParse(idElement.GetString(), out var reviewerId))
            {
                reviewerIds.Add(reviewerId);
            }
        }

        return reviewerIds;
    }

    private static string RequireString(JsonElement element, params string[] path)
    {
        var current = element;

        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                throw new InvalidOperationException($"Payload is missing required field '{string.Join('.', path)}'.");
            }
        }

        if (current.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(current.GetString()))
        {
            throw new InvalidOperationException($"Payload is missing required field '{string.Join('.', path)}'.");
        }

        return current.GetString()!;
    }
}
