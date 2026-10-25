// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.SecurityScanning.Models;

/// <summary>Request to create a fix proposal from a finding or review comment.</summary>
/// <remarks>
///     <c>Remediation</c> may be <c>null</c>: it is stored exactly as given and never fabricated.
///     A proposal is only a request for a human decision — it never creates a pull request
///     or applies a code change.
/// </remarks>
public sealed record CreateFixProposalRequest(
    string FindingRef,
    string Source,
    string Title,
    string? FilePath = null,
    int? Line = null,
    string? Remediation = null,
    string? Confidence = null);

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
    string? DecisionReason);

/// <summary>List envelope for fix proposals, newest first.</summary>
public sealed record FixProposalListResponse(IReadOnlyList<FixProposalResponse> Items);
