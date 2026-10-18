// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.Crawling.Execution.Models;

public sealed class DiscussionSynchronizationOptions
{
    public int ReconciliationIntervalSeconds { get; set; } = 300;
    public int GlobalConcurrency { get; set; } = 8;
    public int ProviderHostConcurrency { get; set; } = 4;
    public int ClientProviderHostConcurrency { get; set; } = 1;
    public int OperationTimeoutSeconds { get; set; } = 90;
    public TimeSpan ReconciliationInterval => TimeSpan.FromSeconds(Math.Max(10, this.ReconciliationIntervalSeconds));
}
