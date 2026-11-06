// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.SecurityScanning.Models;

/// <summary>Request to create a fix proposal from a finding or review comment.</summary>
/// <remarks>
///     <c>Remediation</c> may be <c>null</c>: it is stored exactly as given and never fabricated.
///     A proposal is only a request for a human decision — it never creates a pull request
///     or applies a code change. The patch fields (<c>RepositoryOwner</c>…
///     <c>SuggestedCode</c>) are optional: when the finding carried an exact suggested
///     replacement, storing it here is what lets an approved proposal open a draft PR
///     in one click; when absent, the create-draft-pr call must supply the patch.
/// </remarks>
public sealed record CreateFixProposalRequest(
    string FindingRef,
    string Source,
    string Title,
    string? FilePath = null,
    int? Line = null,
    string? Remediation = null,
    string? Confidence = null,
    string? RepositoryOwner = null,
    string? RepositoryName = null,
    string? BaseRef = null,
    int? StartLine = null,
    int? EndLine = null,
    string? OriginalCode = null,
    string? SuggestedCode = null);

/// <summary>Optional reason carried by an approve or reject decision. Stored verbatim on the proposal.</summary>
public sealed record DecideFixProposalRequest(string? Reason = null);

/// <summary>One fix proposal as served to the UI.</summary>
public sealed record FixProposalResponse(
    Guid Id,
    string FindingRef,
    string Source,
    string Title,
    string? FilePath,
    int? Line,
    string? Remediation,
    string? Confidence,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DecidedAt,
    string? DecisionReason,
    string? RepositoryOwner = null,
    string? RepositoryName = null,
    string? BaseRef = null,
    int? StartLine = null,
    int? EndLine = null,
    string? OriginalCode = null,
    string? SuggestedCode = null,
    string? DraftPrUrl = null,
    string? DraftPrBranch = null,
    string? DraftPrBaseBranch = null,
    DateTimeOffset? DraftPrCreatedAt = null);

/// <summary>List envelope for fix proposals, newest first.</summary>
public sealed record FixProposalListResponse(IReadOnlyList<FixProposalResponse> Items);

/// <summary>
///     Request to open a draft pull request for an approved proposal. Every field is an
///     override: a value left <c>null</c> falls back to what the proposal stored at
///     creation. At least the repository, the file, the line range, the original text,
///     and the replacement must resolve from the two combined, or the call fails with
///     a 400 naming what is missing. <c>BaseRef</c> is the branch the new branch starts
///     from and the PR targets; <c>BaseSha</c> pins the branch point to one commit
///     (the PR head, when the proposal came from a review). When neither base is
///     supplied the repository default branch is used and the response says so.
/// </summary>
public sealed record CreateDraftPullRequestRequest(
    string? RepositoryOwner = null,
    string? RepositoryName = null,
    string? ProviderScopePath = null,
    string? BaseRef = null,
    string? BaseSha = null,
    string? FilePath = null,
    int? StartLine = null,
    int? EndLine = null,
    string? OriginalCode = null,
    string? SuggestedCode = null);

/// <summary>
///     The opened draft pull request, with the proposal as stored after the call.
///     <c>Note</c> says plainly which base was used — the default branch when no base
///     was supplied, or that a draft PR already existed for this proposal.
/// </summary>
public sealed record DraftPullRequestResponse(
    FixProposalResponse Proposal,
    string PullRequestUrl,
    string Branch,
    string BaseBranch,
    bool UsedDefaultBranch,
    string Note);
