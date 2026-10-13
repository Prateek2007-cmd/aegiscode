// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MeisterDev.ProPR.Infrastructure.Tests.Repositories;

[Collection("PostgresIntegration1")]
public sealed class HistoricalProviderEvidenceMigrationTests(PostgresContainerFixture fixture)
{
    [Fact]
    public async Task UpgradeRetainsHistoricalRowsAndAddsNullableAttemptState()
    {
        Assert.True(fixture.IsAvailable, "Historical refresh migration verification requires PostgreSQL.");
        var database = "propr_historical_refresh_" + Guid.NewGuid().ToString("N");
        var scratch = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = database, Pooling = false }.ConnectionString;
        var id = Guid.NewGuid();
        var client = Guid.NewGuid();
        var tenant = Guid.NewGuid();
        try
        {
            await fixture.CreateDatabaseAtMigrationAsync(database, "RetainWithheldReviewFindings");
            await using (var before = Context(scratch))
            {
                before.AddRange(
                    new TenantRecord { Id = tenant, Slug = tenant.ToString("N"), DisplayName = "Migration", CreatedAt = DateTimeOffset.UtcNow },
                    new ClientRecord { Id = client, TenantId = tenant, DisplayName = "Migration", CreatedAt = DateTimeOffset.UtcNow });
                await before.SaveChangesAsync();
                await before.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                     INSERT INTO code_insight_pull_requests
                       (id, client_id, repository_id, pull_request_id, pull_request_state, latest_revision_key, last_activity_at, created_at, updated_at)
                     VALUES ({id}, {client}, 'retained-repository', 721, 'Completed', 'retained-revision',
                       '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z')
                     """);
            }

            await using (var after = Context(scratch))
            {
                await after.Database.MigrateAsync();
                Assert.False(after.Database.HasPendingModelChanges());
                var row = await after.CodeInsightPullRequests.AsNoTracking().SingleAsync(p => p.Id == id);
                Assert.Equal("retained-revision", row.LatestRevisionKey);
                Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), row.LastActivityAt);
                Assert.Null(row.LastHistoricalRefreshAttemptId);
                Assert.Null(row.LastHistoricalRefreshAttemptAt);
                Assert.Null(row.HistoricalRefreshNotBefore);
                Assert.Null(row.LastHistoricalRefreshReason);
                Assert.Null(row.LastHistoricalRefreshCompletedAt);
                var nullable = await after.Database.SqlQueryRaw<int>(
                    """
                    SELECT count(*)::int AS "Value" FROM information_schema.columns
                    WHERE table_name = 'code_insight_pull_requests' AND is_nullable = 'YES'
                      AND column_name IN ('LastHistoricalRefreshAttemptId', 'LastHistoricalRefreshAttemptAt',
                        'HistoricalRefreshNotBefore', 'LastHistoricalRefreshReason', 'LastHistoricalRefreshCompletedAt')
                    """).SingleAsync();
                Assert.Equal(5, nullable);
            }
        }
        finally
        {
            await using var admin = new NpgsqlConnection(fixture.ConnectionString);
            await admin.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)", admin);
            await command.ExecuteNonQueryAsync();
        }
    }

    private static MeisterProPRDbContext Context(string connection)
    {
        return new MeisterProPRDbContext(new DbContextOptionsBuilder<MeisterProPRDbContext>().UseNpgsql(connection, o => o.UseVector()).Options);
    }
}
