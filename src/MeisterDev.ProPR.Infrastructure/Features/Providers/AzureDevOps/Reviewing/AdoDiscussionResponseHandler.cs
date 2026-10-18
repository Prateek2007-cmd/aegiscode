// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Reviewing;

/// <summary>Records SDK discussion response size when the transport supplies Content-Length.</summary>
internal sealed class AdoDiscussionResponseHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var discussion = request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath.Contains("/threads", StringComparison.OrdinalIgnoreCase) == true;
        if (!discussion)
        {
            return await base.SendAsync(request, ct);
        }

        var clock = Stopwatch.StartNew();
        try
        {
            var response = await base.SendAsync(request, ct);
            DiscussionSynchronizationMetrics.RecordProviderResponse(
                ScmProvider.AzureDevOps, clock.Elapsed.TotalSeconds,
                response.Content?.Headers.ContentLength, response.IsSuccessStatusCode ? "success" : "failed");
            return response;
        }
        catch
        {
            DiscussionSynchronizationMetrics.RecordProviderResponse(ScmProvider.AzureDevOps, clock.Elapsed.TotalSeconds, null, "failed");
            throw;
        }
    }
}
