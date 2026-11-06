// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.SecurityScanning.Models;
using MeisterDev.ProPR.SecurityScanning.Services;
using MeisterDev.ProPR.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace MeisterDev.ProPR.SecurityScanning.Controllers;

/// <summary>
///     Custom review rules: the team's explicit, written-down policy for one client
///     (repo), distinct from reviewer memory, which learns implicitly from
///     accept/dismiss decisions. Every endpoint is client-scoped: a rule id only ever
///     resolves inside its own client, so asking for another client's rule is a 404,
///     never a leak. An invalid regex is rejected at save time with the real regex
///     error (400) and never stored. A rule never invents a finding — enforcement only
///     matches its pattern literally against a real file path or real content, and
///     only ever upgrades a finding's severity, citing the rule by name.
/// </summary>
[ApiController]
[Route("clients/{clientId:guid}/review-rules")]
public sealed class ReviewRulesController(IReviewRuleService reviewRuleService) : ControllerBase
{
    /// <summary>Lists the client's custom review rules, in creation order.</summary>
    /// <response code="200">The rules, possibly empty.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller has no role on this client.</response>
    [HttpGet]
    [ProducesResponseType(typeof(ReviewRuleListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListRules(Guid clientId, CancellationToken ct)
    {
        if (AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientUser) is { } denied)
        {
            return denied;
        }

        var items = await reviewRuleService.ListAsync(clientId, ct);
        return this.Ok(new ReviewRuleListResponse(items));
    }

    /// <summary>Creates a custom review rule. An invalid regex is a 400 with the real regex error.</summary>
    /// <response code="200">The created rule.</response>
    /// <response code="400">Name, pattern, severity, or message is missing or invalid.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks the client administrator role.</response>
    [HttpPost]
    [ProducesResponseType(typeof(ReviewRuleEntry), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateRule(
        Guid clientId,
        [FromBody] CreateReviewRuleRequest request,
        CancellationToken ct)
    {
        if (AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientAdministrator) is { } denied)
        {
            return denied;
        }

        try
        {
            var entry = await reviewRuleService.CreateAsync(clientId, request.Name, request.Pattern, request.IsRegex, request.Severity, request.Message, ct);
            return this.Ok(entry);
        }
        catch (ArgumentException ex)
        {
            return this.BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Replaces a custom review rule, including its enabled state.</summary>
    /// <response code="200">The updated rule.</response>
    /// <response code="400">Name, pattern, severity, or message is missing or invalid.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks the client administrator role.</response>
    /// <response code="404">No such rule for this client.</response>
    [HttpPut("{ruleId:guid}")]
    [ProducesResponseType(typeof(ReviewRuleEntry), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateRule(
        Guid clientId,
        Guid ruleId,
        [FromBody] UpdateReviewRuleRequest request,
        CancellationToken ct)
    {
        if (AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientAdministrator) is { } denied)
        {
            return denied;
        }

        try
        {
            var entry = await reviewRuleService.UpdateAsync(
                clientId, ruleId, request.Name, request.Pattern, request.IsRegex, request.Severity, request.Message, request.Enabled, ct);
            return entry is null ? this.NotFound() : this.Ok(entry);
        }
        catch (ArgumentException ex)
        {
            return this.BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Enables or disables one custom review rule without changing anything else.</summary>
    /// <response code="200">The rule after the change.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks the client administrator role.</response>
    /// <response code="404">No such rule for this client.</response>
    [HttpPost("{ruleId:guid}/set-enabled")]
    [ProducesResponseType(typeof(ReviewRuleEntry), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetEnabled(
        Guid clientId,
        Guid ruleId,
        [FromBody] SetReviewRuleEnabledRequest request,
        CancellationToken ct)
    {
        if (AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientAdministrator) is { } denied)
        {
            return denied;
        }

        var entry = await reviewRuleService.SetEnabledAsync(clientId, ruleId, request.Enabled, ct);
        return entry is null ? this.NotFound() : this.Ok(entry);
    }

    /// <summary>Deletes a custom review rule entirely.</summary>
    /// <response code="200">The rule was deleted.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks the client administrator role.</response>
    /// <response code="404">No such rule for this client.</response>
    [HttpDelete("{ruleId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteRule(Guid clientId, Guid ruleId, CancellationToken ct)
    {
        if (AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientAdministrator) is { } denied)
        {
            return denied;
        }

        var deleted = await reviewRuleService.DeleteAsync(clientId, ruleId, ct);
        return deleted ? this.Ok(new { deleted = true }) : this.NotFound();
    }
}
