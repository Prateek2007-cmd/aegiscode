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
///     Ask-your-codebase: a question is answered only from code retrieved through ProCursor
///     for this client, with file citations for every claim. When nothing relevant is
///     retrieved, ProCursor is unavailable, or the model answers without citing the retrieved
///     snippets, the endpoint answers honestly that it does not know — it never invents an
///     answer, a file, or a line number. Every endpoint is client-scoped: another client's
///     knowledge is never reachable from here.
/// </summary>
[ApiController]
[Route("clients/{clientId:guid}/code-qa")]
public sealed class CodeQaController(ICodeQaService codeQaService) : ControllerBase
{
    /// <summary>Asks the client's codebase a question and returns the grounded answer with citations.</summary>
    /// <response code="200">The answer, grounded or an honest "don't know" — never an invented answer.</response>
    /// <response code="400">The question is missing or empty.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller has no role on this client.</response>
    [HttpPost("ask")]
    [ProducesResponseType(typeof(AskCodeQuestionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> AskQuestion(Guid clientId, [FromBody] AskCodeQuestionRequest request, CancellationToken ct)
    {
        if (AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientUser) is { } denied)
        {
            return denied;
        }

        try
        {
            var response = await codeQaService.AskAsync(clientId, request, ct);
            return this.Ok(response);
        }
        catch (ArgumentException ex)
        {
            return this.BadRequest(new { error = ex.Message });
        }
    }
}
