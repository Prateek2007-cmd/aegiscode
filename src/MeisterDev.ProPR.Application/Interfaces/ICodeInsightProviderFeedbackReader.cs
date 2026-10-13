// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.ThreadOwnership;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>Selects ordinary close or strict historical feedback reads for the captured provider family.</summary>
public interface ICodeInsightProviderFeedbackReader
{
    Task<CodeInsightProviderFeedback> ReadAsync(
        CodeInsightProviderEvidenceTarget target, ThreadOwnershipResolver ownership,
        CancellationToken ct = default, CodeInsightObservationMode mode = CodeInsightObservationMode.HistoricalBackfill);
}

public sealed record CodeInsightProviderFeedback(
    IReadOnlyList<PrThreadStatusEntry> ReviewerThreads,
    IReadOnlyList<PrCommentThread> Threads,
    bool ReviewerEnumerationComplete = true);

/// <summary>The provider read could not establish a complete code-thread enumeration.</summary>
public sealed class CodeInsightIncompleteEnumerationException() : InvalidOperationException("Provider feedback enumeration is incomplete.");

/// <summary>The provider cannot correlate retained publications with code-thread identities.</summary>
public sealed class CodeInsightProviderIdentityException() : InvalidOperationException("Provider code-thread correlation is unavailable.");
