// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.SecurityScanning.Models;
using MeisterDev.ProPR.SecurityScanning.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MeisterDev.ProPR.SecurityScanning.Tests;

/// <summary>
///     Coverage for the draft-PR flow: approved-only gating, missing-input validation,
///     the fail-closed line gate (a changed file refuses, never force-applies), the
///     exact patched content handed to the provider, idempotent re-calls, and honest
///     upstream failures. The GitHub gateway is faked; no network is involved.
///     NOTE: run on the laptop with <c>dotnet test</c>; testhost is broken in the VM.
/// </summary>
public sealed class DraftPullRequestServiceTests : IDisposable
{
    private const string HeadContent =
        "using System;\n\npublic sealed class Foo\n{\n    public void Bar()\n    {\n        var x = 1;\n        var y = 2;\n    }\n}";

    private const string PatchedContent =
        "using System;\n\npublic sealed class Foo\n{\n    public void Bar()\n    {\n        var x = 2;\n        var y = 2;\n    }\n}";

    private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "aegis-draft-pr-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeDraftPullRequestGateway gateway = new();

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(this.tempRoot))
            {
                Directory.Delete(this.tempRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            /* best-effort cleanup */
        }
        catch (UnauthorizedAccessException)
        {
            /* best-effort cleanup */
        }
    }

    [Fact]
    public async Task UnknownProposal_ReturnsNull()
    {
        var service = this.CreateDraftPrService();

        var result = await service.CreateDraftPullRequestAsync(Guid.NewGuid(), Guid.NewGuid(), new CreateDraftPullRequestRequest(), CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(0, this.gateway.CreateCalls);
    }

    [Fact]
    public async Task PendingProposal_ThrowsNotApproved()
    {
        var proposals = this.CreateProposalService();
        var clientId = Guid.NewGuid();
        var created = await proposals.CreateAsync(clientId, PatchRequest(), CancellationToken.None);

        var service = this.CreateDraftPrService();

        var ex = await Assert.ThrowsAsync<DraftPullRequestNotApprovedException>(() =>
            service.CreateDraftPullRequestAsync(clientId, created.Id, new CreateDraftPullRequestRequest(), CancellationToken.None));
        Assert.Equal(created.Id, ex.ProposalId);
        Assert.Equal(FixProposalStatus.PendingApproval, ex.Status);
        Assert.Equal(0, this.gateway.CreateCalls);
    }

    [Fact]
    public async Task MissingPatchInputs_ThrowsArgumentExceptionNamingEveryMissingField()
    {
        var proposals = this.CreateProposalService();
        var clientId = Guid.NewGuid();
        var created = await proposals.CreateAsync(
            clientId,
            new CreateFixProposalRequest("finding-1", "review", "Fix it", FilePath: "src/Foo.cs", Line: 7),
            CancellationToken.None);
        await proposals.ApproveAsync(clientId, created.Id, null, CancellationToken.None);

        var service = this.CreateDraftPrService();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateDraftPullRequestAsync(clientId, created.Id, new CreateDraftPullRequestRequest(), CancellationToken.None));
        Assert.Contains("repositoryOwner", ex.Message, StringComparison.Ordinal);
        Assert.Contains("repositoryName", ex.Message, StringComparison.Ordinal);
        Assert.Contains("originalCode", ex.Message, StringComparison.Ordinal);
        Assert.Contains("suggestedCode", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, this.gateway.CreateCalls);
    }

    [Fact]
    public async Task FileNotFoundAtBase_RefusesWithoutCallingCreate()
    {
        var (clientId, proposalId) = await this.ApprovedProposalAsync();
        this.gateway.FileContent = null;

        var service = this.CreateDraftPrService();

        var ex = await Assert.ThrowsAsync<DraftPullRequestRefusedException>(() =>
            service.CreateDraftPullRequestAsync(clientId, proposalId, new CreateDraftPullRequestRequest(), CancellationToken.None));
        Assert.Contains("was not found", ex.Reason, StringComparison.Ordinal);
        Assert.Equal(0, this.gateway.CreateCalls);
    }

    [Fact]
    public async Task HeadMismatch_RefusesAndDoesNotCallGateway()
    {
        var (clientId, proposalId) = await this.ApprovedProposalAsync();
        this.gateway.FileContent = new DraftPullRequestFileContent(HeadContent.Replace("var x = 1;", "var x = 99;", StringComparison.Ordinal), "blob-sha");

        var service = this.CreateDraftPrService();

        var ex = await Assert.ThrowsAsync<DraftPullRequestRefusedException>(() =>
            service.CreateDraftPullRequestAsync(clientId, proposalId, new CreateDraftPullRequestRequest(), CancellationToken.None));
        Assert.Contains("has changed since the proposal was made", ex.Reason, StringComparison.Ordinal);
        Assert.Equal(0, this.gateway.CreateCalls);
    }

    [Fact]
    public async Task Success_CreatesDraftPrWithExactPatchedContentAndStoresUrl()
    {
        var (clientId, proposalId) = await this.ApprovedProposalAsync();
        this.gateway.FileContent = new DraftPullRequestFileContent(HeadContent, "blob-sha");

        var service = this.CreateDraftPrService();

        var result = await service.CreateDraftPullRequestAsync(clientId, proposalId, new CreateDraftPullRequestRequest(), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("https://github.com/octo/demo/pull/42", result.PullRequestUrl);
        Assert.Equal("main", result.BaseBranch);
        Assert.True(result.UsedDefaultBranch);
        Assert.Contains("default branch", result.Note, StringComparison.Ordinal);
        Assert.Equal(1, this.gateway.CreateCalls);
        Assert.Equal("aegiscode/fix-", this.gateway.LastCreateInput!.BranchName[..13]);
        Assert.Equal(PatchedContent, this.gateway.LastCreateInput.NewContent);
        Assert.Equal("blob-sha", this.gateway.LastCreateInput.BlobSha);
        Assert.Equal("src/Foo.cs", this.gateway.LastCreateInput.FilePath);
        Assert.Equal("main", this.gateway.LastCreateInput.BaseBranch);
        Assert.Null(this.gateway.LastCreateInput.BaseSha);

        // The stored proposal now carries the draft PR, so the queue can link to it.
        var stored = await this.CreateProposalService().ListAsync(clientId, CancellationToken.None);
        var updated = Assert.Single(stored, p => p.Id == proposalId);
        Assert.Equal("https://github.com/octo/demo/pull/42", updated.DraftPrUrl);
        Assert.Equal(this.gateway.LastCreateInput.BranchName, updated.DraftPrBranch);
        Assert.Equal("main", updated.DraftPrBaseBranch);
        Assert.NotNull(updated.DraftPrCreatedAt);
    }

    [Fact]
    public async Task SecondCall_ReturnsStoredUrlWithoutOpeningAnotherPr()
    {
        var (clientId, proposalId) = await this.ApprovedProposalAsync();
        this.gateway.FileContent = new DraftPullRequestFileContent(HeadContent, "blob-sha");

        var service = this.CreateDraftPrService();

        var first = await service.CreateDraftPullRequestAsync(clientId, proposalId, new CreateDraftPullRequestRequest(), CancellationToken.None);
        var second = await service.CreateDraftPullRequestAsync(clientId, proposalId, new CreateDraftPullRequestRequest(), CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.PullRequestUrl, second.PullRequestUrl);
        Assert.Equal(first.Branch, second.Branch);
        Assert.Contains("already created", second.Note, StringComparison.Ordinal);
        Assert.Equal(1, this.gateway.CreateCalls);
    }

    [Fact]
    public async Task RequestOverrides_UseRequestValuesOverStoredOnes()
    {
        var (clientId, proposalId) = await this.ApprovedProposalAsync();
        this.gateway.FileContent = new DraftPullRequestFileContent(HeadContent, "blob-sha");

        var service = this.CreateDraftPrService();

        var result = await service.CreateDraftPullRequestAsync(
            clientId,
            proposalId,
            new CreateDraftPullRequestRequest(RepositoryName: "other-repo", BaseRef: "develop", BaseSha: "abc1234"),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result.UsedDefaultBranch);
        Assert.Equal("develop", result.BaseBranch);
        Assert.Equal("other-repo", this.gateway.LastCreateInput!.Repository);
        Assert.Equal("develop", this.gateway.LastCreateInput.BaseBranch);
        Assert.Equal("abc1234", this.gateway.LastCreateInput.BaseSha);
        Assert.Equal("abc1234", this.gateway.LastFileRef);
    }

    [Fact]
    public async Task UpstreamFailure_ThrowsUpstreamExceptionWithProviderMessage()
    {
        var (clientId, proposalId) = await this.ApprovedProposalAsync();
        this.gateway.FileContent = new DraftPullRequestFileContent(HeadContent, "blob-sha");
        this.gateway.CreateFailure = new InvalidOperationException("GitHub branch creation failed with status 422: Reference already exists.");

        var service = this.CreateDraftPrService();

        var ex = await Assert.ThrowsAsync<DraftPullRequestUpstreamException>(() =>
            service.CreateDraftPullRequestAsync(clientId, proposalId, new CreateDraftPullRequestRequest(), CancellationToken.None));
        Assert.Contains("Reference already exists", ex.Message, StringComparison.Ordinal);

        // Nothing was recorded as created: the proposal still has no draft PR.
        var stored = await this.CreateProposalService().ListAsync(clientId, CancellationToken.None);
        Assert.Null(Assert.Single(stored, p => p.Id == proposalId).DraftPrUrl);
    }

    [Fact]
    public void ApplyReplacement_PreservesCrlfLineEndings()
    {
        const string crlfHead = "line one\r\nline two\r\nline three\r\n";
        var patched = DraftPullRequestService.ApplyReplacement(crlfHead, 2, 2, "line TWO");

        Assert.Equal("line one\r\nline TWO\r\nline three\r\n", patched);
    }

    [Fact]
    public void ApplyReplacement_PreservesMissingTrailingNewline()
    {
        var patched = DraftPullRequestService.ApplyReplacement("a\nb\nc", 2, 2, "B");

        Assert.Equal("a\nB\nc", patched);
    }

    [Fact]
    public void ApplyReplacement_KeepsTrailingNewlineWhenHeadHasOne()
    {
        var patched = DraftPullRequestService.ApplyReplacement("a\nb\nc\n", 3, 3, "C");

        Assert.Equal("a\nb\nC\n", patched);
    }

    private FixProposalService CreateProposalService()
    {
        var store = new JsonFileFixProposalStore(
            Options.Create(new SecurityScanningOptions { FixProposalsRoot = this.tempRoot }),
            NullLogger<JsonFileFixProposalStore>.Instance);
        return new FixProposalService(store, NullLogger<FixProposalService>.Instance);
    }

    private DraftPullRequestService CreateDraftPrService()
    {
        var store = new JsonFileFixProposalStore(
            Options.Create(new SecurityScanningOptions { FixProposalsRoot = this.tempRoot }),
            NullLogger<JsonFileFixProposalStore>.Instance);
        return new DraftPullRequestService(store, this.gateway, NullLogger<DraftPullRequestService>.Instance);
    }

    private async Task<(Guid ClientId, Guid ProposalId)> ApprovedProposalAsync()
    {
        var proposals = this.CreateProposalService();
        var clientId = Guid.NewGuid();
        var created = await proposals.CreateAsync(clientId, PatchRequest(), CancellationToken.None);
        await proposals.ApproveAsync(clientId, created.Id, null, CancellationToken.None);
        return (clientId, created.Id);
    }

    private static CreateFixProposalRequest PatchRequest() => new(
        "finding-1", "review", "Fix the off-by-one",
        FilePath: "src/Foo.cs", Line: 7,
        Remediation: "Change x to 2.",
        Confidence: "high",
        RepositoryOwner: "octo",
        RepositoryName: "demo",
        StartLine: 7,
        EndLine: 7,
        OriginalCode: "        var x = 1;",
        SuggestedCode: "        var x = 2;");

    private sealed class FakeDraftPullRequestGateway : IDraftPullRequestGateway
    {
        public ScmProvider Provider => ScmProvider.GitHub;

        public string DefaultBranch { get; set; } = "main";

        public DraftPullRequestFileContent? FileContent { get; set; }

        public string? LastFileRef { get; private set; }

        public DraftPullRequestInput? LastCreateInput { get; private set; }

        public int CreateCalls { get; private set; }

        public Exception? CreateFailure { get; set; }

        public DraftPullRequestResult CreateResult { get; set; } =
            new("aegiscode/fix-test-1", "https://github.com/octo/demo/pull/42", "main");

        public Task<string?> GetDefaultBranchAsync(Guid clientId, ProviderHostRef host, string owner, string repository, CancellationToken ct) =>
            Task.FromResult<string?>(this.DefaultBranch);

        public Task<DraftPullRequestFileContent?> GetFileContentAsync(
            Guid clientId, ProviderHostRef host, string owner, string repository, string filePath, string @ref, CancellationToken ct)
        {
            this.LastFileRef = @ref;
            return Task.FromResult(this.FileContent);
        }

        public Task<DraftPullRequestResult> CreateDraftPullRequestAsync(Guid clientId, ProviderHostRef host, DraftPullRequestInput input, CancellationToken ct)
        {
            this.CreateCalls++;
            this.LastCreateInput = input;
            if (this.CreateFailure is not null)
            {
                throw this.CreateFailure;
            }

            return Task.FromResult(this.CreateResult);
        }
    }
}
