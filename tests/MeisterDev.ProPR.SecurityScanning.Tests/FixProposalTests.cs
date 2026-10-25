// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.SecurityScanning.Models;
using MeisterDev.ProPR.SecurityScanning.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MeisterDev.ProPR.SecurityScanning.Tests;

/// <summary>
///     Coverage for the fix-proposals human-approval gate: creation validation, the
///     approve/reject transitions, 409 on re-decision, client scoping, and survival
///     across service instances (the JSON files on disk, not memory).
///     Tests run against a fresh temp directory per test — no database involved.
///     NOTE: run on the laptop with <c>dotnet test</c>; testhost is broken in the VM.
/// </summary>
public sealed class FixProposalTests : IDisposable
{
    private readonly string tempRoot = Path.Combine(
        Path.GetTempPath(), "aegis-fix-proposal-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(this.tempRoot))
            {
                Directory.Delete(this.tempRoot, recursive: true);
            }
        }
        catch (IOException) { /* best-effort cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
    }

    private FixProposalService CreateService()
    {
        var store = new JsonFileFixProposalStore(
            Options.Create(new SecurityScanningOptions { FixProposalsRoot = this.tempRoot }),
            NullLogger<JsonFileFixProposalStore>.Instance);
        return new FixProposalService(store, NullLogger<FixProposalService>.Instance);
    }

    private static CreateFixProposalRequest ValidRequest(
        string findingRef = "finding-1",
        string source = "scan",
        string? remediation = "Upgrade the package.") =>
        new(findingRef, source, "Upgrade lodash", FilePath: "package.json", Line: 12,
            Remediation: remediation, Confidence: "high");

    [Fact]
    public async Task Create_ValidRequest_StartsPendingApprovalWithDecidedFieldsNull()
    {
        var service = this.CreateService();
        var clientId = Guid.NewGuid();

        var created = await service.CreateAsync(clientId, ValidRequest(), CancellationToken.None);

        Assert.Equal("PendingApproval", created.Status);
        Assert.Null(created.DecidedAt);
        Assert.Null(created.DecisionReason);
        Assert.Equal("scan", created.Source);
        Assert.Equal("finding-1", created.FindingRef);
        Assert.Equal("Upgrade the package.", created.Remediation);
        Assert.Equal("package.json", created.FilePath);
        Assert.Equal(12, created.Line);
    }

    [Fact]
    public async Task Create_NullRemediation_IsStoredAsGivenNotFabricated()
    {
        var service = this.CreateService();
        var clientId = Guid.NewGuid();

        var created = await service.CreateAsync(
            clientId, ValidRequest(remediation: null), CancellationToken.None);

        Assert.Null(created.Remediation);
        var listed = await service.ListAsync(clientId, CancellationToken.None);
        Assert.Single(listed);
        Assert.Null(listed[0].Remediation);
    }

    [Theory]
    [InlineData("", "scan", "title")]
    [InlineData("   ", "scan", "title")]
    [InlineData("finding-1", "scan", "")]
    [InlineData("finding-1", "scan", "   ")]
    [InlineData("finding-1", "", "title")]
    [InlineData("finding-1", "bogus", "title")]
    public async Task Create_InvalidInput_ThrowsArgumentException(string findingRef, string source, string title)
    {
        var service = this.CreateService();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateAsync(Guid.NewGuid(), new CreateFixProposalRequest(findingRef, source, title),
                CancellationToken.None));
    }

    [Fact]
    public async Task Create_SourceIsCaseInsensitive_AndNormalizedToLowercase()
    {
        var service = this.CreateService();
        var created = await service.CreateAsync(
            Guid.NewGuid(), ValidRequest(source: "Review"), CancellationToken.None);
        Assert.Equal("review", created.Source);
    }

    [Fact]
    public async Task List_NewestFirst_AndScopedToClient()
    {
        var service = this.CreateService();
        var clientId = Guid.NewGuid();
        var otherClientId = Guid.NewGuid();
        var ct = CancellationToken.None;

        var first = await service.CreateAsync(clientId, ValidRequest("finding-1"), ct);
        await Task.Delay(TimeSpan.FromMilliseconds(5), ct);
        var second = await service.CreateAsync(clientId, ValidRequest("finding-2"), ct);
        await service.CreateAsync(otherClientId, ValidRequest("finding-3"), ct);

        var items = await service.ListAsync(clientId, ct);

        Assert.Equal([second.Id, first.Id], items.Select(p => p.Id));
        Assert.DoesNotContain(items, p => p.FindingRef == "finding-3");
        Assert.Empty(await service.ListAsync(Guid.NewGuid(), ct));
    }

    [Fact]
    public async Task Approve_SetsApprovedDecidedAtAndReason()
    {
        var service = this.CreateService();
        var clientId = Guid.NewGuid();
        var ct = CancellationToken.None;

        var created = await service.CreateAsync(clientId, ValidRequest(), ct);
        var approved = await service.ApproveAsync(clientId, created.Id, "Looks right.", ct);

        Assert.NotNull(approved);
        Assert.Equal("Approved", approved.Status);
        Assert.NotNull(approved.DecidedAt);
        Assert.Equal("Looks right.", approved.DecisionReason);
    }

    [Fact]
    public async Task Reject_SetsRejectedAndStoresReason()
    {
        var service = this.CreateService();
        var clientId = Guid.NewGuid();
        var ct = CancellationToken.None;

        var created = await service.CreateAsync(clientId, ValidRequest(), ct);
        var rejected = await service.RejectAsync(clientId, created.Id, "Too risky to auto-fix.", ct);

        Assert.NotNull(rejected);
        Assert.Equal("Rejected", rejected.Status);
        Assert.NotNull(rejected.DecidedAt);
        Assert.Equal("Too risky to auto-fix.", rejected.DecisionReason);
    }

    [Fact]
    public async Task Decide_UnknownProposal_ReturnsNull()
    {
        var service = this.CreateService();
        var ct = CancellationToken.None;

        Assert.Null(await service.ApproveAsync(Guid.NewGuid(), Guid.NewGuid(), null, ct));
        Assert.Null(await service.RejectAsync(Guid.NewGuid(), Guid.NewGuid(), null, ct));
    }

    [Fact]
    public async Task Decide_ProposalOfAnotherClient_ReturnsNull()
    {
        var service = this.CreateService();
        var clientId = Guid.NewGuid();
        var ct = CancellationToken.None;

        var created = await service.CreateAsync(clientId, ValidRequest(), ct);

        Assert.Null(await service.ApproveAsync(Guid.NewGuid(), created.Id, null, ct));
        Assert.Null(await service.RejectAsync(Guid.NewGuid(), created.Id, null, ct));

        // Still pending for its own client — the cross-client attempt changed nothing.
        var own = await service.ListAsync(clientId, ct);
        Assert.Single(own);
        Assert.Equal("PendingApproval", own[0].Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Decide_AlreadyDecided_ThrowsAlreadyDecided(bool approveFirst)
    {
        var service = this.CreateService();
        var clientId = Guid.NewGuid();
        var ct = CancellationToken.None;

        var created = await service.CreateAsync(clientId, ValidRequest(), ct);
        if (approveFirst)
        {
            await service.ApproveAsync(clientId, created.Id, null, ct);
        }
        else
        {
            await service.RejectAsync(clientId, created.Id, null, ct);
        }

        // Neither a second approve nor a reject may silently re-decide.
        await Assert.ThrowsAsync<FixProposalAlreadyDecidedException>(() =>
            service.ApproveAsync(clientId, created.Id, null, ct));
        await Assert.ThrowsAsync<FixProposalAlreadyDecidedException>(() =>
            service.RejectAsync(clientId, created.Id, null, ct));
    }

    [Fact]
    public async Task Proposals_SurviveNewServiceInstance_SameRoot()
    {
        var clientId = Guid.NewGuid();
        var ct = CancellationToken.None;

        var first = this.CreateService();
        var created = await first.CreateAsync(clientId, ValidRequest(), ct);
        await first.ApproveAsync(clientId, created.Id, "ok", ct);

        // A fresh service + store over the same directory (simulating an API restart).
        var second = this.CreateService();
        var items = await second.ListAsync(clientId, ct);

        Assert.Single(items);
        Assert.Equal(created.Id, items[0].Id);
        Assert.Equal("Approved", items[0].Status);
        Assert.Equal("ok", items[0].DecisionReason);
    }

    [Fact]
    public async Task List_CorruptFile_ReturnsEmptyInsteadOfThrowing()
    {
        var clientId = Guid.NewGuid();
        Directory.CreateDirectory(this.tempRoot);
        await File.WriteAllTextAsync(
            Path.Combine(this.tempRoot, $"{clientId:N}.fix-proposals.json"),
            "{ this is not json",
            CancellationToken.None);

        var items = await this.CreateService().ListAsync(clientId, CancellationToken.None);
        Assert.Empty(items);
    }
}
