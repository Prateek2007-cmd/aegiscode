// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Security.Cryptography;
using System.Text.Json;
using MeisterDev.ProPR.Domain.Events;

namespace MeisterDev.ProPR.CodeInsights.Contracts;

/// <summary>Identifies the native root comment, falling back only to an actual native thread identity.</summary>
public static class ModelConcernIdentity
{
    public static string? Of(ThreadUpdatedEvent observation)
    {
        var root = observation.ConcernAnchor?.RootCommentId;
        var thread = string.IsNullOrWhiteSpace(observation.ThreadId) ? null : observation.ThreadId;
        root = string.IsNullOrWhiteSpace(root) ? null : root;
        if (root is null && thread is null || root?.Length > 256 || thread?.Length > 256
            || observation.RepositoryId.Length > 512 || observation.ProviderScope?.Length > 1024)
        {
            return null;
        }

        return Convert.ToHexStringLower(
            SHA256.HashData(
                JsonSerializer.SerializeToUtf8Bytes(
                    new
                    {
                        observation.ClientId, observation.ConnectionId, observation.RepositoryId, observation.PullRequestId,
                        ProviderScope = string.IsNullOrWhiteSpace(observation.ProviderScope) ? "" : observation.ProviderScope, root, thread,
                    })));
    }
}
