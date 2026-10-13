// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.CodeInsights.Contracts;

/// <summary>Separates known normalized provider resolution states from absent or unsupported status evidence.</summary>
public static class CodeInsightNativeResolution
{
    public static ThreadResolutionIntent? InterpretKnown(string? status)
    {
        return status?.Trim().ToUpperInvariant() switch
        {
            "ACTIVE" or "PENDING" => ThreadResolutionIntent.Active,
            "FIXED" or "CLOSED" => ThreadResolutionIntent.ClaimsFix,
            "WONTFIX" or "BYDESIGN" => ThreadResolutionIntent.AcceptedByHuman,
            _ => null,
        };
    }
}
