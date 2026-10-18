// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Features.Crawling.Execution.Ports;

public sealed record CrawlWorkScope(Guid ClientId, ScmProvider Provider, string Host);

public interface ICrawlWorkScheduler
{
    Task RunAsync<T>(IReadOnlyList<T> work, Func<T, CrawlWorkScope> scope, Func<T, CancellationToken, Task> execute, CancellationToken ct = default);
}
