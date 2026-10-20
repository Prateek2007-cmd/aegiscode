// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Data.Common;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Features.Clients.Contracts;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Repositories;
using MeisterDev.ProPR.Infrastructure.Tests.Fixtures;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Repositories;

[Collection("PostgresIntegration2")]
public sealed class AiConfigurationPostgresTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _clientId = Guid.NewGuid();
    private Guid _firstId;
    private Guid _secondId;

    private MeisterProPRDbContext Context(params IInterceptor[] interceptors) => new(
        new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, options => options.UseVector()).AddInterceptors(interceptors).Options);

    public async Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();
        await using var db = this.Context();
        db.Tenants.Add(
            new TenantRecord
            {
                Id = this._tenantId, Slug = "ai-test-" + this._tenantId.ToString("N"),
                DisplayName = "AI lifecycle test", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
            });
        db.Clients.Add(
            new ClientRecord
            {
                Id = this._clientId, TenantId = this._tenantId, DisplayName = "AI lifecycle test",
                CreatedAt = DateTimeOffset.UtcNow
            });
        var first = AiConnectionRepositoryTests.MakeProfile(this._clientId, true, true, "First");
        var second = AiConnectionRepositoryTests.MakeProfile(this._clientId, false, true, "Second");
        first.AuthMode = "ApiKey";
        second.AuthMode = "ApiKey";
        this._firstId = first.Id;
        this._secondId = second.Id;
        db.AiConnectionProfiles.AddRange(first, second);
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        if (!fixture.IsAvailable)
        {
            return;
        }

        await using var db = this.Context();
        await db.Clients.Where(c => c.Id == this._clientId).ExecuteDeleteAsync();
        await db.Tenants.Where(t => t.Id == this._tenantId).ExecuteDeleteAsync();
    }

    private static AiWorkspacePurposeSelection Selection(AiConnectionDto profile)
    {
        var chat = profile.ConfiguredModels.Single(m => m.SupportsChat);
        var embedding = profile.ConfiguredModels.Single(m => m.SupportsEmbedding);
        return new(new(profile.Id, chat.Id), new(profile.Id, chat.Id), new(profile.Id, embedding.Id));
    }

    private static LogicalModelCatalogRepository Catalog(MeisterProPRDbContext db)
    {
        var scope = Substitute.For<IAiConnectionScopeGuard>();
        scope.ValidateAsync(Arg.Any<AiConnectionDto>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        return new LogicalModelCatalogRepository(
            db, Substitute.For<ILogicalModelCapabilityValidator>(), AiConnectionRepositoryTests.CreateRepository(db),
            scope, DeclaringProviderFamilies.Superseding("meisterdev/azureOpenAi", "AzureOpenAi"));
    }

    [Fact]
    public async Task WorkspaceSelection_AfterStartupBackfillCanReplaceModelsAcrossRepeatedRestarts()
    {
        await using (var selected = this.Context())
        {
            await selected.AiPurposeBindings.Where(binding =>
                    (binding.ConnectionProfileId == this._firstId || binding.ConnectionProfileId == this._secondId) &&
                    binding.Purpose == nameof(AiPurpose.ProRVPrefilter))
                .ExecuteDeleteAsync();
            var repository = AiConnectionRepositoryTests.CreateRepository(selected);
            Assert.True((await repository.SelectPurposesAsync(this._clientId, Selection((await repository.GetByIdAsync(this._firstId))!))).Applied);
        }

        for (var restart = 0; restart < 2; restart++)
        {
            await using (var startup = this.Context())
            {
                var backfill = new LogicalModelMigrationBackfill(startup, AiConnectionRepositoryTests.CreateRepository(startup), Catalog(startup));
                await backfill.BackfillClientPurposesAsync(this._clientId, default);
            }

            await using var edited = this.Context();
            var repository = AiConnectionRepositoryTests.CreateRepository(edited);
            var next = restart == 0 ? this._secondId : this._firstId;
            var result = await repository.SelectPurposesAsync(this._clientId, Selection((await repository.GetByIdAsync(next))!));
            Assert.True(result.Applied, result.Error);
            Assert.Empty(await Catalog(edited).GetPurposeRolesAsync(this._clientId, default));
            foreach (var purpose in Enum.GetValues<AiPurpose>())
            {
                Assert.Equal(next, (await repository.GetActiveBindingForPurposeAsync(this._clientId, purpose))!.Connection.Id);
            }
        }
    }

    [Fact]
    public async Task Selection_SaveFailureRollsBackAllBindingsAndActivation()
    {
        await using var failing = this.Context(new FailAfterSave());
        var repository = AiConnectionRepositoryTests.CreateRepository(failing);
        var before = (await repository.GetByIdAsync(this._firstId))!;
        var selected = (await repository.GetByIdAsync(this._secondId))!;
        await Assert.ThrowsAsync<InjectedSaveException>(() => repository.SelectPurposesAsync(this._clientId, Selection(selected)));
        await using var read = this.Context();
        var saved = AiConnectionRepositoryTests.CreateRepository(read);
        var first = (await saved.GetByIdAsync(this._firstId))!;
        var second = (await saved.GetByIdAsync(this._secondId))!;
        Assert.Equal(before.UpdatedAt, first.UpdatedAt);
        Assert.Equal(before.PurposeBindings.Select(b => (b.Id, b.ConfiguredModelId)), first.PurposeBindings.Select(b => (b.Id, b.ConfiguredModelId)));
        Assert.False(second.IsActive);
        var client = await read.Clients.AsNoTracking().SingleAsync(c => c.Id == this._clientId);
        Assert.False(client.WorkspaceAiSelectionInitialized);
        Assert.Equal(0, client.AiRoutingRevision);
    }

    [Fact]
    public async Task Selection_WithExplicitOperatorRolePreservesRoleAndInitializationOnRefusal()
    {
        await using var db = this.Context();
        await Catalog(db).SetPurposeRoleAsync(this._clientId, AiPurpose.ReviewDefault, "operator-role", default);
        var revision = await db.Clients.Where(c => c.Id == this._clientId).Select(c => c.AiRoutingRevision).SingleAsync();
        var repository = AiConnectionRepositoryTests.CreateRepository(db);
        var result = await repository.SelectPurposesAsync(this._clientId, Selection((await repository.GetByIdAsync(this._secondId))!));
        Assert.True(result.Conflict);
        Assert.Equal("operator-role", await Catalog(db).GetPurposeRoleAsync(this._clientId, AiPurpose.ReviewDefault, default));
        var client = await db.Clients.AsNoTracking().SingleAsync(c => c.Id == this._clientId);
        Assert.False(client.WorkspaceAiSelectionInitialized);
        Assert.Equal(revision, client.AiRoutingRevision);
    }

    [Fact]
    public async Task InitializedWorkspace_SkipsPurposeBackfillButStillMigratesReviewPasses()
    {
        await using var db = this.Context();
        var repository = AiConnectionRepositoryTests.CreateRepository(db);
        var selected = (await repository.GetByIdAsync(this._firstId))!;
        Assert.True((await repository.SelectPurposesAsync(this._clientId, Selection(selected))).Applied);
        db.ClientReviewPasses.Add(
            new ClientReviewPassRecord
            {
                Id = Guid.NewGuid(), ClientId = this._clientId, Ordinal = 1,
                ConfiguredModelId = selected.ConfiguredModels.Single(model => model.SupportsChat).Id,
            });
        await db.SaveChangesAsync();
        var backfill = new LogicalModelMigrationBackfill(db, repository, Catalog(db));
        Assert.Equal(1, await backfill.BackfillClientReviewPassesAsync(this._clientId, default));
        Assert.Equal(0, await backfill.BackfillClientPurposesAsync(this._clientId, default));
        Assert.NotNull((await db.ClientReviewPasses.SingleAsync(pass => pass.ClientId == this._clientId)).LogicalModelName);
        Assert.Empty(await Catalog(db).GetPurposeRolesAsync(this._clientId, default));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SelectionAndStartupBackfill_RespectTheFirstRoutingWriter(bool selectionFirst)
    {
        var pause = new RoutingWritePause();
        var attempt = new RoutingWriteAttempt();
        await using var firstDb = this.Context(pause);
        await using var secondDb = this.Context(attempt);
        var firstRepository = AiConnectionRepositoryTests.CreateRepository(firstDb);
        var secondRepository = AiConnectionRepositoryTests.CreateRepository(secondDb);
        var next = Selection((await firstRepository.GetByIdAsync(this._secondId))!);
        var firstBackfill = new LogicalModelMigrationBackfill(firstDb, firstRepository, Catalog(firstDb));
        var secondBackfill = new LogicalModelMigrationBackfill(secondDb, secondRepository, Catalog(secondDb));
        Task<AiConfigurationResult> selection;
        Task<int> startup;
        if (selectionFirst)
        {
            selection = firstRepository.SelectPurposesAsync(this._clientId, next);
            await pause.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            startup = secondBackfill.BackfillClientPurposesAsync(this._clientId, default);
        }
        else
        {
            startup = firstBackfill.BackfillClientPurposesAsync(this._clientId, default);
            await pause.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            selection = secondRepository.SelectPurposesAsync(this._clientId, next);
        }

        await attempt.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        pause.Release.TrySetResult();
        await Task.WhenAll(selection, startup).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(selectionFirst, (await selection).Applied);
        Assert.Equal(!selectionFirst, (await selection).Conflict);
        Assert.Equal(selectionFirst ? 0 : Enum.GetValues<AiPurpose>().Length, await startup);
        await using var read = this.Context();
        Assert.Equal(selectionFirst, (await read.Clients.AsNoTracking().SingleAsync(c => c.Id == this._clientId)).WorkspaceAiSelectionInitialized);
        Assert.Equal(selectionFirst ? 0 : Enum.GetValues<AiPurpose>().Length, (await Catalog(read).GetPurposeRolesAsync(this._clientId, default)).Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExplicitPurposeChangeAndSelection_PreserveTheOperatorRoute(bool operatorFirst)
    {
        var pause = new RoutingWritePause();
        var attempt = new RoutingWriteAttempt();
        await using var firstDb = this.Context(pause);
        await using var secondDb = this.Context(attempt);
        var firstRepository = AiConnectionRepositoryTests.CreateRepository(firstDb);
        var secondRepository = AiConnectionRepositoryTests.CreateRepository(secondDb);
        var next = Selection((await firstRepository.GetByIdAsync(this._secondId))!);
        Task<AiConfigurationResult> selection;
        Task update;
        if (operatorFirst)
        {
            update = Catalog(firstDb).SetPurposeRoleAsync(this._clientId, AiPurpose.ReviewDefault, "operator-role", default);
            await pause.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            selection = secondRepository.SelectPurposesAsync(this._clientId, next);
        }
        else
        {
            selection = firstRepository.SelectPurposesAsync(this._clientId, next);
            await pause.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            update = Catalog(secondDb).SetPurposeRoleAsync(this._clientId, AiPurpose.ReviewDefault, "operator-role", default);
        }

        await attempt.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        pause.Release.TrySetResult();
        await Task.WhenAll(selection, update).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(!operatorFirst, (await selection).Applied);
        Assert.Equal(operatorFirst, (await selection).Conflict);
        await using var read = this.Context();
        Assert.Equal("operator-role", await Catalog(read).GetPurposeRoleAsync(this._clientId, AiPurpose.ReviewDefault, default));
    }

    [Fact]
    public async Task VerifiedUpdate_AuthMutationWithoutTimestampRejectsCandidate()
    {
        await using var db = this.Context();
        var repository = AiConnectionRepositoryTests.CreateRepository(db);
        var original = (await repository.GetByIdAsync(this._firstId))!;
        var request = new AiConnectionWriteRequestDto(
            "Stale candidate", original.ProviderKind, original.BaseUrl,
            original.AuthMode, original.DiscoveryMode, original.ConfiguredModels, original.PurposeBindings);
        var result = await repository.VerifyUpdateAsync(
            this._clientId, original, request, async (_, _) =>
            {
                await using var writer = this.Context();
                await writer.AiConnectionProfiles.Where(p => p.Id == original.Id)
                    .ExecuteUpdateAsync(update => update.SetProperty(p => p.AuthMode, "AzureIdentity"));
                return original.Verification;
            });
        Assert.False(result.Applied);
        Assert.True(result.Conflict);
        Assert.Equal(original.DisplayName, (await repository.GetByIdAsync(original.Id))!.DisplayName);
    }

    [Fact]
    public async Task VerifiedUpdateAndRepeatedSelection_PreserveConfiguredModelIdentifiers()
    {
        await using var db = this.Context();
        var repository = AiConnectionRepositoryTests.CreateRepository(db);
        var original = (await repository.GetByIdAsync(this._firstId))!;
        var request = new AiConnectionWriteRequestDto(
            "Verified replacement", original.ProviderKind, original.BaseUrl,
            original.AuthMode, original.DiscoveryMode, original.ConfiguredModels, original.PurposeBindings);
        var result = await repository.VerifyUpdateAsync(
            this._clientId, original, request,
            (_, _) => Task.FromResult(original.Verification));
        Assert.True(result.Applied);
        Assert.Equal(original.ConfiguredModels.Select(m => m.Id), result.Connection!.ConfiguredModels.Select(m => m.Id));
        var selected = Selection(result.Connection);
        Assert.True((await repository.SelectPurposesAsync(this._clientId, selected)).Applied);
        Assert.True((await repository.SelectPurposesAsync(this._clientId, selected)).Applied);
        await using var read = this.Context();
        var saved = (await AiConnectionRepositoryTests.CreateRepository(read).GetByIdAsync(original.Id))!;
        Assert.Equal(original.ConfiguredModels.Select(m => m.Id), saved.ConfiguredModels.Select(m => m.Id));
        Assert.True(saved.IsActive);
    }

    [Fact]
    public async Task ConcurrentSelections_OneCompleteSelectionWins()
    {
        var barrier = new RoutingSnapshotBarrier();
        await using var firstDb = this.Context(barrier);
        await using var secondDb = this.Context(barrier);
        var firstRepository = AiConnectionRepositoryTests.CreateRepository(firstDb);
        var secondRepository = AiConnectionRepositoryTests.CreateRepository(secondDb);
        var firstProfile = (await firstRepository.GetByIdAsync(this._firstId))!;
        var secondProfile = (await secondRepository.GetByIdAsync(this._secondId))!;
        var results = await Task.WhenAll(
            firstRepository.SelectPurposesAsync(this._clientId, Selection(firstProfile)),
            secondRepository.SelectPurposesAsync(this._clientId, Selection(secondProfile))).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Single(results, result => result.Applied);
        Assert.Single(results, result => result.Conflict);
        var expectedId = results[0].Applied ? firstProfile.Id : secondProfile.Id;
        await using var read = this.Context();
        var saved = AiConnectionRepositoryTests.CreateRepository(read);
        Assert.True((await saved.GetByIdAsync(expectedId))!.IsActive);
        foreach (var purpose in new[]
                 {
                     AiPurpose.ReviewDefault, AiPurpose.ReviewTriage, AiPurpose.ReviewVerification,
                     AiPurpose.ReviewLowEffort, AiPurpose.ReviewMediumEffort, AiPurpose.ReviewHighEffort,
                     AiPurpose.MemoryReconsideration, AiPurpose.EmbeddingDefault
                 })
        {
            Assert.Equal(expectedId, (await saved.GetActiveBindingForPurposeAsync(this._clientId, purpose))!.Connection.Id);
        }
    }

    private sealed class InjectedSaveException : Exception
    {
    }

    private sealed class FailAfterSave : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default) => throw new InjectedSaveException();
    }

    private sealed class RoutingSnapshotBarrier : DbCommandInterceptor
    {
        private int _arrived;
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!IsRoutingWrite(command))
            {
                return result;
            }

            await using var snapshot = command.Connection!.CreateCommand();
            snapshot.Transaction = command.Transaction;
            snapshot.CommandText = "SELECT count(*) FROM clients";
            await snapshot.ExecuteScalarAsync(cancellationToken);
            if (Interlocked.Increment(ref this._arrived) == 2)
            {
                this._ready.TrySetResult();
            }

            await this._ready.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            return result;
        }
    }

    private static bool IsRoutingWrite(DbCommand command) => command.CommandText.Contains("ai_routing_revision", StringComparison.Ordinal);

    private sealed class RoutingWriteAttempt : DbCommandInterceptor
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (IsRoutingWrite(command))
            {
                // Establish the serializable reader's snapshot before the competing writer is released.
                await using var snapshot = command.Connection!.CreateCommand();
                snapshot.Transaction = command.Transaction;
                snapshot.CommandText = "SELECT count(*) FROM clients";
                await snapshot.ExecuteScalarAsync(cancellationToken);
                this.Reached.TrySetResult();
            }

            return result;
        }
    }

    private sealed class RoutingWritePause : DbCommandInterceptor
    {
        private int _writes;
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> NonQueryExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (IsRoutingWrite(command) && Interlocked.Increment(ref this._writes) == 1)
            {
                this.Reached.TrySetResult();
                await this.Release.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            }

            return result;
        }
    }
}
