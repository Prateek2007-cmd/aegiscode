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
///     Dependency Radar: the watch that re-checks a client's stored SBOMs against
///     OSV.dev and alerts when a new Critical or High CVE appears. Every endpoint
///     is client-scoped: asking for another client's alerts is a 403, never an
///     empty list. A pass that finds nothing new says so; a pass that could not
///     check anything says that instead.
/// </summary>
[ApiController]
[Route("clients/{clientId:guid}/radar")]
public sealed class RadarController(IRadarService radarService) : ControllerBase
{
    /// <summary>Lists the client's radar alerts, newest first.</summary>
    /// <response code="200">The alerts, possibly empty.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller has no role on this client.</response>
    [HttpGet("alerts")]
    [ProducesResponseType(typeof(RadarAlertListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListAlerts(Guid clientId, CancellationToken ct)
    {
        if (AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientUser) is { } denied)
        {
            return denied;
        }

        var items = await radarService.ListAlertsAsync(clientId, ct);
        return this.Ok(new RadarAlertListResponse(items));
    }

    /// <summary>Runs one radar pass for the client right now and returns what it found, including "nothing new".</summary>
    /// <response code="200">The pass result.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks the client administrator role.</response>
    /// <response code="503">Dependency Radar is disabled on this installation.</response>
    [HttpPost("check-now")]
    [ProducesResponseType(typeof(RadarPassResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> CheckNow(Guid clientId, CancellationToken ct)
    {
        if (AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientAdministrator) is { } denied)
        {
            return denied;
        }

        try
        {
            var result = await radarService.CheckNowAsync(clientId, ct);
            return this.Ok(result);
        }
        catch (RadarUnavailableException ex)
        {
            return this.StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ex.Message });
        }
    }

    /// <summary>Reads the client's radar watch settings.</summary>
    /// <response code="200">The settings.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller has no role on this client.</response>
    [HttpGet("settings")]
    [ProducesResponseType(typeof(RadarSettingsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetSettings(Guid clientId, CancellationToken ct)
    {
        if (AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientUser) is { } denied)
        {
            return denied;
        }

        return this.Ok(await radarService.GetSettingsAsync(clientId, ct));
    }

    /// <summary>Updates the client's radar watch settings. Null fields keep their current value.</summary>
    /// <response code="200">The updated settings.</response>
    /// <response code="400">The interval is outside 15–10080 minutes.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks the client administrator role.</response>
    [HttpPut("settings")]
    [ProducesResponseType(typeof(RadarSettingsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> UpdateSettings(Guid clientId, [FromBody] UpdateRadarSettingsRequest request, CancellationToken ct)
    {
        if (AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientAdministrator) is { } denied)
        {
            return denied;
        }

        try
        {
            return this.Ok(await radarService.UpdateSettingsAsync(clientId, request, ct));
        }
        catch (ArgumentException ex)
        {
            return this.BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Acknowledges a New alert. Idempotent: acknowledging an already-acknowledged or resolved alert returns it unchanged.</summary>
    /// <response code="200">The alert.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller has no role on this client.</response>
    /// <response code="404">No such alert for this client.</response>
    [HttpPost("alerts/{alertId:guid}/acknowledge")]
    [ProducesResponseType(typeof(RadarAlertResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AcknowledgeAlert(Guid clientId, Guid alertId, CancellationToken ct)
    {
        if (AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientUser) is { } denied)
        {
            return denied;
        }

        var alert = await radarService.AcknowledgeAlertAsync(clientId, alertId, ct);
        return alert is null ? this.NotFound() : this.Ok(alert);
    }
}
