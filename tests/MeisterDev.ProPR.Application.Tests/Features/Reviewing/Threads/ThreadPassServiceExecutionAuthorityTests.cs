// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Crawling.Webhooks.Ports;
using MeisterDev.ProPR.Application.Features.ExecutionAuthority;
using MeisterDev.ProPR.Application.Features.Reviewing.Threads;
using MeisterDev.ProPR.Application.Features.Reviewing.Threads.Services;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MeisterDev.ProPR.Application.Tests.Features.Reviewing.Threads;

public sealed class ThreadPassServiceExecutionAuthorityTests
{
    private readonly IThreadPassJobRepository _jobs = Substitute.For<IThreadPassJobRepository>();
    private readonly IClientRegistry _clients = Substitute.For<IClientRegistry>();
    private readonly IExecutionAdmission _admission = Substitute.For<IExecutionAdmission>();

    private readonly ThreadPassJob _job = new(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 1, 1, "revision", "trigger");

    // The attempt has begun once the pass is claimed. A lookup that fails while its execution scope is opened is
    // recorded as a failed attempt, so the pass follows the normal retry delay and is not left in processing.
    [Fact]
    public async Task AnExecutionScopeThatCannotBeOpened_IsRecordedAsAFailedAttempt()
    {
        this._admission.IsActive.Returns(true);
        this._admission.AdmitThreadPassAsync(this._job, Arg.Any<CancellationToken>()).Returns(ExecutionAdmissionResult.Allowed);
        this._jobs.TryBeginAttemptAsync(this._job.Id, Arg.Any<CancellationToken>()).Returns(true);
        this._clients.GetTenantIdAsync(this._job.ClientId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("The database is unavailable."));

        await this.Service().ProcessAsync(this._job);

        await this._jobs.Received(1).RecordAttemptFailureAsync(this._job.Id, "The database is unavailable.", Arg.Any<CancellationToken>());
    }

    private ThreadPassService Service()
    {
        return new ThreadPassService(
            this._jobs,
            Substitute.For<IReviewPrScanThreadPassStore>(),
            Substitute.For<IPullRequestFetcher>(),
            Substitute.For<IScmProviderRegistry>(),
            this._clients,
            Substitute.For<IAiCommentResolutionCore>(),
            Substitute.For<IProtocolRecorder>(),
            NullLogger<ThreadPassService>.Instance,
            executionAdmission: this._admission,
            executionScopeAccessor: new ExecutionScopeAccessor());
    }
}
