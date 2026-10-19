// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Domain.ValueObjects;

/// <summary>The observed disposition of a finding-producing model pass.</summary>
public enum ModelGenerationDisposition
{
    Unknown,
    Completed,
    NotApplicable,
    Skipped,
    Failed,
    Cancelled,
    InvalidOutput,
}

/// <summary>A closed range assigned to the generation pass, independent of its tool reads.</summary>
public sealed record ModelReviewRange(int Start, int End, string Side = "RIGHT");

/// <summary>A stable, attributed candidate retained before verification and publication.</summary>
public sealed record ModelReviewCandidate(string Id, string FilePath, int? LineNumber, string Message)
{
    public static ModelReviewCandidate FromComment(string id, ReviewComment comment) =>
        new(id, comment.FilePath ?? string.Empty, comment.LineNumber, comment.Message);
}

/// <summary>A bounded transport fragment retained until its complete proof checksum can be verified.</summary>
public sealed record ModelReviewProofFragment(string Digest, int Offset, int TotalBytes, string Data)
{
    public const int MaximumFragments = 256;
    public const int MaximumProofBytes = 3 * 1024 * 1024;
    public const int MaximumDataCharacters = 16 * 1024;

    public bool IsBounded
    {
        get
        {
            if (this.Digest is not { Length: 64 } || !this.Digest.All(char.IsAsciiHexDigit) || this.TotalBytes is < 1 or > MaximumProofBytes
                || this.Offset < 0 || this.Offset >= this.TotalBytes || this.Data is not { Length: > 0 and <= MaximumDataCharacters }
                || this.Data.Length % 4 != 0 || !this.Data.All(character => char.IsAsciiLetterOrDigit(character) || character is '+' or '/' or '='))
            {
                return false;
            }

            var padding = this.Data.EndsWith("==", StringComparison.Ordinal) ? 2 : this.Data.EndsWith('=') ? 1 : 0;
            var bytes = this.Data.Length / 4 * 3 - padding;
            return bytes > 0 && this.Data.AsSpan(0, this.Data.Length - padding).IndexOf('=') < 0 && (long)this.Offset + bytes <= this.TotalBytes;
        }
    }
}

/// <summary>Positive completion evidence for one generation pass, including a valid empty candidate set.</summary>
public sealed record ModelReviewPassEvidence
{
    public Guid JobId { get; init; }
    public string PassKey { get; init; } = string.Empty;
    public string ModelId { get; init; } = string.Empty;
    public string LogicalModelName { get; init; } = string.Empty;
    public Guid? ConnectionId { get; init; }
    public Guid? ConfiguredModelId { get; init; }
    public int Ordinal { get; init; }
    public int Attempt { get; init; }
    public int? LeaseGeneration { get; init; }
    public string Kind { get; init; } = "generation";
    public string? Lens { get; init; }
    public bool Shadow { get; init; }
    public ModelGenerationDisposition Disposition { get; init; }
    public bool CandidateSetComplete { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public DateTimeOffset? AcceptedAt { get; init; }
    public IReadOnlyList<ModelReviewCandidate> Candidates { get; init; } = [];

    public bool IsCertified => this.Disposition == ModelGenerationDisposition.Completed && this.CandidateSetComplete
                                                                                        && this.Kind is "generation" or "pr-wide-generation"
                                                                                        && !string.IsNullOrWhiteSpace(this.ModelId) &&
                                                                                        !string.IsNullOrWhiteSpace(this.PassKey)
                                                                                        && this.StartedAt is { } started && this.CompletedAt is { } completed &&
                                                                                        completed >= started;

    internal bool IsBounded => this.PassKey is { Length: > 0 and <= 64 } && this.ModelId is { Length: <= 256 }
                                                                         && this.LogicalModelName is { Length: <= 128 } && this.Kind is { Length: <= 32 } &&
                                                                         this.Lens is null or { Length: <= 128 }
                                                                         && Enum.IsDefined(this.Disposition) && this.Candidates is not null &&
                                                                         this.Candidates.Count <= ModelReviewEvidence.MaximumCandidates
                                                                         && this.Candidates.All(candidate => candidate is not null &&
                                                                             candidate.Id is { Length: > 0 and <= 64 }
                                                                             && candidate.FilePath is { Length: <= 2048 } &&
                                                                             candidate.Message is { Length: > 0 and <= 16_384 } &&
                                                                             !string.IsNullOrWhiteSpace(candidate.Message)
                                                                             && candidate.LineNumber is null or >= 1)
                                                                         && this.Candidates.Select(candidate => candidate.Id).Distinct(StringComparer.Ordinal)
                                                                             .Count() == this.Candidates.Count;

    internal ModelReviewPassEvidence BoundedOrUnknown() => this.IsBounded
        ? this
        : new()
        {
            PassKey = this.PassKey is { Length: > 0 and <= 64 } ? this.PassKey : "unknown",
            ModelId = this.ModelId is { Length: <= 256 } ? this.ModelId : string.Empty,
            LogicalModelName = this.LogicalModelName is { Length: <= 128 } ? this.LogicalModelName : string.Empty,
            ConnectionId = this.ConnectionId, ConfiguredModelId = this.ConfiguredModelId, Ordinal = this.Ordinal,
            StartedAt = this.StartedAt, CompletedAt = this.CompletedAt, Disposition = ModelGenerationDisposition.Unknown,
        };
}

/// <summary>Versioned source, input and pass inventory retained with a review checkpoint.</summary>
public sealed record ModelReviewEvidence
{
    public const string CurrentSchema = "model-opportunity-v1";
    public const int MaximumPasses = 128;
    public const int MaximumCandidates = 1024;
    public const int MaximumCandidateCharacters = 256_000;
    public const int MaximumRanges = 4096;

    public string Schema { get; init; } = CurrentSchema;
    public Guid JobId { get; init; }
    public int Attempt { get; init; }
    public int? LeaseGeneration { get; init; }
    public Guid ClientId { get; init; }
    public string RepositoryId { get; init; } = string.Empty;
    public long PullRequestId { get; init; }
    public string ProviderScope { get; init; } = string.Empty;
    public Guid? SourceConnectionId { get; init; }
    public string RevisionKey { get; init; } = string.Empty;
    public string? HeadSha { get; init; }
    public string? BaseSha { get; init; }
    public string FilePath { get; init; } = string.Empty;
    public string InputFingerprint { get; init; } = string.Empty;
    public IReadOnlyList<ModelReviewRange> AssignedRanges { get; init; } = [];
    public bool InventoryComplete { get; init; }
    public bool InputComplete { get; init; }
    public IReadOnlyList<ModelReviewPassEvidence> Passes { get; init; } = [];
    public DateTimeOffset CapturedAt { get; init; }
    public DateTimeOffset? AcceptedAt { get; init; }
    public Guid? OriginJobId { get; init; }
    public Guid? OriginFileResultId { get; init; }
    public bool UnchangedInputProven { get; init; }
    public string? UnavailableReason { get; init; }
    public IReadOnlyList<ModelReviewProofFragment> PendingFragments { get; init; } = [];
    public IReadOnlyList<string> RetainedTransportDigests { get; init; } = [];

    public bool IsBounded => this.Schema == CurrentSchema && this.Passes is not null && this.AssignedRanges is not null
                             && this.PendingFragments is not null && this.PendingFragments.Count <= ModelReviewProofFragment.MaximumFragments
                             && this.PendingFragments.All(fragment => fragment is not null && fragment.IsBounded)
                             && this.PendingFragments.Sum(fragment => (long)fragment.Data.Length) <= ModelReviewProofFragment.MaximumProofBytes * 4L / 3 + 4
                             && this.RetainedTransportDigests is not null && this.RetainedTransportDigests.Count <= MaximumPasses
                             && this.RetainedTransportDigests.All(digest => digest is { Length: 64 } && digest.All(char.IsAsciiHexDigit))
                             && this.Passes.Count <= MaximumPasses && this.AssignedRanges.Count <= MaximumRanges
                             && this.AssignedRanges.All(range =>
                                 range is not null && range.Start > 0 && range.End >= range.Start && range.Side is "LEFT" or "RIGHT")
                             && this.ProviderScope is { Length: <= 1024 } && this.RepositoryId is { Length: <= 512 } && this.FilePath is { Length: <= 2048 }
                             && this.RevisionKey is { Length: <= 1024 } && this.InputFingerprint is { Length: <= 128 }
                             && this.HeadSha is null or { Length: <= 128 } && this.BaseSha is null or { Length: <= 128 }
                             && this.UnavailableReason is null or { Length: <= 128 }
                             && this.Passes.All(pass => pass is not null && pass.IsBounded)
                             && this.Passes.Select(pass => pass.PassKey).Distinct(StringComparer.Ordinal).Count() == this.Passes.Count
                             && this.Passes.SelectMany(pass => pass.Candidates).Select(candidate => candidate.Id).Distinct(StringComparer.Ordinal).Count() ==
                             this.Passes.Sum(pass => pass.Candidates.Count)
                             && this.Passes.Sum(pass => (long)pass.Candidates.Count) <= MaximumCandidates
                             && this.Passes.Sum(pass =>
                                 pass.Candidates.Sum(candidate => (long)candidate.Message.Length + candidate.FilePath.Length + candidate.Id.Length)) <=
                             MaximumCandidateCharacters;

    /// <summary>Retains the review result when its optional analytics proof exceeds a capture limit.</summary>
    public ModelReviewEvidence BoundedOrUnknown() => this.IsBounded
        ? this
        : new()
        {
            JobId = this.JobId, Attempt = this.Attempt, ClientId = this.ClientId, PullRequestId = this.PullRequestId,
            CapturedAt = this.CapturedAt, AcceptedAt = this.AcceptedAt, UnavailableReason = "model-proof-limit-exceeded",
        };

    /// <summary>Appends independent pass checkpoints without allowing later invalid proof to remove a certified pass.</summary>
    public ModelReviewEvidence Merge(ModelReviewEvidence incoming)
    {
        if (this.JobId != incoming.JobId || this.FilePath != incoming.FilePath
                                         || this.ClientId != incoming.ClientId || this.RepositoryId != incoming.RepositoryId
                                         || this.PullRequestId != incoming.PullRequestId || this.SourceConnectionId != incoming.SourceConnectionId
                                         || this.ProviderScope != incoming.ProviderScope || this.RevisionKey != incoming.RevisionKey ||
                                         this.HeadSha != incoming.HeadSha || this.BaseSha != incoming.BaseSha
                                         || this.InputFingerprint != incoming.InputFingerprint || this.InputComplete != incoming.InputComplete
                                         || this.AssignedRanges is null || incoming.AssignedRanges is null
                                         || !this.AssignedRanges.SequenceEqual(incoming.AssignedRanges))
        {
            return incoming.BoundedOrUnknown();
        }

        if (incoming.Passes is null || incoming.Passes.Count > MaximumPasses)
        {
            return this with { InventoryComplete = false, UnavailableReason = "model-proof-limit-exceeded" };
        }

        var passes = this.Passes.ToDictionary(pass => pass.PassKey, StringComparer.Ordinal);
        foreach (var group in incoming.Passes.Where(pass => pass is not null).GroupBy(pass => pass.PassKey, StringComparer.Ordinal))
        {
            var pass = group.First();
            var bounded = pass.BoundedOrUnknown();
            if (group.Count() > 1)
            {
                bounded = bounded with { Disposition = ModelGenerationDisposition.Unknown, CandidateSetComplete = false, Candidates = [] };
            }

            if (!passes.TryGetValue(bounded.PassKey, out var prior) || !prior.IsCertified)
            {
                var proposed = this with { Passes = passes.Values.Where(item => item.PassKey != bounded.PassKey).Append(bounded).ToArray() };
                if (!proposed.IsBounded)
                {
                    bounded = bounded with
                    {
                        Disposition = ModelGenerationDisposition.Unknown, CandidateSetComplete = false, Candidates = [], AcceptedAt = null
                    };
                }

                passes[bounded.PassKey] = bounded;
            }
        }

        var result = this with
        {
            Attempt = incoming.Attempt, LeaseGeneration = incoming.LeaseGeneration,
            Passes = passes.Values.OrderBy(pass => pass.Ordinal).ThenBy(pass => pass.PassKey, StringComparer.Ordinal).ToArray(),
            InventoryComplete = this.InventoryComplete && incoming.InventoryComplete, AcceptedAt = incoming.AcceptedAt ?? this.AcceptedAt
        };
        return result.IsBounded ? result : this with { InventoryComplete = false, UnavailableReason = "model-proof-limit-exceeded" };
    }
}

/// <summary>A server-dispatched model assignment with optional typed scope and pass identity.</summary>
/// <param name="ModelId">Resolved remote model identity.</param>
/// <param name="LogicalModelName">Logical role used by the actual pass.</param>
/// <param name="ConnectionId">Resolved AI runtime connection, independent of the source-provider connection.</param>
/// <param name="ConfiguredModelId">Resolved configured model identity.</param>
/// <param name="Ordinal">Actual generation pass ordinal; missing legacy metadata cannot authorize a certificate.</param>
/// <param name="Scope">The assigned per_file or pr_wide scope.</param>
/// <param name="Lens">Configured per-file lens when applicable.</param>
/// <param name="Shadow">Whether the assignment suppresses publication.</param>
public sealed record ModelReviewModelBinding(
    string ModelId,
    string LogicalModelName,
    Guid? ConnectionId,
    Guid? ConfiguredModelId,
    int? Ordinal = null,
    string? Scope = null,
    string? Lens = null,
    bool? Shadow = null);

/// <summary>The frozen file inventory and PR-wide generation proof of one execution.</summary>
public sealed record ModelReviewManifest
{
    public string Schema { get; init; } = ModelReviewEvidence.CurrentSchema;
    public Guid JobId { get; init; }
    public int Attempt { get; init; }
    public int? LeaseGeneration { get; init; }
    public IReadOnlyList<string> Files { get; init; } = [];
    public IReadOnlyList<ModelReviewModelBinding> AllowedModels { get; init; } = [];
    public string ProviderScope { get; init; } = string.Empty;
    public Guid? SourceConnectionId { get; init; }
    public bool InventoryComplete { get; init; }
    public IReadOnlyList<ModelReviewEvidence> PullRequestPasses { get; init; } = [];
    public DateTimeOffset CapturedAt { get; init; }
    public DateTimeOffset? AcceptedAt { get; init; }
    public string? UnavailableReason { get; init; }

    public bool IsBounded => this.Schema == ModelReviewEvidence.CurrentSchema && this.Files is not null && this.Files.Count <= 10_000
                             && this.ProviderScope is { Length: <= 1024 } && this.AllowedModels is not null &&
                             this.AllowedModels.Count <= ModelReviewEvidence.MaximumPasses
                             && this.AllowedModels.All(model =>
                                 model is not null && model.ModelId is { Length: > 0 and <= 256 } && model.LogicalModelName is { Length: <= 128 }
                                 && model.Ordinal is null or >= 0 && model.Scope is null or "per_file" or "pr_wide" && model.Lens is null or { Length: <= 128 })
                             && this.Files.All(path => path is { Length: > 0 and <= 2048 }) && this.PullRequestPasses is not null
                             && this.PullRequestPasses.Count <= ModelReviewEvidence.MaximumPasses &&
                             this.PullRequestPasses.All(proof => proof is not null && proof.IsBounded && proof.PendingFragments.Count == 0)
                             && this.Files.Distinct(StringComparer.Ordinal).Count() == this.Files.Count
                             && this.PullRequestPasses.Select(proof => proof.FilePath).Distinct(StringComparer.Ordinal).Count() == this.PullRequestPasses.Count
                             && this.PullRequestPasses.Sum(proof => proof.AssignedRanges.Count) <= ModelReviewEvidence.MaximumRanges
                             && this.PullRequestPasses.Sum(proof => proof.Passes.Count) <= ModelReviewEvidence.MaximumPasses
                             && this.PullRequestPasses.Sum(proof => proof.Passes.Sum(pass => pass.Candidates.Count)) <= ModelReviewEvidence.MaximumCandidates
                             && this.Files.Sum(path => (long)path.Length) <= 1_000_000
                             && this.PullRequestPasses.Sum(proof => proof.Passes.Sum(pass =>
                                 pass.Candidates.Sum(candidate => (long)candidate.Message.Length + candidate.FilePath.Length + candidate.Id.Length))) <=
                             ModelReviewEvidence.MaximumCandidateCharacters;
}
