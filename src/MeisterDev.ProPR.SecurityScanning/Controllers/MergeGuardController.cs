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
///     Merge guard: a GitHub check run on a pull request head that reports failure when
///     the latest scan or the just-published review has blocking findings at or above
///     the client's threshold. Reading the state never claims a pass: an empty check
///     list means the guard never reported for this client. The check itself only
///     reports the decision — actually blocking a merge needs the
///     "AegisCode Merge Guard" check added to the branch's required status checks,
///     which the dashboard says plainly.
/// </summary>
[ApiController]
[Route("clients/{clientId:guid}/merge-guard")]
public sealed class MergeGuardController(IMergeGuardManagementService mergeGuardService) : ControllerBase
{
    /// <summary>Returns the client's guard config plus its latest checks, newest first.</summary>
    /// <response code="200">The guard state, possibly with no checks yet.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller has no role on this client.</response>
    [HttpGet]
    [ProducesResponseType(typeof(MergeGuardStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetStatus(Guid clientId, CancellationToken ct)
    {
        if (AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientUser) is { } denied)
        {
            return denied;
        }

        var status = await mergeGuardService.GetStatusAsync(clientId, ct);
        return this.Ok(status);
    }

    /// <summary>
    ///     Saves the client's guard configuration. Threshold must be Critical, High, Medium,
    ///     or Low; anything else is a 400, never silently remapped.
    /// </summary>
    /// <response code="200">The saved configuration.</response>
    /// <response code="400">Threshold is not a known value.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks the client administrator role.</response>
    [HttpPut]
    [ProducesResponseType(typeof(MergeGuardConfigResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> UpdateConfig(Guid clientId, [FromBody] UpdateMergeGuardConfigRequest request, CancellationToken ct)
    {
        if (AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientAdministrator) is { } denied)
        {
            return denied;
        }

        try
        {
            var config = await mergeGuardService.UpdateConfigAsync(clientId, request.Enabled, request.Threshold, ct);
            return this.Ok(config);
        }
        catch (ArgumentException ex)
        {
            return this.BadRequest(new { error = ex.Message });
        }
    }
}
