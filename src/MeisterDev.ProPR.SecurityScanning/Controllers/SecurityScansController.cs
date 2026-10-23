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
///     Deterministic security scanning: start scans, poll status, read findings,
///     download SBOMs and reports. Every endpoint is client-scoped: the caller must
///     hold a role on the client, and a client id only ever narrows what the caller
///     may already see — asking for another client's scans is a 403, never an empty list.
/// </summary>
[ApiController]
[Route("clients/{clientId:guid}/security-scans")]
public sealed class SecurityScansController(ISecurityScanService scanService) : ControllerBase
{
    /// <summary>Starts a security scan of a repository. Returns 202 with the scan identity.</summary>
    /// <param name="clientId">Owning client. The caller must be a client administrator.</param>
    /// <param name="request">Repository URL, optional ref, optional scanner subset.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="202">Scan queued.</response>
    /// <response code="400">Invalid request (bad URL, unknown scanners).</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks the client administrator role.</response>
    /// <response code="422">Repository URL uses an unsupported host or scheme.</response>
    /// <response code="503">Scanning is disabled or the engine is not configured.</response>
    [HttpPost]
    [ProducesResponseType(typeof(StartSecurityScanResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> StartScan(
        Guid clientId, [FromBody] StartSecurityScanRequest request, CancellationToken ct)
    {
        if (AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientAdministrator) is { } denied)
        {
            return denied;
        }

        try
        {
            var response = await scanService.StartScanAsync(
                clientId, request, AuthHelpers.GetUserId(this.HttpContext), ct);
            return this.StatusCode(StatusCodes.Status202Accepted, response);
        }
        catch (ArgumentException ex)
        {
            return this.UnprocessableEntity(new { error = ex.Message });
        }
        catch (SecurityScanEngineException ex)
        {
            return this.StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ex.Message });
        }
    }

    /// <summary>Lists scans for the client, newest first.</summary>
    /// <response code="200">The scans, possibly empty.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller has no role on this client.</response>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<SecurityScanResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListScans(
        Guid clientId, [FromQuery] int limit = 20, [FromQuery] int offset = 0, CancellationToken ct = default)
    {
        if (AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientUser) is { } denied)
        {
            return denied;
        }

        return this.Ok(await scanService.ListScansAsync(clientId, limit, offset, ct));
    }

    /// <summary>Returns scan status, progress, coverage, and severity summary.</summary>
    /// <response code="200">The scan.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller has no role on this client.</response>
    /// <response code="404">No such scan for this client.</response>
    [HttpGet("{scanId:guid}")]
    [ProducesResponseType(typeof(SecurityScanResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetScan(Guid clientId, Guid scanId, CancellationToken ct)
    {
        if (AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientUser) is { } denied)
        {
            return denied;
        }

        var scan = await scanService.GetScanAsync(clientId, scanId, ct);
        return scan is null ? this.NotFound() : this.Ok(scan);
    }

    /// <summary>Cancels a queued or running scan. Idempotent.</summary>
    /// <response code="200">Cancelled (or already terminal).</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks the client administrator role.</response>
    /// <response code="404">No such scan for this client.</response>
    [HttpDelete("{scanId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CancelScan(Guid clientId, Guid scanId, CancellationToken ct)
    {
        if (AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientAdministrator) is { } denied)
        {
            return denied;
        }

        return await scanService.CancelScanAsync(clientId, scanId, ct)
            ? this.Ok(new { status = nameof(SecurityScanStatus.Cancelled) })
            : this.NotFound();
    }

    /// <summary>Returns paginated findings for a scan, filterable by severity, scanner, and status.</summary>
    /// <response code="200">The findings, possibly empty.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller has no role on this client.</response>
    [HttpGet("{scanId:guid}/findings")]
    [ProducesResponseType(typeof(PagedSecurityFindingsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetFindings(
        Guid clientId,
        Guid scanId,
        [FromQuery] string? severity = null,
        [FromQuery] string? scanner = null,
        [FromQuery] string? status = null,
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0,
        CancellationToken ct = default)
    {
        if (AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientUser) is { } denied)
        {
            return denied;
        }

        return this.Ok(await scanService.GetFindingsAsync(
            clientId, scanId, severity, scanner, status, limit, offset, ct));
    }

    /// <summary>Downloads the CycloneDX SBOM produced by the scan, when one exists.</summary>
    /// <response code="200">The SBOM JSON file.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller has no role on this client.</response>
    /// <response code="404">No SBOM for this scan.</response>
    [HttpGet("{scanId:guid}/sbom")]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSbom(Guid clientId, Guid scanId, CancellationToken ct)
    {
        if (AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientUser) is { } denied)
        {
            return denied;
        }

        var sbomJson = await scanService.GetSbomJsonAsync(clientId, scanId, ct);
        if (sbomJson is null)
        {
            return this.NotFound(new { error = "No SBOM was produced for this scan." });
        }

        return this.File(
            System.Text.Encoding.UTF8.GetBytes(sbomJson),
            "application/json",
            $"{scanId:N}.cyclonedx.json");
    }

    /// <summary>Downloads the full JSON security report for a scan.</summary>
    /// <response code="200">The report JSON file.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller has no role on this client.</response>
    /// <response code="404">No such scan for this client.</response>
    [HttpGet("{scanId:guid}/report")]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetReport(Guid clientId, Guid scanId, CancellationToken ct)
    {
        if (AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientUser) is { } denied)
        {
            return denied;
        }

        var scan = await scanService.GetScanAsync(clientId, scanId, ct);
        if (scan is null)
        {
            return this.NotFound();
        }

        var findings = await scanService.GetFindingsAsync(
            clientId, scanId, null, null, null, 5000, 0, ct);
        var sbomJson = await scanService.GetSbomJsonAsync(clientId, scanId, ct);

        var report = new
        {
            scan.ScanId,
            scan.ClientId,
            scan.RepositoryUrl,
            scan.Ref,
            scan.Status,
            scan.QueuedAt,
            scan.StartedAt,
            scan.FinishedAt,
            scan.Error,
            scan.Coverage,
            scan.Summary,
            findings = findings.Items,
            sbom = sbomJson is null ? null : System.Text.Json.JsonSerializer.Deserialize<object>(sbomJson),
            limitations = new[]
            {
                "Deterministic scanners only: dependency/CVE matching (OSV.dev), secret detection (gitleaks), SBOM.",
                "No static application security testing (SAST) engine is wired in yet.",
                "AI remediation is not part of this report.",
                "Secret values are redacted; rotate any exposed credentials.",
            },
        };

        return this.File(
            System.Text.Encoding.UTF8.GetBytes(
                System.Text.Json.JsonSerializer.Serialize(report,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true })),
            "application/json",
            $"{scanId:N}.security-report.json");
    }

    /// <summary>Suppresses a finding with a mandatory, audited reason.</summary>
    /// <response code="200">Suppressed.</response>
    /// <response code="400">Missing reason.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks the client administrator role.</response>
    /// <response code="404">No such finding for this client.</response>
    [HttpPost("{scanId:guid}/findings/{findingId:guid}/suppress")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SuppressFinding(
        Guid clientId, Guid scanId, Guid findingId,
        [FromBody] SuppressFindingRequest request, CancellationToken ct)
    {
        if (AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientAdministrator) is { } denied)
        {
            return denied;
        }

        try
        {
            var ok = await scanService.SuppressFindingAsync(
                clientId, findingId, request.Reason, AuthHelpers.GetUserId(this.HttpContext), ct);
            return ok ? this.Ok(new { status = nameof(SecurityFindingStatus.Suppressed) }) : this.NotFound();
        }
        catch (ArgumentException ex)
        {
            return this.BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Acknowledges a finding (manual triage; a rescan may reopen it).</summary>
    /// <response code="200">Acknowledged.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks the client administrator role.</response>
    /// <response code="404">No such finding for this client.</response>
    [HttpPost("{scanId:guid}/findings/{findingId:guid}/acknowledge")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AcknowledgeFinding(
        Guid clientId, Guid scanId, Guid findingId, CancellationToken ct)
    {
        if (AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientAdministrator) is { } denied)
        {
            return denied;
        }

        var ok = await scanService.AcknowledgeFindingAsync(clientId, findingId, ct);
        return ok ? this.Ok(new { status = nameof(SecurityFindingStatus.Acknowledged) }) : this.NotFound();
    }
}
