// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.ExecutionAuthority;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Extensibility.Execution;

namespace MeisterDev.ProPR.Infrastructure.AI;

/// <summary>The facts about a resolved runtime that every usage report of its calls carries.</summary>
/// <param name="ConnectionId">The AI connection.</param>
/// <param name="Revision">The configuration revision of the connection when the runtime was resolved.</param>
/// <param name="ClientId">The client that owns the connection, or null for a tenant connection.</param>
/// <param name="TenantId">The tenant that owns the connection, or null for a client connection.</param>
/// <param name="ProviderKind">The provider family of the connection.</param>
/// <param name="ModelId">The model the runtime calls.</param>
public sealed record ExecutionAuthorityConnection(
    Guid ConnectionId,
    long Revision,
    Guid? ClientId,
    Guid? TenantId,
    string ProviderKind,
    string ModelId)
{
    /// <summary>Takes the facts from the connection and the configured model a runtime was resolved for.</summary>
    public static ExecutionAuthorityConnection For(AiConnectionDto connection, AiConfiguredModelDto model)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(model);
        return new ExecutionAuthorityConnection(
            connection.Id,
            connection.ConfigurationRevision,
            connection.ClientId,
            connection.TenantId,
            connection.ProviderKind,
            model.RemoteModelId);
    }

    /// <summary>Builds the usage of one completed call made through this runtime.</summary>
    public AiCallUsage Usage(AiOperation operation, AiTokenUsage usage, TimeSpan duration, DateTimeOffset completedAt)
    {
        return new AiCallUsage(
            this.ConnectionId,
            this.Revision,
            this.ClientId,
            this.TenantId,
            this.ProviderKind,
            this.ModelId,
            operation,
            usage,
            duration,
            completedAt);
    }
}
