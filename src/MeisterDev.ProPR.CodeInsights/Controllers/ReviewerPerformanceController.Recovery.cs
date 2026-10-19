// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.CodeInsights.Ports;
using MeisterDev.ProPR.CodeInsights.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace MeisterDev.ProPR.CodeInsights.Controllers;

public sealed partial class ReviewerPerformanceController
{
    /// <summary>Retries classification of bounded retained model concerns without provider reads or review execution.</summary>
    /// <param name="aggregateId">Source aggregate within the administered client scope.</param>
    /// <param name="request">Captured provider namespace and connection for this source.</param>
    /// <param name="ct">Cancels retained classification and persistence.</param>
    /// <response code="200">A bounded local classification retry completed; refresh the measurement to inspect its result.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Tenant administration or the Code Insights capability is missing.</response>
    /// <response code="404">The source is absent or outside the administered client scope.</response>
    /// <response code="409">Collection is disabled, the source is busy or cooling down, or retained proof cannot support this action.</response>
    /// <response code="503">Database analytics services are unavailable.</response>
    [HttpPost("evidence/{aggregateId:guid}/retry-model")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> RetryModelEvidence(Guid aggregateId, ReviewerPerformanceRetryModelRequest request, CancellationToken ct = default)
    {
        var scope = await scopeResolver.ResolveForTenantAdministrationAsync(this.HttpContext, null, ct);
        if (scope.Denied is not null)
        {
            return scope.Denied;
        }

        if (modelEvidenceMaintenance is null)
        {
            return this.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        var outcome = await modelEvidenceMaintenance.RetryAsync(new(aggregateId, request.ProviderScope, request.ConnectionId), scope.ClientIds, ct);
        if (outcome == ModelEvidenceRetryResult.Retried)
        {
            return this.Ok();
        }

        if (outcome == ModelEvidenceRetryResult.NotFound)
        {
            return this.NotFound();
        }

        var (title, detail) = outcome switch
        {
            ModelEvidenceRetryResult.Disabled => ("Evidence collection is disabled", "Enable Code Insights collection for this client, then retry."),
            ModelEvidenceRetryResult.Busy => ("Evidence observation is in progress", "Wait for the current evidence observation to finish, then retry."),
            ModelEvidenceRetryResult.Cooldown => ("Retained classification is cooling down",
                "Wait until the next classification attempt is permitted, then retry."),
            _ => ("Historical model evidence cannot be reconstructed",
                "The retained source or execution proof is incomplete. A classification retry cannot reconstruct this evidence."),
        };
        return this.Conflict(new ProblemDetails { Status = StatusCodes.Status409Conflict, Title = title, Detail = detail });
    }

    /// <summary>Queues retained provider evidence for the next background sweep after source or configuration repair.</summary>
    /// <param name="aggregateId">Retained pull-request aggregate within an administered tenant.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="202">Evidence is queued; provider collection runs during a subsequent bounded sweep.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Missing tenant administration or the commercial Code Insights capability.</response>
    /// <response code="404">The aggregate is absent or outside the authorized client scope.</response>
    /// <response code="409">Collection is disabled or an unexpired observation claim is active.</response>
    /// <response code="503">Database analytics services are unavailable.</response>
    [HttpPost("evidence/{aggregateId:guid}/retry")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> RetryEvidence(Guid aggregateId, CancellationToken ct = default)
    {
        var scope = await scopeResolver.ResolveForTenantAdministrationAsync(this.HttpContext, null, ct);
        if (scope.Denied is not null)
        {
            return scope.Denied;
        }

        if (historicalRefreshStore is null)
        {
            return this.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        var outcome = await historicalRefreshStore.RequeueAsync(aggregateId, scope.ClientIds, DateTimeOffset.UtcNow, ct);
        return outcome switch
        {
            CodeInsightHistoricalRequeueResult.Queued => this.StatusCode(StatusCodes.Status202Accepted),
            CodeInsightHistoricalRequeueResult.NotFound => this.NotFound(),
            CodeInsightHistoricalRequeueResult.CollectionDisabled => this.Conflict(
                new ProblemDetails
                {
                    Status = StatusCodes.Status409Conflict,
                    Title = "Evidence collection is disabled",
                    Detail = "Enable Code Insights collection for this client, then retry.",
                }),
            _ => this.Conflict(
                new ProblemDetails
                {
                    Status = StatusCodes.Status409Conflict,
                    Title = "Evidence observation is in progress",
                    Detail = "Wait for the current evidence observation to finish, then retry.",
                }),
        };
    }
}
