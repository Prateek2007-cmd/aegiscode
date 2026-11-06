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
///     Push scans: a security scan started automatically when a push webhook arrives
///     for a repository the client covers. Reading the state never claims a scan:
///     an empty recent list means no push ever started a scan for this client.
///     The scan itself only starts when the client has push scans on, the branch is
///     in the filter (main only by default), and the commit was not scanned before.
/// </summary>
[ApiController]
[Route("clients/{clientId:guid}/push-scans")]
public sealed class PushScanController(IPushScanManagementService pushScanService) : ControllerBase
{
    /// <summary>Returns the client's push-scan config plus its latest push-triggered scans, newest first.</summary>
    /// <response code="200">The push-scan state, possibly with no scans yet.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller has no role on this client.</response>
    [HttpGet]
    [ProducesResponseType(typeof(PushScanStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetStatus(Guid clientId, CancellationToken ct)
    {
        if (AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientUser) is { } denied)
        {
            return denied;
        }

        var status = await pushScanService.GetStatusAsync(clientId, ct);
        return this.Ok(status);
    }

    /// <summary>
    ///     Saves the client's push-scan configuration. An empty branch list resets to
    ///     main only; anything else is a 400, never silently remapped.
    /// </summary>
    /// <response code="200">The saved configuration.</response>
    /// <response code="400">The branch list is not usable.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks the client administrator role.</response>
    [HttpPut]
    [ProducesResponseType(typeof(PushScanConfigResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> UpdateConfig(Guid clientId, [FromBody] UpdatePushScanConfigRequest request, CancellationToken ct)
    {
        if (AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientAdministrator) is { } denied)
        {
            return denied;
        }

        try
        {
            var config = await pushScanService.UpdateConfigAsync(clientId, request.Enabled, request.Branches, ct);
            return this.Ok(config);
        }
        catch (ArgumentException ex)
        {
            return this.BadRequest(new { error = ex.Message });
        }
    }
}
