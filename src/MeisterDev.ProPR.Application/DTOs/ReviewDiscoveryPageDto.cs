// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.DTOs;

/// <summary>A bounded provider observation before consumer filtering. A null continuation establishes terminal coverage.</summary>
public sealed record ReviewDiscoveryPageDto(
    IReadOnlyList<ReviewDiscoveryItemDto> Items,
    string? Continuation,
    int RawCount,
    IReadOnlyList<string> ObservationIds)
{
    /// <summary>Optional native count used only to validate raw traversal, before consumer filtering.</summary>
    public int? TotalCount { get; init; }
}
