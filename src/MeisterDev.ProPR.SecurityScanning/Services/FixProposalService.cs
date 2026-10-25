// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.SecurityScanning.Models;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.SecurityScanning.Services;

/// <summary>Raised when an approve/reject is attempted on a proposal that already has a decision. Maps to HTTP 409.</summary>
public sealed class FixProposalAlreadyDecidedException(Guid proposalId, FixProposalStatus status)
    : InvalidOperationException(
        $"Fix proposal {proposalId} was already decided ({status}) and cannot be decided again.")
{
    public Guid ProposalId { get; } = proposalId;
    public FixProposalStatus Status { get; } = status;
}

/// <summary>
///     The human-approval gate for fixes. Proposals sit in <c>PendingApproval</c> until a human
///     approves or rejects them. Approving a proposal does NOT create a pull request or apply a
///     code change — it only records the human's decision; creating the fix is a separate,
///     future step that reads approved proposals.
/// </summary>
public interface IFixProposalService
{
    /// <summary>Validates and persists a new proposal in <c>PendingApproval</c>. Throws <see cref="ArgumentException"/> on invalid input.</summary>
    Task<FixProposalResponse> CreateAsync(Guid clientId, CreateFixProposalRequest request, CancellationToken ct);

    /// <summary>Lists the client's proposals, newest first. Never returns another client's proposals.</summary>
    Task<IReadOnlyList<FixProposalResponse>> ListAsync(Guid clientId, CancellationToken ct);

    /// <summary>Approves a pending proposal. Returns <c>null</c> when it does not exist for this client; throws <see cref="FixProposalAlreadyDecidedException"/> when already decided.</summary>
    Task<FixProposalResponse?> ApproveAsync(Guid clientId, Guid proposalId, string? reason, CancellationToken ct);

    /// <summary>Rejects a pending proposal. Returns <c>null</c> when it does not exist for this client; throws <see cref="FixProposalAlreadyDecidedException"/> when already decided.</summary>
    Task<FixProposalResponse?> RejectAsync(Guid clientId, Guid proposalId, string? reason, CancellationToken ct);
}

/// <inheritdoc />
public sealed class FixProposalService(IFixProposalStore store, ILogger<FixProposalService> logger) : IFixProposalService
{
    /// <inheritdoc />
    public async Task<FixProposalResponse> CreateAsync(
        Guid clientId, CreateFixProposalRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.FindingRef))
        {
            throw new ArgumentException("findingRef is required.", nameof(request));
        }
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            throw new ArgumentException("title is required.", nameof(request));
        }

        var source = FixProposalSources.Normalize(request.Source);
        if (source is null)
        {
            throw new ArgumentException(
                $"source must be one of: {string.Join(", ", FixProposalSources.All)}.", nameof(request));
        }

        // Remediation is stored exactly as given — including null. Never fabricated.
        var record = new FixProposalRecord(
            Guid.NewGuid(),
            clientId,
            request.FindingRef.Trim(),
            source,
            request.Title.Trim(),
            string.IsNullOrWhiteSpace(request.FilePath) ? null : request.FilePath.Trim(),
            request.Line,
            request.Remediation,
            string.IsNullOrWhiteSpace(request.Confidence) ? null : request.Confidence.Trim(),
            FixProposalStatus.PendingApproval,
            DateTimeOffset.UtcNow,
            null,
            null);

        var saved = await store.AddAsync(record, ct);
        logger.LogInformation("Created fix proposal {ProposalId} for client {ClientId} (source {Source}).",
            saved.Id, clientId, source);
        return ToResponse(saved);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FixProposalResponse>> ListAsync(Guid clientId, CancellationToken ct)
    {
        var proposals = await store.ListAsync(clientId, ct);
        return proposals
            .OrderByDescending(p => p.CreatedAt)
            .ThenByDescending(p => p.Id)
            .Select(ToResponse)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<FixProposalResponse?> ApproveAsync(Guid clientId, Guid proposalId, string? reason, CancellationToken ct)
    {
        return await this.DecideAsync(clientId, proposalId, FixProposalStatus.Approved, reason, ct);
    }

    /// <inheritdoc />
    public async Task<FixProposalResponse?> RejectAsync(Guid clientId, Guid proposalId, string? reason, CancellationToken ct)
    {
        return await this.DecideAsync(clientId, proposalId, FixProposalStatus.Rejected, reason, ct);
    }

    private async Task<FixProposalResponse?> DecideAsync(
        Guid clientId, Guid proposalId, FixProposalStatus decision, string? reason, CancellationToken ct)
    {
        var proposal = await store.GetAsync(clientId, proposalId, ct);
        if (proposal is null)
        {
            return null;
        }

        if (proposal.Status != FixProposalStatus.PendingApproval)
        {
            throw new FixProposalAlreadyDecidedException(proposalId, proposal.Status);
        }

        var decided = proposal with
        {
            Status = decision,
            DecidedAt = DateTimeOffset.UtcNow,
            DecisionReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
        };

        var saved = await store.UpdateAsync(decided, ct);
        logger.LogInformation("Fix proposal {ProposalId} for client {ClientId} decided: {Decision}.",
            proposalId, clientId, decision);
        return ToResponse(saved);
    }

    internal static FixProposalResponse ToResponse(FixProposalRecord record)
    {
        return new FixProposalResponse(
            record.Id,
            record.FindingRef,
            record.Source,
            record.Title,
            record.FilePath,
            record.Line,
            record.Remediation,
            record.Confidence,
            record.Status.ToString(),
            record.CreatedAt,
            record.DecidedAt,
            record.DecisionReason);
    }
}
