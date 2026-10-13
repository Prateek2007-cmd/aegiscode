// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Domain.ValueObjects;

/// <summary>Generated finding retained as evidence without allowing SCM publication.</summary>
public sealed record WithheldReviewFinding
{
    /// <summary>The maximum number of characters in a withholding reason code.</summary>
    public const int MaximumReasonCodeLength = 128;

    /// <summary>Creates evidence for a finding that cannot be published.</summary>
    /// <param name="comment">The original finding and its producing metadata.</param>
    /// <param name="reasonCode">The verification or final-gate reason that withheld the finding.</param>
    public WithheldReviewFinding(ReviewComment comment, string reasonCode)
    {
        ArgumentNullException.ThrowIfNull(comment);
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(reasonCode.Length, MaximumReasonCodeLength, nameof(reasonCode));
        this.Comment = comment;
        this.ReasonCode = reasonCode;
    }

    /// <summary>The original finding and its producing metadata.</summary>
    public ReviewComment Comment { get; init; }

    /// <summary>The verification or final-gate reason that withheld the finding.</summary>
    public string ReasonCode { get; }
}
