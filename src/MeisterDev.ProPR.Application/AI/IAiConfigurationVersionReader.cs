// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.AI;

/// <summary>Reads effective non-secret AI routing dependencies without creating a chat runtime.</summary>
public interface IAiConfigurationVersionReader
{
    Task<string> ReadAsync(Guid clientId, IReadOnlyList<AiPurpose> purposes, CancellationToken ct = default);
}
