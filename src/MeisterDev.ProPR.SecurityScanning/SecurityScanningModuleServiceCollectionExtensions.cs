// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Threading.Channels;
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
        var channel = Channel.CreateBounded<SecurityScanJob>(new BoundedChannelOptions(256)
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
        services.AddHostedService<SecurityScanWorker>();

        return services;
    }
}
