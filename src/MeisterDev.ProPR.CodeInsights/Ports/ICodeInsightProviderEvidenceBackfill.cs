// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.CodeInsights.Ports;

/// <summary>Refreshes a bounded retained pull-request population through provider evidence collection.</summary>
public interface ICodeInsightProviderEvidenceBackfill
{
    Task<CodeInsightProviderEvidenceBackfillResult> SweepAsync(int maximumPullRequests, CancellationToken ct = default);
}

/// <summary>Counts durable claims and their acknowledged observation states in one bounded cycle.</summary>
public sealed record CodeInsightProviderEvidenceBackfillResult(int Attempted, int Observed, int Unavailable, int Disabled);
