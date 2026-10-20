// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Extensibility.Execution;

namespace MeisterDev.ProPR.Application.Features.ExecutionAuthority;

/// <summary>The usage of one completed AI call, before it is stored and reported.</summary>
/// <param name="ConnectionId">The AI connection the call used.</param>
/// <param name="ConnectionRevision">The configuration revision of that connection.</param>
/// <param name="ConnectionClientId">The client that owns the connection, or null for a tenant connection.</param>
/// <param name="ConnectionTenantId">The tenant that owns the connection, or null for a client connection.</param>
/// <param name="ProviderKind">The provider family of the connection, by its identity key.</param>
/// <param name="ModelId">The model the provider was asked for.</param>
/// <param name="Operation">Whether the call was a chat completion or an embedding request.</param>
/// <param name="Usage">The normalized token counts of the response.</param>
/// <param name="Duration">How long the call took.</param>
/// <param name="CompletedAt">When the call completed.</param>
public sealed record AiCallUsage(
    Guid ConnectionId,
    long ConnectionRevision,
    Guid? ConnectionClientId,
    Guid? ConnectionTenantId,
    string ProviderKind,
    string ModelId,
    AiOperation Operation,
    AiTokenUsage Usage,
    TimeSpan Duration,
    DateTimeOffset CompletedAt);
