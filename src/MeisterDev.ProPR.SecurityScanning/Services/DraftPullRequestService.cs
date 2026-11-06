// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.SecurityScanning.Models;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.SecurityScanning.Services;

/// <summary>Raised when a draft PR is attempted on a proposal that is not approved. Maps to HTTP 409.</summary>
public sealed class DraftPullRequestNotApprovedException(Guid proposalId, FixProposalStatus status)
    : InvalidOperationException($"Fix proposal {proposalId} is {status}; only an approved proposal can open a draft pull request.")
{
    public Guid ProposalId { get; } = proposalId;
    public FixProposalStatus Status { get; } = status;
}

/// <summary>
///     Raised when a draft PR is refused without ever touching GitHub: the gate failed,
///     the file is missing at the base, or no usable GitHub connection exists. Maps to
///     HTTP 422. The <see cref="Reason" /> is a plain sentence the UI shows as-is.
/// </summary>
public sealed class DraftPullRequestRefusedException(string reason) : InvalidOperationException(reason)
{
    public string Reason { get; } = reason;
}

/// <summary>
///     Raised when a GitHub call fails after the request was accepted. Maps to HTTP 502.
///     The message carries the provider's own error text; no pull request is claimed.
/// </summary>
public sealed class DraftPullRequestUpstreamException(string message, Exception? inner = null)
    : InvalidOperationException(message, inner);

/// <summary>
///     Opens a draft pull request on GitHub for an approved fix proposal. The flow is
///     fail-closed end to end: the proposal must be approved; the replacement must
///     verify against the file at the base (the suggested-code gate's byte-for-byte
///     original match, range check, and no-op check); and the file update carries the
///     blob SHA read with that content, so a concurrent change is refused by GitHub
///     instead of overwriting. Any failure returns the real reason — a draft PR is
///     never reported unless GitHub returned its URL.
/// </summary>
public interface IDraftPullRequestService
{
    /// <summary>
    ///     Creates a draft PR for an approved proposal. Returns <c>null</c> when the
    ///     proposal does not exist for this client. Returns the already-created PR when
    ///     one exists instead of opening a second. Throws
    ///     <see cref="ArgumentException" /> on missing patch inputs,
    ///     <see cref="DraftPullRequestNotApprovedException" /> when the proposal is not
    ///     approved, <see cref="DraftPullRequestRefusedException" /> when the gate or
    ///     the base refuses, and <see cref="DraftPullRequestUpstreamException" /> when
    ///     a GitHub call fails.
    /// </summary>
    Task<DraftPullRequestResponse?> CreateDraftPullRequestAsync(
        Guid clientId,
        Guid proposalId,
        CreateDraftPullRequestRequest request,
        CancellationToken ct);
}

/// <inheritdoc />
public sealed class DraftPullRequestService(
    IFixProposalStore store,
    IDraftPullRequestGateway gateway,
    ILogger<DraftPullRequestService> logger) : IDraftPullRequestService
{
    private static readonly ProviderHostRef DefaultHost = new(ScmProvider.GitHub, "https://github.com");

    /// <inheritdoc />
    public async Task<DraftPullRequestResponse?> CreateDraftPullRequestAsync(
        Guid clientId,
        Guid proposalId,
        CreateDraftPullRequestRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var proposal = await store.GetAsync(clientId, proposalId, ct);
        if (proposal is null)
        {
            return null;
        }

        if (proposal.Status != FixProposalStatus.Approved)
        {
            throw new DraftPullRequestNotApprovedException(proposalId, proposal.Status);
        }

        // A second call for the same proposal returns the PR that already exists rather
        // than opening a duplicate — the stored URL is the idempotency key.
        if (!string.IsNullOrWhiteSpace(proposal.DraftPrUrl))
        {
            logger.LogInformation(
                "Draft PR already exists for fix proposal {ProposalId} ({PullRequestUrl}); returning it instead of opening another.",
                proposalId, proposal.DraftPrUrl);
            return new DraftPullRequestResponse(
                FixProposalService.ToResponse(proposal),
                proposal.DraftPrUrl,
                proposal.DraftPrBranch ?? string.Empty,
                proposal.DraftPrBaseBranch ?? proposal.BaseRef ?? string.Empty,
                false,
                "A draft pull request was already created for this proposal.");
        }

        var resolved = ResolveInputs(proposal, request);
        var host = ResolveHost(request.ProviderScopePath);

        // The base: an explicit base branch (or commit) wins; otherwise the repository
        // default branch, with the response saying so plainly.
        var usedDefaultBranch = string.IsNullOrWhiteSpace(resolved.BaseRef);
        var baseBranch = usedDefaultBranch
            ? await Upstream(() => gateway.GetDefaultBranchAsync(clientId, host, resolved.Owner, resolved.Repository, ct), ct)
              ?? throw new DraftPullRequestRefusedException(
                  $"Repository '{resolved.Owner}/{resolved.Repository}' was not found or is not accessible to this client's GitHub connection.")
            : resolved.BaseRef!;
        var baseRef = string.IsNullOrWhiteSpace(resolved.BaseSha) ? baseBranch : resolved.BaseSha;

        var file = await Upstream(() => gateway.GetFileContentAsync(clientId, host, resolved.Owner, resolved.Repository, resolved.FilePath, baseRef, ct), ct)
                   ?? throw new DraftPullRequestRefusedException(
                       $"File '{resolved.FilePath}' was not found in '{resolved.Owner}/{resolved.Repository}' at '{baseRef}'.");

        // Fail closed: the file must still contain exactly the lines the proposal was
        // written against. A mismatch means the code moved on — never force-apply.
        var gate = SuggestedCodeGate.VerifyReplacement(file.Content, resolved.StartLine, resolved.EndLine, resolved.OriginalCode, resolved.SuggestedCode);
        if (!gate.Passed)
        {
            throw new DraftPullRequestRefusedException(GateRefusalMessage(gate.FailureReason, resolved));
        }

        var newContent = ApplyReplacement(file.Content, resolved.StartLine!.Value, resolved.EndLine!.Value, resolved.SuggestedCode);
        var branchName = BuildBranchName(proposalId);
        var title = $"AegisCode fix: {proposal.Title}";
        var body = BuildPullRequestBody(proposal, resolved, baseBranch, baseRef);

        var created = await Upstream(
            () => gateway.CreateDraftPullRequestAsync(
                clientId,
                host,
                new DraftPullRequestInput(
                    resolved.Owner,
                    resolved.Repository,
                    baseBranch,
                    resolved.BaseSha,
                    branchName,
                    resolved.FilePath,
                    file.Sha,
                    newContent,
                    title,
                    body),
                ct), ct);

        var updated = proposal with
        {
            DraftPrUrl = created.PullRequestUrl,
            DraftPrBranch = created.Branch,
            DraftPrBaseBranch = created.BaseBranch,
            DraftPrCreatedAt = DateTimeOffset.UtcNow,
        };
        var saved = await store.UpdateAsync(updated, ct);
        logger.LogInformation(
            "Created draft pull request {PullRequestUrl} on branch {Branch} for fix proposal {ProposalId}.",
            created.PullRequestUrl, created.Branch, proposalId);

        var note = usedDefaultBranch
            ? $"Branched from the repository default branch '{created.BaseBranch}' because no base was supplied."
            : string.IsNullOrWhiteSpace(resolved.BaseSha)
                ? $"Branched from '{created.BaseBranch}'."
                : $"Branched from commit {resolved.BaseSha[..Math.Min(7, resolved.BaseSha.Length)]} on '{created.BaseBranch}'.";
        return new DraftPullRequestResponse(
            FixProposalService.ToResponse(saved),
            created.PullRequestUrl,
            created.Branch,
            created.BaseBranch,
            usedDefaultBranch,
            note);
    }

    /// <summary>
    ///     Resolves every input the flow needs from the request override falling back to
    ///     the stored proposal. Throws <see cref="ArgumentException" /> naming every
    ///     missing field at once instead of failing on the first one.
    /// </summary>
    private static ResolvedDraftPrInputs ResolveInputs(FixProposalRecord proposal, CreateDraftPullRequestRequest request)
    {
        var owner = FirstNonBlank(request.RepositoryOwner, proposal.RepositoryOwner);
        var repository = FirstNonBlank(request.RepositoryName, proposal.RepositoryName);
        var filePath = FirstNonBlank(request.FilePath, proposal.FilePath);
        var startLine = request.StartLine ?? proposal.StartLine ?? proposal.Line;
        var endLine = request.EndLine ?? proposal.EndLine ?? startLine;
        var originalCode = FirstNonBlank(request.OriginalCode, proposal.OriginalCode);
        var suggestedCode = FirstNonBlank(request.SuggestedCode, proposal.SuggestedCode);

        var missing = new List<string>();
        if (owner is null)
        {
            missing.Add("repositoryOwner");
        }

        if (repository is null)
        {
            missing.Add("repositoryName");
        }

        if (filePath is null)
        {
            missing.Add("filePath");
        }

        if (startLine is null)
        {
            missing.Add("startLine");
        }

        if (originalCode is null)
        {
            missing.Add("originalCode");
        }

        if (suggestedCode is null)
        {
            missing.Add("suggestedCode");
        }

        if (missing.Count > 0)
        {
            throw new ArgumentException(
                $"Cannot create a draft pull request: missing {string.Join(", ", missing)}. " +
                "Supply them with this call or store them on the proposal when it is created.");
        }

        if (startLine!.Value < 1 || endLine!.Value < startLine.Value)
        {
            throw new ArgumentException("startLine must be at least 1 and endLine must not be less than startLine.");
        }

        return new ResolvedDraftPrInputs(
            owner!,
            repository!,
            FirstNonBlank(request.BaseRef, proposal.BaseRef),
            FirstNonBlank(request.BaseSha, null),
            filePath!,
            startLine,
            endLine,
            originalCode!,
            suggestedCode!);
    }

    private static ProviderHostRef ResolveHost(string? providerScopePath)
    {
        if (string.IsNullOrWhiteSpace(providerScopePath))
        {
            return DefaultHost;
        }

        try
        {
            return new ProviderHostRef(ScmProvider.GitHub, providerScopePath.Trim());
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException("providerScopePath must be an absolute URL.", nameof(providerScopePath), ex);
        }
    }

    /// <summary>Maps a gate failure to the plain sentence the UI shows. The draft PR was not created in any of these cases.</summary>
    private static string GateRefusalMessage(SuggestedCodeGateFailureReason? reason, ResolvedDraftPrInputs resolved) => reason switch
    {
        SuggestedCodeGateFailureReason.NoHeadContent =>
            $"GitHub did not return readable content for '{resolved.FilePath}'.",
        SuggestedCodeGateFailureReason.NoAnchorLine =>
            "No line range was supplied for the replacement.",
        SuggestedCodeGateFailureReason.InvalidRange =>
            $"The line range {resolved.StartLine}-{resolved.EndLine} is outside '{resolved.FilePath}'.",
        SuggestedCodeGateFailureReason.MissingOriginalCode =>
            "The original lines were not supplied, so the replacement cannot be verified.",
        SuggestedCodeGateFailureReason.OriginalCodeMismatch =>
            $"The file has changed since the proposal was made: lines {resolved.StartLine}-{resolved.EndLine} of '{resolved.FilePath}' no longer match the original text. The draft pull request was not created.",
        SuggestedCodeGateFailureReason.NoOpReplacement =>
            "The suggested code is identical to the current lines — there is nothing to change.",
        _ => "The replacement failed verification against the current file, so the draft pull request was not created.",
    };

    /// <summary>
    ///     Splices the replacement lines into the head content, preserving the file's
    ///     line endings and its trailing newline (or lack of one).
    /// </summary>
    internal static string ApplyReplacement(string headContent, int startLine, int endLine, string suggestedCode)
    {
        var lineEnding = headContent.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var headLines = Normalize(headContent).Split('\n').ToList();
        if (headLines.Count > 1 && headLines[^1].Length == 0)
        {
            headLines.RemoveAt(headLines.Count - 1);
        }

        var replacementLines = Normalize(suggestedCode).Split('\n').ToList();
        if (replacementLines.Count > 1 && replacementLines[^1].Length == 0)
        {
            replacementLines.RemoveAt(replacementLines.Count - 1);
        }

        headLines.RemoveRange(startLine - 1, endLine - startLine + 1);
        headLines.InsertRange(startLine - 1, replacementLines);

        var joined = string.Join(lineEnding, headLines);
        return headContent.EndsWith('\n') ? joined + lineEnding : joined;
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string BuildBranchName(Guid proposalId)
    {
        var shortId = proposalId.ToString("N")[..8];
        return $"aegiscode/fix-{shortId}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
    }

    private static string BuildPullRequestBody(
        FixProposalRecord proposal,
        ResolvedDraftPrInputs resolved,
        string baseBranch,
        string baseRef) =>
        $"Draft fix for `{resolved.FilePath}` (lines {resolved.StartLine}-{resolved.EndLine}) from an approved AegisCode fix proposal.\n\n" +
        $"Proposal: {proposal.Title}\n" +
        $"Source: {(proposal.Source == "review" ? "AI review finding" : "Security scan finding")} {proposal.FindingRef}\n\n" +
        $"The replacement was verified against the file at `{baseRef}` before this branch was created: the original lines matched exactly.\n" +
        $"Base branch: `{baseBranch}`.\n\n" +
        "Created by AegisCode. Review the change before merging — this draft makes no claim the fix is complete.";

    /// <summary>
    ///     Provider calls fail as upstream errors, never as silent nulls or generic
    ///     failures. A caller-cancelled request still propagates as cancellation.
    /// </summary>
    private static async Task<T> Upstream<T>(Func<Task<T>> call, CancellationToken ct)
    {
        try
        {
            return await call();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            throw new DraftPullRequestUpstreamException($"GitHub request failed: {ex.Message}", ex);
        }
    }

    private static string? FirstNonBlank(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private sealed record ResolvedDraftPrInputs(
        string Owner,
        string Repository,
        string? BaseRef,
        string? BaseSha,
        string FilePath,
        int? StartLine,
        int? EndLine,
        string OriginalCode,
        string SuggestedCode);
}
