// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.SecurityScanning.Models;

/// <summary>Optional repository scope used to bias a code question toward one repo and branch.</summary>
public sealed record CodeQaRepositoryContext(
    string ProviderScopePath,
    string ProviderProjectKey,
    string RepositoryId,
    string Branch);

/// <summary>Request to ask the codebase a question. Only <c>Question</c> is required.</summary>
public sealed record AskCodeQuestionRequest(
    string Question,
    IReadOnlyList<Guid>? KnowledgeSourceIds = null,
    CodeQaRepositoryContext? RepositoryContext = null);

/// <summary>One retrieved snippet an answer is grounded in. Always a real retrieved match — never invented.</summary>
public sealed record CodeQaCitationResponse(
    string ContentPath,
    string? Title,
    string Excerpt,
    double Score,
    string SourceKind,
    string Branch,
    string CommitSha,
    string FreshnessStatus);

/// <summary>
///     The answer to a code question, with the citations it stands on.
///     <c>Answered</c> is false whenever the answer could not be grounded in retrieved code:
///     ProCursor is unavailable, nothing matched, no AI model is configured, the AI call failed,
///     or the model answered without citing the retrieved snippets. In that case <c>Answer</c>
///     says plainly what is missing instead of inventing an answer, and <c>Citations</c> still
///     carries whatever was retrieved so the user can read the snippets themselves.
/// </summary>
public sealed record AskCodeQuestionResponse(
    bool Answered,
    string Answer,
    IReadOnlyList<CodeQaCitationResponse> Citations,
    string RetrievalStatus,
    string? RetrievalNote,
    string? ModelId);
