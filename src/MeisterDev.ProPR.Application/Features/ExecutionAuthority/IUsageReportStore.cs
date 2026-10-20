// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Extensibility.Execution;

namespace MeisterDev.ProPR.Application.Features.ExecutionAuthority;

/// <summary>Stores usage reports in sequence and reads them back for an execution authority.</summary>
public interface IUsageReportStore : IUsageReportFeed
{
    /// <summary>
    ///     Stores one report and returns it with the sequence it was given. The store opens its own database context,
    ///     because the calls of one execution complete on concurrent tasks.
    /// </summary>
    /// <param name="report">The report to store; its sequence is ignored.</param>
    /// <param name="ct">The cancellation token.</param>
    Task<UsageReport> AppendAsync(UsageReport report, CancellationToken ct = default);
}
