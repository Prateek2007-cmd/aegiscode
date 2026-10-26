// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json.Serialization;

namespace MeisterDev.ProPR.Domain.ValueObjects;

/// <summary>
///     Aggregated result of a review run, including summary and comments.
/// </summary>
/// <param name="Summary">Overall summary of the review results.</param>
/// <param name="Comments">List of individual review comments.</param>
public sealed record ReviewResult(
    string Summary,
    IReadOnlyList<ReviewComment> Comments)
{
    /// <summary>
    ///     Short structured summary of what the pull request does (2-4 lines), produced by the
    ///     reviewing AI from its read of the actual diff. <see langword="null" /> when the AI produced
    ///     none; the publisher omits the section rather than inventing one.
    /// </summary>
    public string? PrSummary { get; init; }

    /// <summary>
    ///     One short entry per changed file — what changed and why it matters — produced by the
    ///     reviewing AI from its read of the actual diff. <see langword="null" /> when the AI produced
    ///     none; entries for files outside the pull request are dropped before publication.
    /// </summary>
    public IReadOnlyList<PrWalkthroughEntry>? Walkthrough { get; init; }

    /// <summary>
    ///     Mermaid diagram of how the changed pieces connect, produced by the reviewing AI from its
    ///     read of the actual diff. <see langword="null" /> when the AI produced none or the diagram
    ///     failed the fail-closed gate; the publisher omits it rather than posting a broken diagram.
    /// </summary>
    public string? ChangeDiagram { get; init; }

    /// <summary>
    ///     Change statistics counted from the actual diffs of the pull request's changed files.
    ///     <see langword="null" /> when the statistics were not computed for this result.
    /// </summary>
    public PrChangeStats? ChangeStats { get; init; }

    /// <summary>Generated findings withheld by verification or the final gate, for internal evidence collection.</summary>
    [JsonIgnore]
    public IReadOnlyList<WithheldReviewFinding> WithheldFindings { get; init; } = [];

    /// <summary>Internal generation proof retained independently of publishable comments.</summary>
    [JsonIgnore]
    public ModelReviewEvidence? ModelEvidence { get; init; }

    /// <summary>Generation status supplied by the producing core, including invalid and skipped output.</summary>
    [JsonIgnore]
    public ModelGenerationDisposition? GenerationDisposition { get; init; }

    /// <summary>The execution inventory and PR-wide pass proofs.</summary>
    [JsonIgnore]
    public ModelReviewManifest? ModelEvidenceManifest { get; init; }

    /// <summary>
    ///     File paths whose results were carried forward from a prior iteration's review.
    ///     Empty for full (non-incremental) reviews.
    /// </summary>
    public IReadOnlyList<string> CarriedForwardFilePaths { get; init; } = [];

    /// <summary>
    ///     Count of comment candidates suppressed before posting because they originated from
    ///     carried-forward file results rather than a fresh review pass.
    /// </summary>
    public int CarriedForwardCandidatesSkipped { get; init; }

    /// <summary>
    ///     File paths reviewed diff-only because their full context exceeded the model's context window.
    ///     Surfaced in the review summary so a degraded review is never silently presented as a full one.
    /// </summary>
    public IReadOnlyList<string> ContextDegradedFilePaths { get; init; } = [];

    /// <summary>
    ///     File paths skipped without a review because even their minimal payload exceeded the model's
    ///     context window. Surfaced in the review summary so a skipped file is visible rather than missing.
    /// </summary>
    public IReadOnlyList<string> ContextSkippedFilePaths { get; init; } = [];

    /// <summary>
    ///     True when the review stopped scanning further files because the per-increment budget soft cap was
    ///     reached mid-run. The review still completed with a synthesis over the files that were reviewed.
    /// </summary>
    public bool BudgetSoftCapped { get; init; }

    /// <summary>The per-increment soft cap (USD) that was reached, when <see cref="BudgetSoftCapped" /> is true.</summary>
    public decimal? BudgetSoftCapThresholdUsd { get; init; }

    /// <summary>The metered spend (USD) that reached the soft cap, when <see cref="BudgetSoftCapped" /> is true.</summary>
    public decimal? BudgetSoftCapSpentUsd { get; init; }

    /// <summary>
    ///     File paths not scanned because the per-increment budget soft cap was reached mid-run. Surfaced in the
    ///     review summary so a budget-shortened review is never silently presented as a full one.
    /// </summary>
    public IReadOnlyList<string> BudgetSoftCapSkippedFilePaths { get; init; } = [];
}
