// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.ExecutionAuthority;
using MeisterDev.ProPR.Extensibility.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeisterDev.ProPR.Infrastructure.Features.ExecutionAuthority;

/// <summary>
///     Registers the services through which ProPR consults an execution authority. ProPR registers no
///     <see cref="IExecutionAuthority" /> itself; while none is registered, these services ask nothing, look nothing up
///     and store nothing.
/// </summary>
public static class ExecutionAuthorityServiceCollectionExtensions
{
    /// <summary>Adds admission, usage reporting and the usage report feed.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="hasDatabase">Whether the host has a database, which the usage report store needs.</param>
    public static IServiceCollection AddExecutionAuthoritySupport(this IServiceCollection services, bool hasDatabase)
    {
        services.TryAddSingleton<IExecutionScopeAccessor, ExecutionScopeAccessor>();
        services.TryAddScoped<IExecutionAdmission, ExecutionAdmission>();
        services.TryAddScoped<IExecutionUsageSink, ExecutionUsageSink>();
        services.TryAddScoped<IExecutionStopper, ExecutionStopper>();

        if (hasDatabase)
        {
            services.TryAddSingleton<IUsageReportStore, UsageReportStore>();
            services.TryAddSingleton<IUsageReportFeed>(provider => provider.GetRequiredService<IUsageReportStore>());
        }

        return services;
    }
}
