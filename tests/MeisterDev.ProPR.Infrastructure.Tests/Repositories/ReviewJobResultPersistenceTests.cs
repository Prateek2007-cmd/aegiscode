// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Persistence;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Repositories;
using MeisterDev.ProPR.Infrastructure.Tests.Fixtures;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using FactAttribute = Xunit.SkippableFactAttribute;
using MeisterDev.ProPR.Application.Features.Providers.Identity;
using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Support;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;

namespace MeisterDev.ProPR.Infrastructure.Tests.Repositories;

[Collection("PostgresIntegration1")]
public sealed class ReviewJobResultPersistenceTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private MeisterProPRDbContext _db = null!;
    private JobRepository _repository = null!;

    public async Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, builder => builder.UseVector()).Options;
        this._db = new MeisterProPRDbContext(options);
        await this._db.ReviewJobs.ExecuteDeleteAsync();
        this._repository = new JobRepository(this._db, new TestDbContextFactory(options), NullLogger<JobRepository>.Instance);
    }

    public async Task DisposeAsync()
    {
        if (this._db is not null)
        {
            await this._db.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("local", false)]
    [InlineData("file", false)]
    [InlineData("manifest", false)]
    [InlineData("file", true)]
    [InlineData("manifest", true)]
    public async Task SameFileResumeKeepsServerCertifiedHistoryAndAcceptsOnlyCurrentBoundNewPasses(string channel, bool forged)
    {
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "project", "repository", 1, 1)
            { Status = JobStatus.Processing };
        job.ApplyLease("first", 1, DateTimeOffset.UtcNow.AddMinutes(5), DateTimeOffset.UtcNow);
        var scope = ProviderSourceIdentity.FromReviewJob(job, new AdoReviewSourcePolicy()).Value;
        var connection = Guid.NewGuid();
        var dispatch = new ModelReviewManifest
        {
            JobId = job.Id, Attempt = 1, LeaseGeneration = 1, Files = ["f.cs"], InventoryComplete = true,
            ProviderScope = scope, SourceConnectionId = connection,
            AllowedModels = [new("A", "a", null, null, 1, ReviewPassScope.PerFile, Shadow: false)],
        };
        job.RecordModelEvidenceManifest(dispatch);
        await this._repository.AddAsync(job);
        var proof = new ModelReviewEvidence
        {
            JobId = job.Id, Attempt = 1, LeaseGeneration = 1, ClientId = job.ClientId, RepositoryId = job.RepositoryId, PullRequestId = 1,
            ProviderScope = scope, SourceConnectionId = connection, RevisionKey = "1", FilePath = "f.cs", InputFingerprint = "same-input",
            InputComplete = true, InventoryComplete = true, AssignedRanges = [new(1, 20)],
            Passes =
            [
                new()
                {
                    JobId = job.Id, Attempt = 1, LeaseGeneration = 1, PassKey = "original-A", ModelId = "A", LogicalModelName = "a", Ordinal = 1,
                    Disposition = ModelGenerationDisposition.Completed, CandidateSetComplete = true,
                    StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1), CompletedAt = DateTimeOffset.UtcNow.AddSeconds(-1),
                }
            ],
        };
        var file = new ReviewFileResult(job.Id, "f.cs");
        file.RecordModelEvidence(proof);
        file.MarkFailed("interrupted after A");
        await this._repository.AddFileResultAsync(file);
        this._db.ChangeTracker.Clear();
        var resumed = Assert.Single((await this._repository.GetByIdWithFileResultsAsync(job.Id))!.FileReviewResults);
        var original = Assert.Single(resumed.ModelEvidence!.Passes);
        Assert.NotNull(original.AcceptedAt);
        this._db.ChangeTracker.Clear();
        var leased = await this._db.ReviewJobs.SingleAsync(row => row.Id == job.Id);
        leased.ApplyLease("second", 2, DateTimeOffset.UtcNow.AddMinutes(5), DateTimeOffset.UtcNow);
        await this._db.SaveChangesAsync();
        var next = dispatch with { LeaseGeneration = 2, AllowedModels = [new("B", "b", null, null, 2, ReviewPassScope.PerFile, Shadow: false)] };
        await this._repository.RecordModelManifestAsync(job.Id, 1, next);
        resumed.ResetForRetry();
        resumed.RecordModelEvidence(
            proof with
            {
                LeaseGeneration = 2, Passes =
                [
                    original with
                    {
                        LeaseGeneration = 2, PassKey = "current-B", ModelId = "B", LogicalModelName = "b", Ordinal = 2,
                        AcceptedAt = null, StartedAt = DateTimeOffset.UtcNow.AddSeconds(-1), CompletedAt = DateTimeOffset.UtcNow,
                    }
                ],
            });
        Assert.Equal(2, resumed.ModelEvidence!.Passes.Count);
        var incoming = resumed.ModelEvidence;
        if (forged)
        {
            incoming = incoming with
            {
                Passes = incoming.Passes.Select(pass => pass.ModelId == "A"
                    ? pass with
                    {
                        AcceptedAt = DateTimeOffset.MinValue, Candidates = [new("forged", "f.cs", 1, "Claimed historical candidate")],
                    }
                    : pass).ToArray()
            };
        }

        var beforeAcceptance = DateTimeOffset.UtcNow;
        if (channel == "local")
        {
            await this._repository.UpdateFileResultAsync(resumed);
        }
        else
        {
            Assert.True(
                await this._repository.RetainRunnerModelEvidenceAsync(
                    new(job.Id, 2, "second"),
                    channel == "file" ? [new("f.cs", false, false, null, null, [], ModelEvidence: incoming)] : [],
                    channel == "manifest" ? [next with { Files = [], AllowedModels = [], PullRequestPasses = [incoming] }] : []));
        }

        this._db.ChangeTracker.Clear();
        var stored = (await this._repository.GetByIdWithFileResultsAsync(job.Id))!;
        var retained = channel == "manifest" && !forged
            ? Assert.Single(stored.ModelEvidenceManifest!.PullRequestPasses)
            : Assert.Single(stored.FileReviewResults).ModelEvidence!;
        var a = Assert.Single(retained.Passes, pass => pass.ModelId == "A");
        Assert.Equal(original.AcceptedAt, a.AcceptedAt);
        Assert.Empty(a.Candidates);
        Assert.Equal(1, a.LeaseGeneration);
        if (forged)
        {
            Assert.DoesNotContain(retained.Passes, pass => pass.ModelId == "B");
        }
        else
        {
            var b = Assert.Single(retained.Passes, pass => pass.ModelId == "B");
            Assert.True(b.IsCertified);
            Assert.Empty(b.Candidates);
            Assert.True(b.AcceptedAt >= beforeAcceptance);
            Assert.Equal(2, b.LeaseGeneration);
        }
    }

    [Theory]
    [InlineData("source")]
    [InlineData("envelope-generation")]
    [InlineData("pass-generation")]
    [InlineData("pass-attempt")]
    [InlineData("pass-job")]
    [InlineData("file")]
    [InlineData("candidate-file")]
    [InlineData("model")]
    [InlineData("configuration")]
    [InlineData("runtime")]
    [InlineData("kind")]
    [InlineData("stage")]
    [InlineData("ordinal")]
    [InlineData("shadow")]
    [InlineData("valid-manifest")]
    [InlineData("valid-investigation")]
    [InlineData("valid-split")]
    public async Task RunnerNestedAndSplitProofRequireTheActualDispatchedPass(string variation)
    {
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "project", "repository", 1, 1)
            { Status = JobStatus.Processing };
        job.ApplyLease("runner-a", 1, DateTimeOffset.UtcNow.AddMinutes(5), DateTimeOffset.UtcNow);
        var connection = Guid.NewGuid();
        var runtime = Guid.NewGuid();
        var configured = Guid.NewGuid();
        var scope = ProviderSourceIdentity.FromReviewJob(job, new AdoReviewSourcePolicy()).Value;
        var dispatch = new ModelReviewManifest
        {
            JobId = job.Id, Attempt = 1, LeaseGeneration = 1, Files = ["f.cs"], InventoryComplete = true,
            ProviderScope = scope, SourceConnectionId = connection,
            AllowedModels =
            [
                new("A", "a", runtime, configured, 2, ReviewPassScope.PrWide, Shadow: true),
                new("B", "b", runtime, configured, 3, ReviewPassScope.PrWide, Shadow: true)
            ],
        };
        job.RecordModelEvidenceManifest(dispatch);
        await this._repository.AddAsync(job);
        var ordinary = new ReviewFileResult(job.Id, "f.cs");
        ordinary.MarkCompleted("retained", [new("f.cs", 1, CommentSeverity.Warning, "ordinary finding")]);
        await this._repository.AddFileResultAsync(ordinary);

        ModelReviewEvidence Proof(string model, string logical, int ordinal) => new()
        {
            JobId = job.Id, Attempt = 1, LeaseGeneration = 1, ClientId = job.ClientId, RepositoryId = job.RepositoryId,
            PullRequestId = 1, ProviderScope = scope, SourceConnectionId = connection, RevisionKey = "1", FilePath = "f.cs",
            InventoryComplete = true, InputComplete = false, UnavailableReason = "model-input-scope-unavailable",
            Passes =
            [
                new()
                {
                    JobId = job.Id, Attempt = 1, LeaseGeneration = 1, PassKey = model, ModelId = model, LogicalModelName = logical,
                    ConnectionId = runtime, ConfiguredModelId = configured, Ordinal = ordinal, Shadow = true,
                    Kind = "pr-wide-generation", Lens = "pr-wide-synthesis", Disposition = ModelGenerationDisposition.Completed,
                    CandidateSetComplete = true, StartedAt = DateTimeOffset.UtcNow.AddSeconds(-1), CompletedAt = DateTimeOffset.UtcNow,
                    Candidates = [new(model + "-finding", "f.cs", 1, "A positively generated candidate")],
                }
            ],
        };

        var call = new RunnerCallContext(job.Id, 1, "runner-a");
        Assert.True(await this._repository.RetainRunnerModelEvidenceAsync(call, [], [dispatch with { PullRequestPasses = [Proof("A", "a", 2)] }]));
        this._db.ChangeTracker.Clear();
        var first = (await this._repository.GetByIdWithFileResultsAsync(job.Id))!.ModelEvidenceManifest!;
        var acceptance = Assert.Single(Assert.Single(first.PullRequestPasses).Passes).AcceptedAt;
        Assert.NotNull(acceptance);
        var proof = Proof("B", "b", 3);
        var pass = proof.Passes[0];
        proof = variation switch
        {
            "source" => proof with { SourceConnectionId = Guid.NewGuid() },
            "envelope-generation" => proof with { LeaseGeneration = null },
            "file" => proof with { FilePath = "outside.cs" },
            "candidate-file" => proof with { Passes = [pass with { Candidates = [new("outside", "outside.cs", 1, "Outside dispatched input")] }] },
            _ => proof with
            {
                Passes =
                [
                    variation switch
                    {
                        "pass-generation" => pass with { LeaseGeneration = 2 },
                        "pass-attempt" => pass with { Attempt = 2 },
                        "pass-job" => pass with { JobId = Guid.NewGuid() },
                        "model" => pass with { ModelId = "unassigned" },
                        "configuration" => pass with { ConfiguredModelId = Guid.NewGuid() },
                        "runtime" => pass with { ConnectionId = Guid.NewGuid() },
                        "kind" => pass with { Kind = "generation" },
                        "stage" => pass with { Lens = "unrecognized-stage" },
                        "ordinal" => pass with { Ordinal = 20 },
                        "shadow" => pass with { Shadow = false },
                        "valid-investigation" => pass with { Lens = "pr-wide-investigation-0123456789abcdef" },
                        _ => pass,
                    }
                ]
            },
        };
        var split = variation == "valid-split";
        Assert.True(
            await this._repository.RetainRunnerModelEvidenceAsync(
                call,
                split ? [new("f.cs", false, false, null, null, [], ModelEvidence: proof)] : [],
                split ? [dispatch with { Files = [], AllowedModels = [], PullRequestPasses = [] }] : [dispatch with { PullRequestPasses = [proof] }]));
        this._db.ChangeTracker.Clear();
        var retained = (await this._repository.GetByIdWithFileResultsAsync(job.Id))!;
        Assert.Equal("ordinary finding", Assert.Single(Assert.Single(retained.FileReviewResults).Comments!).Message);
        Assert.Equal(
            acceptance, retained.ModelEvidenceManifest!.PullRequestPasses.SelectMany(item => item.Passes).Single(item => item.ModelId == "A").AcceptedAt);
        var acceptedB = retained.ModelEvidenceManifest.PullRequestPasses.SelectMany(item => item.Passes)
            .Concat(retained.FileReviewResults.SelectMany(file => file.ModelEvidence?.Passes ?? []))
            .Where(item => item.PassKey == "B" && item.IsCertified && item.AcceptedAt != null).ToArray();
        Assert.Equal(variation.StartsWith("valid-", StringComparison.Ordinal) ? 1 : 0, acceptedB.Length);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task NewDispatchRefreshesBindingsWithoutRestampingCompatibleHistoricalPasses(bool newAttempt, bool changedSource)
    {
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "project", "repository", 1, 1)
            { Status = JobStatus.Processing };
        job.ApplyLease("runner-a", 1, DateTimeOffset.UtcNow.AddMinutes(5), DateTimeOffset.UtcNow);
        var scope = ProviderSourceIdentity.FromReviewJob(job, new AdoReviewSourcePolicy()).Value;
        var source = Guid.NewGuid();
        var first = new ModelReviewManifest
        {
            JobId = job.Id, Attempt = 1, LeaseGeneration = 1, Files = ["f.cs"], InventoryComplete = true,
            ProviderScope = scope, SourceConnectionId = source, AllowedModels = [new("A", "a", null, null)],
            PullRequestPasses =
            [
                new()
                {
                    JobId = job.Id, Attempt = 1, LeaseGeneration = 1, ClientId = job.ClientId, RepositoryId = job.RepositoryId, PullRequestId = 1,
                    ProviderScope = scope, SourceConnectionId = source, RevisionKey = "1", FilePath = "f.cs", Passes =
                    [
                        new()
                        {
                            JobId = job.Id, Attempt = 1, LeaseGeneration = 1, PassKey = "original-a", ModelId = "A", LogicalModelName = "a",
                            Disposition = ModelGenerationDisposition.Completed, CandidateSetComplete = true,
                            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1), CompletedAt = DateTimeOffset.UtcNow.AddSeconds(-1),
                        }
                    ],
                }
            ],
        };
        await this._repository.AddAsync(job);
        await this._repository.RecordModelManifestAsync(job.Id, 1, first);
        this._db.ChangeTracker.Clear();
        var original = (await this._repository.GetByIdWithFileResultsAsync(job.Id))!.ModelEvidenceManifest!;
        var receipt = Assert.Single(Assert.Single(original.PullRequestPasses).Passes).AcceptedAt;
        Assert.NotNull(receipt);
        var leased = await this._db.ReviewJobs.SingleAsync(item => item.Id == job.Id);
        leased.ApplyLease("runner-b", 2, DateTimeOffset.UtcNow.AddMinutes(5), DateTimeOffset.UtcNow);
        leased.RetryCount = newAttempt ? 1 : 0;
        await this._db.SaveChangesAsync();
        var boundary = DateTimeOffset.UtcNow;
        var next = first with
        {
            Attempt = newAttempt ? 2 : 1, LeaseGeneration = 2, Files = ["g.cs"],
            SourceConnectionId = changedSource ? Guid.NewGuid() : source,
            AllowedModels = [new("B", "b", null, null, 1, ReviewPassScope.PerFile, Shadow: false)], PullRequestPasses = [],
            AcceptedAt = DateTimeOffset.MinValue,
        };
        await this._repository.RecordModelManifestAsync(job.Id, next.Attempt, next);
        this._db.ChangeTracker.Clear();
        var current = (await this._repository.GetByIdWithFileResultsAsync(job.Id))!.ModelEvidenceManifest!;
        Assert.Equal(next.Files, current.Files);
        Assert.Equal("B", Assert.Single(current.AllowedModels).ModelId);
        Assert.Equal(next.SourceConnectionId, current.SourceConnectionId);
        Assert.True(current.AcceptedAt >= boundary);
        if (changedSource)
        {
            Assert.Empty(current.PullRequestPasses);
        }
        else
        {
            var historical = Assert.Single(Assert.Single(current.PullRequestPasses).Passes);
            Assert.Equal(receipt, historical.AcceptedAt);
            Assert.Equal(1, historical.Attempt);
            Assert.Equal(1, historical.LeaseGeneration);
        }

        var newProof = first.PullRequestPasses[0] with
        {
            Attempt = next.Attempt, LeaseGeneration = 2, SourceConnectionId = next.SourceConnectionId, FilePath = "g.cs",
            Passes =
            [
                first.PullRequestPasses[0].Passes[0] with
                {
                    PassKey = "new-b", ModelId = "B", LogicalModelName = "b", Ordinal = 1,
                    Attempt = next.Attempt, LeaseGeneration = 2, AcceptedAt = DateTimeOffset.MinValue,
                }
            ],
        };
        Assert.True(
            await this._repository.RetainRunnerModelEvidenceAsync(
                new(job.Id, 2, "runner-b"),
                [new("g.cs", false, false, null, null, [], ModelEvidence: newProof)], []));
        this._db.ChangeTracker.Clear();
        var after = (await this._repository.GetByIdWithFileResultsAsync(job.Id))!;
        var newPass = Assert.Single(Assert.Single(after.FileReviewResults).ModelEvidence!.Passes);
        Assert.True(newPass.AcceptedAt >= boundary);
        Assert.Equal(next.Attempt, newPass.Attempt);
        Assert.Equal(2, newPass.LeaseGeneration);
        if (!changedSource)
        {
            Assert.Equal(receipt, Assert.Single(Assert.Single(after.ModelEvidenceManifest!.PullRequestPasses).Passes).AcceptedAt);
        }
    }

    [Fact]
    public async Task ModelEvidenceReaderReturnsCertifiedZeroOnlyForTheRequestedSourceAndTenant()
    {
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "project", "repository", 1, 1);
        await this._repository.AddAsync(job);
        var scope = ProviderSourceIdentity.FromReviewJob(job, new AdoReviewSourcePolicy()).Value;
        var sourceConnection = Guid.NewGuid();
        var file = new ReviewFileResult(job.Id, "f.cs");
        file.RecordModelEvidence(
            new()
            {
                JobId = job.Id, Attempt = 1, ClientId = job.ClientId, RepositoryId = job.RepositoryId,
                PullRequestId = 1, ProviderScope = scope, SourceConnectionId = sourceConnection, RevisionKey = "1", FilePath = "f.cs", InputComplete = true,
                Passes =
                [
                    new()
                    {
                        PassKey = "zero", ModelId = "a", CandidateSetComplete = true, Disposition = ModelGenerationDisposition.Completed,
                        StartedAt = DateTimeOffset.UtcNow.AddSeconds(-1), CompletedAt = DateTimeOffset.UtcNow
                    }
                ]
            });
        await this._repository.AddFileResultAsync(file);
        var source = new ModelReviewSource(job.ClientId, job.RepositoryId, 1, scope, sourceConnection);
        var reader = new ModelReviewEvidenceReader(this._db);
        var snapshot = await reader.ReadAsync([source], DateTimeOffset.UtcNow);
        Assert.True(snapshot.Complete);
        var proof = Assert.Single(Assert.Single(snapshot.Executions).Files);
        Assert.True(Assert.Single(proof.Passes).IsCertified);
        Assert.NotNull(proof.Passes[0].AcceptedAt);
        Assert.Empty(proof.Passes[0].Candidates);
        Assert.Empty((await reader.ReadAsync([source with { ClientId = Guid.NewGuid() }], DateTimeOffset.UtcNow)).Executions);
        Assert.Empty((await reader.ReadAsync([source with { ProviderScope = "GitHub:https://other.invalid" }], DateTimeOffset.UtcNow)).Executions);
        Assert.Empty((await reader.ReadAsync([source with { ConnectionId = Guid.NewGuid() }], DateTimeOffset.UtcNow)).Executions);
    }

    [Fact]
    public async Task NotApplicableAssignmentHasItsOwnImmutableDurableReceipt()
    {
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "project", "repository", 1, 1);
        await this._repository.AddAsync(job);
        var file = new ReviewFileResult(job.Id, "f.cs");
        var proof = new ModelReviewEvidence
        {
            JobId = job.Id, Attempt = 1, ClientId = job.ClientId, RepositoryId = job.RepositoryId,
            PullRequestId = 1, ProviderScope = ProviderSourceIdentity.FromReviewJob(job, new AdoReviewSourcePolicy()).Value,
            RevisionKey = "1", FilePath = "f.cs", InputComplete = true, InventoryComplete = true, AssignedRanges = [new(1, 20)],
            Passes =
            [
                new()
                {
                    PassKey = "not-assigned", ModelId = "A", Disposition = ModelGenerationDisposition.NotApplicable,
                    StartedAt = DateTimeOffset.UtcNow.AddSeconds(-2), CompletedAt = DateTimeOffset.UtcNow.AddSeconds(-1)
                }
            ]
        };
        file.RecordModelEvidence(proof);
        await this._repository.AddFileResultAsync(file);
        this._db.ChangeTracker.Clear();
        var first = Assert.Single((await this._repository.GetByIdWithFileResultsAsync(job.Id))!.FileReviewResults);
        var receipt = Assert.Single(first.ModelEvidence!.Passes).AcceptedAt;
        Assert.NotNull(receipt);
        Assert.True(receipt >= proof.Passes[0].CompletedAt);
        Assert.False(first.ModelEvidence.Passes[0].IsCertified);
        first.RecordModelEvidence(proof);
        await this._repository.UpdateFileResultAsync(first);
        this._db.ChangeTracker.Clear();
        var replay = Assert.Single((await this._repository.GetByIdWithFileResultsAsync(job.Id))!.FileReviewResults);
        Assert.Equal(receipt, Assert.Single(replay.ModelEvidence!.Passes).AcceptedAt);
    }

    [Fact]
    public async Task ExpiredLocalLeaseCannotCertifyOptionalProofOrDiscardOrdinaryComments()
    {
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "project", "repository", 1, 1) { Status = JobStatus.Processing };
        job.ApplyLease("local", 1, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(-2));
        await this._repository.AddAsync(job);
        var file = new ReviewFileResult(job.Id, "f.cs");
        file.RecordModelEvidence(
            new()
            {
                JobId = job.Id, Attempt = 1, LeaseGeneration = 1, ClientId = job.ClientId, RepositoryId = job.RepositoryId,
                PullRequestId = 1, ProviderScope = ProviderSourceIdentity.FromReviewJob(job, new AdoReviewSourcePolicy()).Value, RevisionKey = "1",
                FilePath = "f.cs",
                Passes =
                [
                    new()
                    {
                        PassKey = "late", ModelId = "A", Disposition = ModelGenerationDisposition.Completed, CandidateSetComplete = true,
                        StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1), CompletedAt = DateTimeOffset.UtcNow
                    }
                ]
            });
        file.MarkCompleted("ordinary", [new("f.cs", 1, CommentSeverity.Warning, "Valid ordinary finding")]);
        await this._repository.AddFileResultAsync(file);
        this._db.ChangeTracker.Clear();
        var retained = Assert.Single((await this._repository.GetByIdWithFileResultsAsync(job.Id))!.FileReviewResults);
        Assert.Single(retained.Comments!);
        Assert.DoesNotContain(retained.ModelEvidence!.Passes, pass => pass.IsCertified);
    }

    [Fact]
    public async Task FragmentAssemblyRequiresCompleteChecksumAndUsesTheFinalServerAcceptanceTime()
    {
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "project", "repository", 1, 1)
            { Status = JobStatus.Processing };
        job.ApplyLease("runner-a", 1, DateTimeOffset.UtcNow.AddMinutes(5), DateTimeOffset.UtcNow);
        job.RecordModelEvidenceManifest(
            new()
            {
                JobId = job.Id, Attempt = 1, LeaseGeneration = 1, Files = ["f.cs"], InventoryComplete = true,
                AllowedModels = [new("a", "reviewer", null, null, 1, ReviewPassScope.PerFile, Shadow: false)]
            });
        await this._repository.AddAsync(job);
        var proof = new ModelReviewEvidence
        {
            JobId = job.Id, Attempt = 1, LeaseGeneration = 1, ClientId = job.ClientId,
            RepositoryId = job.RepositoryId, PullRequestId = 1,
            ProviderScope = ProviderSourceIdentity.FromReviewJob(job, new AdoReviewSourcePolicy()).Value,
            RevisionKey = "1", FilePath = "f.cs", InputFingerprint = "input", InputComplete = true, InventoryComplete = true,
            Passes =
            [
                new()
                {
                    JobId = job.Id, Attempt = 1, LeaseGeneration = 1, Ordinal = 1,
                    PassKey = "a", ModelId = "a", LogicalModelName = "reviewer", CandidateSetComplete = true,
                    Disposition = ModelGenerationDisposition.Completed, StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1), CompletedAt = DateTimeOffset.UtcNow
                }
            ]
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(proof, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var cut = bytes.Length / 2;
        var first = new ModelReviewProofFragment(digest, 0, bytes.Length, Convert.ToBase64String(bytes[..cut]));
        var second = new ModelReviewProofFragment(digest, cut, bytes.Length, Convert.ToBase64String(bytes[cut..]));
        var call = new RunnerCallContext(job.Id, 1, "runner-a");

        Task<bool> Send(params ModelReviewProofFragment[] fragments) => this._repository.RetainRunnerModelEvidenceAsync(
            call,
            fragments.Select(fragment => new RunnerFileOutcome("f.cs", false, false, null, null, [], ModelEvidenceFragment: fragment)).ToArray(), []);

        Assert.True(await Send(second, second));
        this._db.ChangeTracker.Clear();
        var partial = Assert.Single((await this._repository.GetByIdWithFileResultsAsync(job.Id))!.FileReviewResults).ModelEvidence;
        Assert.NotNull(partial);
        Assert.Empty(partial.Passes);
        Assert.Single(partial.PendingFragments);
        var beforeCompletion = DateTimeOffset.UtcNow;
        Assert.True(await Send(first));
        this._db.ChangeTracker.Clear();
        var completed = Assert.Single((await this._repository.GetByIdWithFileResultsAsync(job.Id))!.FileReviewResults).ModelEvidence!;
        var accepted = Assert.Single(completed.Passes).AcceptedAt;
        Assert.True(accepted >= beforeCompletion);
        Assert.Empty(completed.PendingFragments);
        Assert.True(await Send(first, second));
        Assert.True(await Send(first with { Digest = new string('0', 64) }, second with { Digest = new string('0', 64) }));
        this._db.ChangeTracker.Clear();
        var retained = Assert.Single((await this._repository.GetByIdWithFileResultsAsync(job.Id))!.FileReviewResults).ModelEvidence!;
        Assert.Equal(accepted, Assert.Single(retained.Passes).AcceptedAt);
        var leased = await this._db.ReviewJobs.SingleAsync(row => row.Id == job.Id);
        leased.ApplyLease("runner-b", 2, DateTimeOffset.UtcNow.AddMinutes(5), DateTimeOffset.UtcNow);
        await this._db.SaveChangesAsync();
        Assert.False(await Send(first));
    }

    [Fact]
    public async Task ServerProvenResumeAndUnchangedCarryForwardPreserveOriginalPassAcceptance()
    {
        var clientId = Guid.NewGuid();
        var original = new ReviewJob(Guid.NewGuid(), clientId, "https://dev.azure.com/org", "project", "repository", 1, 1);
        original.SetReviewRevision(new("head", "base", null, "first", null));
        await this._repository.AddAsync(original);
        var first = new ReviewFileResult(original.Id, "f.cs");
        first.RecordModelEvidence(
            new()
            {
                JobId = original.Id, Attempt = 1, ClientId = clientId, RepositoryId = original.RepositoryId, PullRequestId = 1,
                ProviderScope = ProviderSourceIdentity.FromReviewJob(original, new AdoReviewSourcePolicy()).Value, RevisionKey = "first", HeadSha = "head",
                BaseSha = "base",
                FilePath = "f.cs", InputFingerprint = "unchanged", InputComplete = true, Passes =
                [
                    new()
                    {
                        PassKey = "a", ModelId = "a", CandidateSetComplete = true,
                        Disposition = ModelGenerationDisposition.Completed, StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                        CompletedAt = DateTimeOffset.UtcNow
                    }
                ]
            });
        first.MarkCompleted("done", []);
        await this._repository.AddFileResultAsync(first);
        var acceptance = first.ModelEvidence!.Passes[0].AcceptedAt;
        Assert.NotNull(acceptance);

        foreach (var carry in new[] { false, true })
        {
            var current = new ReviewJob(Guid.NewGuid(), clientId, original.OrganizationUrl, original.ProjectId, original.RepositoryId, 1, carry ? 2 : 1);
            current.SetReviewRevision(carry ? new("later-head", "base", null, "second", null) : original.ReviewRevisionReference);
            await this._repository.AddAsync(current);
            var reused = carry ? ReviewFileResult.CreateCarriedForward(current.Id, first, "unchanged") : ReviewFileResult.CreateResumed(current.Id, first);
            await this._repository.AddFileResultAsync(reused);
            this._db.ChangeTracker.Clear();
            var retained = Assert.Single((await this._repository.GetByIdWithFileResultsAsync(current.Id))!.FileReviewResults).ModelEvidence;
            Assert.NotNull(retained);
            Assert.Equal(acceptance, Assert.Single(retained.Passes).AcceptedAt);
            Assert.Equal(original.Id, retained.OriginJobId);
            if (carry)
            {
                Assert.False(retained.InputComplete);
                Assert.False(retained.UnchangedInputProven);
                Assert.Equal("model-reuse-context-unavailable", retained.UnavailableReason);
            }
            else
            {
                Assert.True(retained.InputComplete);
            }
        }
    }

    [Fact]
    public async Task RunnerProofRequiresDispatchedBindingsAndCannotWriteAfterSameAttemptLeaseRotation()
    {
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "project", "repository", 1, 1)
            { Status = JobStatus.Processing };
        job.ApplyLease("runner-a", 1, DateTimeOffset.UtcNow.AddMinutes(5), DateTimeOffset.UtcNow);
        job.RecordModelEvidenceManifest(
            new()
            {
                JobId = job.Id, Attempt = 1, LeaseGeneration = 1, Files = ["f.cs"], InventoryComplete = true,
                AllowedModels = [new("a", "reviewer", null, null, 1, ReviewPassScope.PerFile, Shadow: false)]
            });
        await this._repository.AddAsync(job);
        var scope = ProviderSourceIdentity.FromReviewJob(job, new AdoReviewSourcePolicy()).Value;
        var proof = new ModelReviewEvidence
        {
            JobId = job.Id, Attempt = 1, LeaseGeneration = 1, ClientId = job.ClientId,
            RepositoryId = job.RepositoryId, PullRequestId = 1, ProviderScope = scope, RevisionKey = "1", FilePath = "f.cs",
            Passes =
            [
                new()
                {
                    JobId = job.Id, Attempt = 1, LeaseGeneration = 1, Ordinal = 1,
                    PassKey = "a", ModelId = "a", LogicalModelName = "reviewer", CandidateSetComplete = true,
                    Disposition = ModelGenerationDisposition.Completed, StartedAt = DateTimeOffset.UtcNow.AddSeconds(-1), CompletedAt = DateTimeOffset.UtcNow
                }
            ]
        };
        RunnerFileOutcome[] files = [new("f.cs", false, false, null, null, [], ModelEvidence: proof)];
        var call = new RunnerCallContext(job.Id, 1, "runner-a");

        Assert.True(await this._repository.RetainRunnerModelEvidenceAsync(call, files, []));
        this._db.ChangeTracker.Clear();
        var initial = Assert.Single((await this._repository.GetByIdWithFileResultsAsync(job.Id))!.FileReviewResults);
        Assert.True(Assert.Single(initial.ModelEvidence!.Passes).IsCertified);

        var leased = await this._db.ReviewJobs.SingleAsync(row => row.Id == job.Id);
        leased.ApplyLease("runner-b", 2, DateTimeOffset.UtcNow.AddMinutes(5), DateTimeOffset.UtcNow);
        await this._db.SaveChangesAsync();
        Assert.False(await this._repository.RetainRunnerModelEvidenceAsync(call, files, []));
        this._db.ChangeTracker.Clear();
        var current = Assert.Single((await this._repository.GetByIdWithFileResultsAsync(job.Id))!.FileReviewResults);
        Assert.Equal(initial.ModelEvidence.Passes[0].AcceptedAt, current.ModelEvidence!.Passes[0].AcceptedAt);
    }

    [Fact]
    public async Task ModelProofAcceptanceIsServerStampedAndRejectsStaleSourceOrAttemptWithoutChangingFindings()
    {
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "project", "repository", 1, 1);
        await this._repository.AddAsync(job);
        var file = new ReviewFileResult(job.Id, "f.cs");
        var proof = new ModelReviewEvidence
        {
            JobId = job.Id, Attempt = 1, FilePath = file.FilePath, ClientId = job.ClientId, RepositoryId = job.RepositoryId,
            PullRequestId = 1, ProviderScope = ProviderSourceIdentity.FromReviewJob(job, new AdoReviewSourcePolicy()).Value, RevisionKey = "1",
            InventoryComplete = true,
            Passes =
            [
                new()
                {
                    PassKey = "a", ModelId = "a", Disposition = ModelGenerationDisposition.Completed, CandidateSetComplete = true,
                    StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1), CompletedAt = DateTimeOffset.UtcNow
                }
            ],
            AcceptedAt = DateTimeOffset.MinValue,
        };
        file.RecordModelEvidence(proof);
        await this._repository.AddFileResultAsync(file);
        this._db.ChangeTracker.Clear();
        var accepted = Assert.Single((await this._repository.GetByIdWithFileResultsAsync(job.Id))!.FileReviewResults);
        Assert.True(accepted.ModelEvidence!.AcceptedAt > proof.Passes[0].CompletedAt);
        var firstAcceptance = Assert.Single(accepted.ModelEvidence.Passes).AcceptedAt;
        var concernOrigin = DateTimeOffset.UtcNow;
        accepted.RecordModelEvidence(proof with { Passes = [proof.Passes[0] with { PassKey = "b", ModelId = "b", AcceptedAt = DateTimeOffset.MinValue }] });
        await this._repository.UpdateFileResultAsync(accepted);
        this._db.ChangeTracker.Clear();
        accepted = Assert.Single((await this._repository.GetByIdWithFileResultsAsync(job.Id))!.FileReviewResults);
        Assert.Equal(firstAcceptance, accepted.ModelEvidence!.Passes.Single(pass => pass.ModelId == "a").AcceptedAt);
        var laterAcceptance = accepted.ModelEvidence.Passes.Single(pass => pass.ModelId == "b").AcceptedAt;
        Assert.True(firstAcceptance < concernOrigin);
        Assert.True(laterAcceptance >= concernOrigin);
        await this._repository.UpdateFileResultAsync(accepted);
        this._db.ChangeTracker.Clear();
        accepted = Assert.Single((await this._repository.GetByIdWithFileResultsAsync(job.Id))!.FileReviewResults);
        Assert.Equal(laterAcceptance, accepted.ModelEvidence!.Passes.Single(pass => pass.ModelId == "b").AcceptedAt);

        accepted.RecordModelEvidence(proof with { Attempt = 99, ProviderScope = "AzureDevOps:https://other.invalid", Passes = [] });
        accepted.MarkCompleted("done", [new("f.cs", 1, CommentSeverity.Warning, "retained")]);
        await this._repository.UpdateFileResultAsync(accepted);
        this._db.ChangeTracker.Clear();
        var loaded = Assert.Single((await this._repository.GetByIdWithFileResultsAsync(job.Id))!.FileReviewResults);
        Assert.Equal("retained", Assert.Single(loaded.Comments!).Message);
        Assert.Equal(2, loaded.ModelEvidence!.Passes.Count);
        Assert.All(loaded.ModelEvidence.Passes, pass => Assert.True(pass.IsCertified));
        Assert.Equal(1, loaded.ModelEvidence.Attempt);
    }

    [Fact]
    public async Task ModelGenerationProofRoundTripsFileAndJobCheckpointsWithoutEnteringPublicResults()
    {
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "project", "repository", 1, 1);
        var file = new ReviewFileResult(job.Id, "src/Reviewed.cs");
        var proof = new ModelReviewEvidence
        {
            JobId = job.Id, Attempt = 1, FilePath = file.FilePath, ClientId = job.ClientId, RepositoryId = job.RepositoryId, PullRequestId = 1,
            ProviderScope = ProviderSourceIdentity.FromReviewJob(job, new AdoReviewSourcePolicy()).Value, RevisionKey = "1", InputFingerprint = "input",
            InputComplete = true, InventoryComplete = true, AssignedRanges = [new(1, 20)],
            Passes =
            [
                new()
                {
                    PassKey = "baseline", ModelId = "model-a", LogicalModelName = "reviewer", CandidateSetComplete = true,
                    Disposition = ModelGenerationDisposition.Completed, StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1), CompletedAt = DateTimeOffset.UtcNow
                }
            ],
        };
        file.RecordModelEvidence(proof);
        file.MarkCompleted("File summary", []);
        var manifest = new ModelReviewManifest { JobId = job.Id, Files = [file.FilePath], InventoryComplete = true };
        job.RecordModelEvidenceManifest(manifest);
        await this._repository.AddAsync(job);
        await this._repository.AddFileResultAsync(file);
        var result = new ReviewResult("Summary", []) { ModelEvidence = proof, ModelEvidenceManifest = manifest };
        await this._repository.SetResultAsync(job.Id, result);
        this._db.ChangeTracker.Clear();

        var loaded = (await this._repository.GetByIdWithFileResultsAsync(job.Id))!;
        var checkpoint = Assert.Single(loaded.FileReviewResults);
        Assert.NotNull(checkpoint.ModelEvidence);
        Assert.True(Assert.Single(checkpoint.ModelEvidence.Passes).IsCertified);
        Assert.NotNull(loaded.ModelEvidenceManifest);
        Assert.NotNull(loaded.Result!.ModelEvidence);
        Assert.NotNull(loaded.Result.ModelEvidenceManifest);
        Assert.DoesNotContain("ModelEvidence", JsonSerializer.Serialize(loaded.Result));
    }

    [Fact]
    public async Task CompletedJob_RoundTripsFinalWithheldEvidenceAndAllPublicAnnotationsThroughPostgres()
    {
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "project", "repository", 1, 1);
        await this._repository.AddAsync(job);
        var withheld = new WithheldReviewFinding(
            new ReviewComment("src/Withheld.cs", 17, CommentSeverity.Warning, "Potential defect.")
            {
                OriginModelId = "review-model", OriginLogicalModelName = "reviewer", OriginPassKind = "MultiPassUnion",
                OriginPassIndex = 2, OriginPassLens = "security", OriginPassShadow = true,
                OriginSymbolName = "Execute", OriginSymbolKind = "Method", ScopeRelation = ReviewCommentScopeRelation.OnChangedLine,
                SourceReadGrounding = ReviewCommentReadGrounding.Covered,
            }, "judge_output_limit");
        var result = new ReviewResult("Summary", [new ReviewComment("src/Published.cs", 12, CommentSeverity.Warning, "Published finding.")])
        {
            WithheldFindings = [withheld],
            CarriedForwardFilePaths = ["src/Carried.cs"], CarriedForwardCandidatesSkipped = 3,
            ContextDegradedFilePaths = ["src/Degraded.cs"], ContextSkippedFilePaths = ["src/Skipped.cs"],
            BudgetSoftCapped = true, BudgetSoftCapThresholdUsd = 12.5m, BudgetSoftCapSpentUsd = 13.25m,
            BudgetSoftCapSkippedFilePaths = ["src/BudgetSkipped.cs"],
        };

        await this._repository.SetResultAsync(job.Id, result);
        this._db.ChangeTracker.Clear();
        var loaded = await this._repository.GetByIdWithFileResultsAsync(job.Id);

        var persisted = Assert.IsType<ReviewResult>(loaded!.Result);
        Assert.Equal(withheld, Assert.Single(persisted.WithheldFindings));
        Assert.Equal(JsonSerializer.Serialize(result), JsonSerializer.Serialize(persisted));
        Assert.Single(persisted.Comments);
        Assert.DoesNotContain(persisted.Comments, comment => comment.FilePath == withheld.Comment.FilePath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OlderJobResult_MissingOrNullWithheldEvidenceRestoresAllPublicAnnotations(bool includeNullField)
    {
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "project", "repository", 1, 1);
        await this._repository.AddAsync(job);
        var result = new ReviewResult("Legacy summary", [new ReviewComment("src/Reviewed.cs", 7, CommentSeverity.Info, "Original finding.")])
        {
            CarriedForwardFilePaths = ["src/Carried.cs"], CarriedForwardCandidatesSkipped = 2,
            ContextDegradedFilePaths = ["src/Degraded.cs"], ContextSkippedFilePaths = ["src/Skipped.cs"],
            BudgetSoftCapped = true, BudgetSoftCapThresholdUsd = 5.5m, BudgetSoftCapSpentUsd = 6.25m,
            BudgetSoftCapSkippedFilePaths = ["src/BudgetSkipped.cs"],
        };
        await this._repository.SetResultAsync(job.Id, result);
        var legacyPayload = JsonSerializer.SerializeToNode(result)!.AsObject();
        if (includeNullField)
        {
            legacyPayload[nameof(ReviewResult.WithheldFindings)] = null;
        }

        var json = legacyPayload.ToJsonString();
        await this._db.Database.ExecuteSqlInterpolatedAsync($"UPDATE review_jobs SET result_json = {json}::jsonb WHERE id = {job.Id}");
        this._db.ChangeTracker.Clear();

        var loaded = await this._repository.GetByIdWithFileResultsAsync(job.Id);

        var persisted = Assert.IsType<ReviewResult>(loaded!.Result);
        Assert.Empty(persisted.WithheldFindings);
        Assert.Equal(JsonSerializer.Serialize(result), JsonSerializer.Serialize(persisted));
    }
}
