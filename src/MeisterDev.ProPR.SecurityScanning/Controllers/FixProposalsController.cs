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
///     Fix proposals: the human-approval gate for fixes. A proposal (rooted in a scan finding
///     or a review comment) sits in <c>PendingApproval</c> until a client administrator approves
///     or rejects it. Approving a proposal does NOT create a GitHub pull request or apply any
///     code change — it only records the human's decision. Every endpoint is client-scoped:
///     a proposal id only ever resolves inside its own client, so asking for another client's
///     proposal is a 404, never a leak.
/// </summary>
[ApiController]
[Route("clients/{clientId:guid}/fix-proposals")]
public sealed class FixProposalsController(IFixProposalService fixProposalService) : ControllerBase
{
    /// <summary>Creates a fix proposal in PendingApproval. Remediation may be null and is stored as given.</summary>
    /// <response code="201">Proposal created.</response>
    /// <response code="400">findingRef or title missing, or source is not scan|review.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks the client administrator role.</response>
    [HttpPost]
    [ProducesResponseType(typeof(FixProposalResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateProposal(
        Guid clientId, [FromBody] CreateFixProposalRequest request, CancellationToken ct)
    {
        if (AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientAdministrator) is { } denied)
        {
            return denied;
        }

        try
        {
            var proposal = await fixProposalService.CreateAsync(clientId, request, ct);
            return this.StatusCode(StatusCodes.Status201Created, proposal);
        }
        catch (ArgumentException ex)
        {
            return this.BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Lists the client's fix proposals, newest first.</summary>
    /// <response code="200">The proposals, possibly empty.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller has no role on this client.</response>
    [HttpGet]
    [ProducesResponseType(typeof(FixProposalListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListProposals(Guid clientId, CancellationToken ct)
    {
        if (AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientUser) is { } denied)
        {
            return denied;
        }

        var items = await fixProposalService.ListAsync(clientId, ct);
        return this.Ok(new FixProposalListResponse(items));
    }

    /// <summary>Approves a pending proposal. Records the decision; creates no pull request and changes no code.</summary>
    /// <response code="200">Approved.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks the client administrator role.</response>
    /// <response code="404">No such proposal for this client.</response>
    /// <response code="409">The proposal was already decided.</response>
    [HttpPost("{proposalId:guid}/approve")]
    [ProducesResponseType(typeof(FixProposalResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ApproveProposal(
        Guid clientId, Guid proposalId, [FromBody] DecideFixProposalRequest? request, CancellationToken ct)
    {
        if (AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientAdministrator) is { } denied)
        {
            return denied;
        }

        try
        {
            var proposal = await fixProposalService.ApproveAsync(clientId, proposalId, request?.Reason, ct);
            return proposal is null ? this.NotFound() : this.Ok(proposal);
        }
        catch (FixProposalAlreadyDecidedException ex)
        {
            return this.Conflict(new { error = ex.Message });
        }
    }

    /// <summary>Rejects a pending proposal, storing the reason. Records the decision; changes no code.</summary>
    /// <response code="200">Rejected.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks the client administrator role.</response>
    /// <response code="404">No such proposal for this client.</response>
    /// <response code="409">The proposal was already decided.</response>
    [HttpPost("{proposalId:guid}/reject")]
    [ProducesResponseType(typeof(FixProposalResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RejectProposal(
        Guid clientId, Guid proposalId, [FromBody] DecideFixProposalRequest? request, CancellationToken ct)
    {
        if (AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientAdministrator) is { } denied)
        {
            return denied;
        }

        try
        {
            var proposal = await fixProposalService.RejectAsync(clientId, proposalId, request?.Reason, ct);
            return proposal is null ? this.NotFound() : this.Ok(proposal);
        }
        catch (FixProposalAlreadyDecidedException ex)
        {
            return this.Conflict(new { error = ex.Message });
        }
    }
}
