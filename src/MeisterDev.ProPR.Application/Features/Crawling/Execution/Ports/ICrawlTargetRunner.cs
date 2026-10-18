// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;

namespace MeisterDev.ProPR.Application.Features.Crawling.Execution.Ports;

public interface ICrawlTargetRunner
{
    Task RunAsync(CrawlConfigurationDto configuration, CancellationToken ct = default);
}
