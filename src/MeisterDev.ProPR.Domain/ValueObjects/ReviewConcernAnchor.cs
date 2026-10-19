// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Domain.ValueObjects;

/// <summary>The native original concern position; observation-head revisions do not establish this proof.</summary>
public sealed record ReviewConcernAnchor
{
    public string? RootCommentId { get; init; }
    public DateTimeOffset? OriginAt { get; init; }
    public string? FilePath { get; init; }
    public string? Side { get; init; }
    public int? StartLine { get; init; }
    public int? EndLine { get; init; }
    public string? CommitSha { get; init; }
    public string? HeadSha { get; init; }
    public string? BaseSha { get; init; }
    public string? NativeRevisionId { get; init; }
    public bool OriginalScopeProven { get; init; }

    public bool IsKnown => this.OriginalScopeProven && this.OriginAt.HasValue && !string.IsNullOrWhiteSpace(this.RootCommentId)
                           && !string.IsNullOrWhiteSpace(this.FilePath) && this.Side is "RIGHT" or "LEFT"
                           && this.StartLine is > 0 && (this.EndLine ?? this.StartLine) >= this.StartLine
                           && (!string.IsNullOrWhiteSpace(this.CommitSha) || !string.IsNullOrWhiteSpace(this.NativeRevisionId));
}
