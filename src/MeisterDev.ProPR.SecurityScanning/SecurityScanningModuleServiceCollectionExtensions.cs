// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Threading.Channels;
using MeisterDev.ProPR.Application.Features.Crawling.Webhooks.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.SecurityScanning.Models;
using MeisterDev.ProPR.SecurityScanning.Services;
using MeisterDev.ProPR.SecurityScanning.Workers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MeisterDev.ProPR.SecurityScanning;

/// <summary>Registers the deterministic security scanning module.</summary>
public static class SecurityScanningModuleServiceCollectionExtensions
{
    /// <summary>
    ///     Registers scan orchestration, the engine sidecar runner, the background worker,
    ///     and the API controller's dependencies. The worker only does real work when the
    ///     database is available and the module is enabled; otherwise scans fail closed
    ///     with an actionable message at initiation time.
    /// </summary>
    public static IServiceCollection AddSecurityScanningModule(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment? environment = null)
    {
        services.AddOptions<SecurityScanningOptions>()
            .Configure(options => SecurityScanningOptions.Bind(options, configuration));

        // Bounded channel: backpressure instead of unbounded queue growth under scan storms.
        var channel = Channel.CreateBounded<SecurityScanJob>(
            new BoundedChannelOptions(256)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = false,
            });
        services.AddSingleton(channel);
        services.AddSingleton(channel.Reader);
        services.AddSingleton(channel.Writer);

        services.AddScoped<ISecurityScanService, SecurityScanService>();
        services.AddScoped<ISecurityScanEngineRunner, SecurityScanEngineRunner>();
        services.AddScoped<IBlastRadiusService, BlastRadiusService>();

        // Posture trends: read-only aggregation over the stored scans and
        // findings — no new table, no migration. The reader is separate from
        // the service so the aggregation is unit-testable with a fake reader.
        services.AddScoped<ISecurityScanHistoryReader, EfSecurityScanHistoryReader>();
        services.AddScoped<ISecurityTrendsService, SecurityTrendsService>();

        // Fix proposals: JSON-file store (no new table/migration) + the human-approval gate service.
        services.AddSingleton<IFixProposalStore, JsonFileFixProposalStore>();
        services.AddScoped<IFixProposalService, FixProposalService>();

        // Draft PRs: opens a real draft pull request for an approved proposal via the
        // provider gateway registered by the Infrastructure provider modules.
        services.AddScoped<IDraftPullRequestService, DraftPullRequestService>();
        // Ask-your-codebase: grounded Q&A over the client's ProCursor index. Answers come only
        // from retrieved snippets and are withheld when the model does not cite them.
        services.AddScoped<ICodeQaService, CodeQaService>();
        // Merge guard: JSON-file store (same no-migration pattern) + the gate service,
        // registered once and shared by both of its contracts — the review-publication
        // hook (IMergeGuardService) and the dashboard API (IMergeGuardManagementService).
        services.AddSingleton<IMergeGuardStore, JsonFileMergeGuardStore>();
        services.AddScoped<MergeGuardService>();
        services.AddScoped<IMergeGuardService>(sp => sp.GetRequiredService<MergeGuardService>());
        services.AddScoped<IMergeGuardManagementService>(sp => sp.GetRequiredService<MergeGuardService>());
        // Dependency Radar: OSV client + JSON-file alert/state store + the watch
        // service + the scheduled worker. No new database table and no EF migration.
        services.AddHttpClient("radar-osv");
        services.AddSingleton<IRadarStore, JsonFileRadarStore>();
        services.AddScoped<IOsvClient, OsvClient>();
        services.AddScoped<IRadarService, RadarService>();
        services.AddHostedService<RadarWorker>();
        // Reviewer memory: JSON-file store (same root as fix proposals) + the learning service.
        services.AddSingleton<IReviewerMemoryStore, JsonFileReviewerMemoryStore>();
        services.AddScoped<IReviewerMemoryService, ReviewerMemoryService>();
        // Custom review rules: JSON-file store (same no-migration pattern) + the CRUD
        // service, registered once and shared by both of its contracts — the dashboard
        // API (IReviewRuleService) and the enforcement read side
        // (ICustomReviewRuleService) used by the review publisher and the scan worker.
        services.AddSingleton<IReviewRuleStore, JsonFileReviewRuleStore>();
        services.AddScoped<ReviewRuleService>();
        services.AddScoped<IReviewRuleService>(sp => sp.GetRequiredService<ReviewRuleService>());
        services.AddScoped<ICustomReviewRuleService>(sp => sp.GetRequiredService<ReviewRuleService>());
        // Push scans: JSON-file store (same no-migration pattern) + the service that
        // the webhook handler calls through IPushScanTrigger and the dashboard API
        // calls through IPushScanManagementService. Registered once and shared by
        // both contracts, like the merge guard.
        services.AddSingleton<IPushScanStore, JsonFilePushScanStore>();
        services.AddScoped<PushScanService>();
        services.AddScoped<IPushScanTrigger>(sp => sp.GetRequiredService<PushScanService>());
        services.AddScoped<IPushScanManagementService>(sp => sp.GetRequiredService<PushScanService>());

        services.AddHostedService<SecurityScanWorker>();

        return services;
    }
}
