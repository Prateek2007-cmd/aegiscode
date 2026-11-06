// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Api.Extensions;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using MeisterDev.ProPR.Web;

namespace MeisterDev.ProPR.Api.Controllers;

/// <summary>Admin endpoint for dismissing a review finding into thread memory for one client.</summary>
[ApiController]
[Route("clients/{clientId:guid}")]
public sealed partial class FindingDismissalController(ILogger<FindingDismissalController> logger) : ControllerBase
{
    /// <summary>
    ///     Dismisses a finding by storing it as an admin-dismissed memory record.
    ///     Future reviews will suppress similar findings via the memory reconsideration pipeline.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="request">Dismiss request with the finding message and optional label.</param>
    /// <param name="memoryService">Service used to persist the dismissal as thread memory.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="201">Finding dismissed and memory record created.</response>
    /// <response code="400">Validation failure.</response>
    /// <response code="403">Caller lacks administrator access to the client.</response>
    [HttpPost("reviewing/dismiss-finding")]
    [HttpPost("dismiss-finding")]
    [ProducesResponseType(typeof(ThreadMemoryRecordDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> DismissFinding(
        Guid clientId,
        [FromBody] DismissFindingRequest request,
        [FromServices] IThreadMemoryService memoryService,
        [FromServices] IReviewerMemoryService? reviewerMemoryService = null,
        CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientAdministrator);
        if (auth is not null)
        {
            return auth;
        }

        if (string.IsNullOrWhiteSpace(request.FindingMessage))
        {
            return this.BadRequest(new { error = "findingMessage is required." });
        }

        var record = await memoryService.DismissFindingAsync(
            clientId,
            request.FilePath,
            request.FindingMessage,
            request.Label,
            ct);

        LogDismissalCreated(logger, record.Id, clientId);

        // The dismissal is the reviewer's lesson for this kind of finding: count it toward
        // the rule's memory so repeated dismissals can mute the rule. Learning is
        // best-effort — it must never fail the dismissal itself, so a failure is logged
        // and the dismissal record still stands.
        if (reviewerMemoryService is not null)
        {
            try
            {
                var ruleKey = ReviewerMemoryRules.KeyForReviewFinding(request.FindingMessage);
                var label = ReviewerMemoryRules.DisplayLabel(request.FindingMessage);
                if (ruleKey is not null && label is not null)
                {
                    await reviewerMemoryService.RecordOutcomeAsync(
                        clientId,
                        ruleKey,
                        label,
                        ReviewerMemoryRules.ReviewSource,
                        ReviewerMemoryOutcome.Dismissed,
                        ct);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Failed to record reviewer-memory outcome for dismissed finding {RecordId}; the dismissal itself was saved.",
                    record.Id);
            }
        }

        return this.StatusCode(201, ToDto(record));
    }

    private static ThreadMemoryRecordDto ToDto(ThreadMemoryRecord r)
    {
        return new ThreadMemoryRecordDto(
            r.Id,
            r.ClientId,
            r.ThreadId,
            r.RepositoryId,
            r.PullRequestId,
            r.FilePath,
            r.ResolutionSummary,
            r.CreatedAt,
            r.UpdatedAt);
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Finding dismissal memory record {Id} created for client {ClientId}")]
    private static partial void LogDismissalCreated(ILogger logger, Guid id, Guid clientId);
}

/// <summary>Request to dismiss a finding and store it as an admin-dismissed memory record.</summary>
/// <param name="FindingMessage">The original finding message to dismiss.</param>
/// <param name="FilePath">Optional file path to scope the dismissal.</param>
/// <param name="Label">Optional human-readable label for the dismissal.</param>
public sealed record DismissFindingRequest(string FindingMessage, string? FilePath = null, string? Label = null);
