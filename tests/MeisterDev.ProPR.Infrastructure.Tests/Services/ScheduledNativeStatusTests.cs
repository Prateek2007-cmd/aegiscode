// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Reviewing;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Security;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Services;

public sealed class ScheduledNativeStatusTests
{
    [Fact]
    public async Task SelectedQuery_MissingConnectionCannotFallBackToHostCredentials()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitLab, "https://gitlab.example.test");
        var repository = new RepositoryRef(host, "123", "owner", "owner/repo");
        var connections = Substitute.For<IClientScmConnectionRepository>();
        var http = Substitute.For<IHttpClientFactory>();
        var query = new GitLabCodeReviewQueryService(new GitLabConnectionVerifier(connections, http), http);

        await Assert.ThrowsAsync<InvalidOperationException>(() => query.GetReviewAsync(
            clientId,
            new(repository, CodeReviewPlatformKind.PullRequest, "7", 7), selected: new(Guid.NewGuid(), host.HostBaseUrl)));
        await connections.DidNotReceiveWithAnyArgs().GetOperationalConnectionAsync(default, default!, default);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("unknown")]
    [InlineData("locked")]
    public void UnknownNativeStatus_DoesNotEstablishClosure(string? status)
    {
        Assert.Throws<InvalidOperationException>(() => GitLabCodeReviewQueryService.MapState(status));
    }
}
