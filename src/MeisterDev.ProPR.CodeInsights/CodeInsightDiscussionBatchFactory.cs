// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.Application.Support;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.CodeInsights.Taxonomy;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.CodeInsights;

/// <summary>Reads finding receipts and effective classifier configuration once for each pull request.</summary>
public sealed class CodeInsightDiscussionBatchFactory(
    ICodeInsightsCollectionGate gate,
    ICodeInsightFindingStore findings,
    IDbContextFactory<MeisterProPRDbContext> factory,
    ICodeInsightTaxonomyService? taxonomy = null,
    MeisterDev.ProPR.Application.AI.IAiConfigurationVersionReader? configurationVersions = null) : ICodeInsightDiscussionBatchFactory
{
    public async Task<CodeInsightDiscussionBatch?> CreateAsync(CodeInsightPullRequestKey key, string providerScope, CancellationToken ct = default)
    {
        if (!await gate.IsCollectionEnabledAsync(key.ClientId, ct))
        {
            return null;
        }

        var collected = (await findings.GetFindingsForPullRequestAsync(key, ct)).Where(finding => finding.ProviderScope == providerScope).ToArray();
        var configuration = await (configurationVersions ??
                                   new MeisterDev.ProPR.Infrastructure.Features.Crawling.Persistence.DiscussionAiConfigurationVersionReader(factory))
            .ReadAsync(key.ClientId, [AiPurpose.InsightsClassification], ct);
        var vocabulary = taxonomy is null ? null : await taxonomy.GetAssignableTaxonomyAsync(key.ClientId, ct);
        var dependency = DiscussionThreadProgress.Fingerprint(
            DiscussionFingerprints.CanonicalValue(
                new
                {
                    configuration,
                    vocabulary,
                    findings = collected.OrderBy(finding => finding.Id).Select(finding => new
                    {
                        finding.Id,
                        finding.ProviderScope,
                        finding.ProviderThreadId,
                        finding.ProviderCommentId,
                        finding.FilePath,
                        finding.LineNumber,
                        finding.RevisionKey,
                        finding.Message
                    }),
                }));
        return new CodeInsightDiscussionBatch(key, providerScope, collected, dependency);
    }
}
