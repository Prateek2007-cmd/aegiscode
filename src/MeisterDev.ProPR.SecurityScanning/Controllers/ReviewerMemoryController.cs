// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.SecurityScanning.Models;
using MeisterDev.ProPR.SecurityScanning.Services;
using MeisterDev.ProPR.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace MeisterDev.ProPR.SecurityScanning.Controllers;

/// <summary>
///     Reviewer memory: what the reviewer has learned from this client's accept and dismiss
///     decisions. Every endpoint is client-scoped: a rule key only ever resolves inside its
///     own client, so asking for another client's rules is a 404, never a leak. Nothing here
///     is inferred — every count comes from a decision the user actually made, and a muted
///     rule is always shown as muted, never hidden.
/// </summary>
[ApiController]
[Route("clients/{clientId:guid}/reviewer-memory")]
public sealed class ReviewerMemoryController(IReviewerMemoryService reviewerMemoryService) : ControllerBase
{
    /// <summary>Lists the client's learned rules, most recently active first.</summary>
    /// <response code="200">The learned rules, possibly empty.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller has no role on this client.</response>
    [HttpGet]
    [ProducesResponseType(typeof(ReviewerMemoryListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListRules(Guid clientId, CancellationToken ct)
    {
        if (AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientUser) is { } denied)
        {
            return denied;
        }

        var items = await reviewerMemoryService.ListAsync(clientId, ct);
        return this.Ok(new ReviewerMemoryListResponse(items));
    }

    /// <summary>
    ///     Records one human outcome for a rule. The rule key may be omitted, in which case it is
    ///     derived from the label and source; an unusable label, source, or outcome is a 400.
    /// </summary>
    /// <response code="200">The updated learned rule.</response>
    /// <response code="400">Label, source, or outcome is missing or invalid.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks the client administrator role.</response>
    [HttpPost("outcomes")]
    [ProducesResponseType(typeof(ReviewerMemoryEntry), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> RecordOutcome(
        Guid clientId,
        [FromBody] RecordReviewerMemoryOutcomeRequest request,
        CancellationToken ct)
    {
        if (AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientAdministrator) is { } denied)
        {
            return denied;
        }

        if (!Enum.TryParse<ReviewerMemoryOutcome>(request.Outcome, ignoreCase: true, out var outcome)
            || !Enum.IsDefined(outcome))
        {
            return this.BadRequest(new { error = "outcome must be \"accepted\" or \"dismissed\"." });
        }

        string? ruleKey = request.RuleKey;
        if (string.IsNullOrWhiteSpace(ruleKey))
        {
            ruleKey = request.Source?.Trim().ToLowerInvariant() switch
            {
                ReviewerMemoryRules.ReviewSource => ReviewerMemoryRules.KeyForReviewFinding(request.Label),
                ReviewerMemoryRules.ScanSource => ReviewerMemoryRules.KeyForScanFinding(request.Label),
                _ => null,
            };
        }

        try
        {
            var entry = await reviewerMemoryService.RecordOutcomeAsync(
                clientId,
                ruleKey ?? string.Empty,
                request.Label ?? string.Empty,
                request.Source ?? string.Empty,
                outcome,
                ct);
            return this.Ok(entry);
        }
        catch (ArgumentException ex)
        {
            return this.BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Un-mutes a muted rule, keeping its counts. A rule that was never muted is returned unchanged.</summary>
    /// <response code="200">The rule after un-muting.</response>
    /// <response code="400">The rule key is not well-formed.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks the client administrator role.</response>
    /// <response code="404">No such rule for this client.</response>
    [HttpPost("unmute")]
    [ProducesResponseType(typeof(ReviewerMemoryEntry), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UnmuteRule(
        Guid clientId,
        [FromBody] ReviewerMemoryRuleRequest request,
        CancellationToken ct)
    {
        if (AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientAdministrator) is { } denied)
        {
            return denied;
        }

        try
        {
            var entry = await reviewerMemoryService.UnmuteAsync(clientId, request.RuleKey, ct);
            return entry is null ? this.NotFound() : this.Ok(entry);
        }
        catch (ArgumentException ex)
        {
            return this.BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Forgets a rule entirely — counts and mute state are deleted.</summary>
    /// <response code="200">The rule was forgotten.</response>
    /// <response code="400">The rule key is not well-formed.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks the client administrator role.</response>
    /// <response code="404">No such rule for this client.</response>
    [HttpPost("reset")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ResetRule(
        Guid clientId,
        [FromBody] ReviewerMemoryRuleRequest request,
        CancellationToken ct)
    {
        if (AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientAdministrator) is { } denied)
        {
            return denied;
        }

        try
        {
            var deleted = await reviewerMemoryService.ResetAsync(clientId, request.RuleKey, ct);
            return deleted ? this.Ok(new { deleted = true }) : this.NotFound();
        }
        catch (ArgumentException ex)
        {
            return this.BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>The learned "do not flag" guidance lines for the client's next review, possibly empty.</summary>
    /// <response code="200">The guidance lines.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller has no role on this client.</response>
    [HttpGet("prompt-guidance")]
    [ProducesResponseType(typeof(ReviewerMemoryGuidanceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetPromptGuidance(Guid clientId, CancellationToken ct)
    {
        if (AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientUser) is { } denied)
        {
            return denied;
        }

        var guidance = await reviewerMemoryService.BuildPromptGuidanceAsync(clientId, ct);
        return this.Ok(new ReviewerMemoryGuidanceResponse(guidance));
    }
}
