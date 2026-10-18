// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.CodeInsights;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.CodeInsights;

public sealed class CodeInsightDiscussionBatchFactoryTests
{
    [Fact]
    public async Task LogicalModelProfileChangesInvalidateDependenciesWithoutResolvingAChatRuntime()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var client = Guid.NewGuid();
        var connection = Guid.NewGuid();
        var model = Guid.NewGuid();
        await using var db = new MeisterProPRDbContext(options);
        db.Clients.Add(new ClientRecord { Id = client, TenantId = TenantCatalog.SystemTenantId, DisplayName = "Test", IsActive = true });
        db.ClientPurposeLogicalModels.Add(
            new() { Id = Guid.NewGuid(), ClientId = client, Purpose = AiPurpose.InsightsClassification, LogicalModelName = "judge" });
        db.LogicalModelOverrides.Add(new() { Id = Guid.NewGuid(), ClientId = client, Name = "judge", ConnectionId = connection, ConfiguredModelId = model });
        var profile = new AiConnectionProfileRecord
        {
            Id = connection, ClientId = client, IsActive = true,
            ProviderKind = "openai-compatible", BaseUrl = "https://first.example.test", ProtectedSecret = "protected-one"
        };
        db.AiConnectionProfiles.Add(profile);
        db.AiConfiguredModels.Add(new() { Id = model, ConnectionProfileId = connection, RemoteModelId = "model-one" });
        await db.SaveChangesAsync();
        var gate = Substitute.For<ICodeInsightsCollectionGate>();
        gate.IsCollectionEnabledAsync(client, Arg.Any<CancellationToken>()).Returns(true);
        var findings = Substitute.For<ICodeInsightFindingStore>();
        findings.GetFindingsForPullRequestAsync(Arg.Any<CodeInsightPullRequestKey>(), Arg.Any<CancellationToken>()).Returns([]);
        var factory = new CodeInsightDiscussionBatchFactory(gate, findings, new Factory(options));
        var key = new CodeInsightPullRequestKey(client, "repo", 42);
        var initial = (await factory.CreateAsync(key, "scope"))!;
        profile.UpdatedAt = DateTimeOffset.UtcNow;
        profile.ProtectedSecret = "protected-rotated";
        await db.SaveChangesAsync();
        Assert.Equal(initial.DependencyVersion, (await factory.CreateAsync(key, "scope"))!.DependencyVersion);
        profile.BaseUrl = "https://second.example.test";
        await db.SaveChangesAsync();
        var moved = (await factory.CreateAsync(key, "scope"))!;
        Assert.NotEqual(initial.DependencyVersion, moved.DependencyVersion);
        profile.ProviderSettings = new() { ["api-version"] = "next" };
        await db.SaveChangesAsync();
        Assert.NotEqual(moved.DependencyVersion, (await factory.CreateAsync(key, "scope"))!.DependencyVersion);
    }

    [Fact]
    public async Task BindingOnlyCapabilitiesAndAuthenticationInvalidateDependenciesButSecretsDoNot()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new MeisterProPRDbContext(options);
        var client = Guid.NewGuid();
        var profile = new AiConnectionProfileRecord
        {
            Id = Guid.NewGuid(), ClientId = client, IsActive = true, ProviderKind = "openai-compatible", BaseUrl = "https://ai.example.test",
            AuthMode = "api-key",
        };
        var model = new AiConfiguredModelRecord { Id = Guid.NewGuid(), ConnectionProfileId = profile.Id, RemoteModelId = "judge" };
        db.Clients.Add(new ClientRecord { Id = client, TenantId = TenantCatalog.SystemTenantId, DisplayName = "Test", IsActive = true });
        db.AiConnectionProfiles.Add(profile);
        db.AiConfiguredModels.Add(model);
        db.AiPurposeBindings.Add(
            new AiPurposeBindingRecord
            {
                Id = Guid.NewGuid(), ConnectionProfileId = profile.Id, ConfiguredModelId = model.Id,
                Purpose = AiPurpose.InsightsClassification.ToString(), ProtocolMode = "chat", IsEnabled = true,
            });
        await db.SaveChangesAsync();
        var reader = new MeisterDev.ProPR.Infrastructure.Features.Crawling.Persistence.DiscussionAiConfigurationVersionReader(new Factory(options));
        var previous = await reader.ReadAsync(client, [AiPurpose.InsightsClassification]);
        Action[] changes =
        [
            () => model.SupportsToolUse = true,
            () => model.OperationKinds = ["chat"],
            () => model.TokenizerName = "o200k_base",
            () => model.EmbeddingDimensions = 512,
            () => profile.AuthMode = "managed-identity",
            () => profile.DefaultHeaders = new() { ["x-model-route"] = "next" },
            () => profile.DefaultQueryParams = new() { ["api-version"] = "next" },
        ];
        foreach (var change in changes)
        {
            change();
            await db.SaveChangesAsync();
            var current = await reader.ReadAsync(client, [AiPurpose.InsightsClassification]);
            Assert.NotEqual(previous, current);
            previous = current;
        }

        profile.ProtectedSecret = "rotated-protected-material";
        profile.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        Assert.Equal(previous, await reader.ReadAsync(client, [AiPurpose.InsightsClassification]));
    }

    private sealed class Factory(DbContextOptions<MeisterProPRDbContext> options) : IDbContextFactory<MeisterProPRDbContext>
    {
        public MeisterProPRDbContext CreateDbContext() => new(options);
    }
}
