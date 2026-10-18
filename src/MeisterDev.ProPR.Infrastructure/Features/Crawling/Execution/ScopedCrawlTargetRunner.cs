// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Models;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Ports;
using MeisterDev.ProPR.Application.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Infrastructure.Features.Crawling.Execution;

internal sealed class ScopedCrawlTargetRunner(IServiceScopeFactory scopes) : ICrawlTargetRunner
{
    public async Task RunAsync(CrawlConfigurationDto configuration, CancellationToken ct = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<PrCrawlService>().ProcessConfigurationAsync(configuration, ct);
    }
}
