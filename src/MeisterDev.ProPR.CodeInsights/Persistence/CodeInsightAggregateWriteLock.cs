// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace MeisterDev.ProPR.CodeInsights.Persistence;

/// <summary>Serializes first inserts and source ordering for the existing natural aggregate key.</summary>
internal static class CodeInsightAggregateWriteLock
{
    internal static async Task AcquireAsync(MeisterProPRDbContext db, CodeInsightPullRequestKey key, CancellationToken ct)
    {
        if (db.Database.IsNpgsql())
        {
            await PostgresAdvisoryLocks.AcquireTransactionAsync(db, LockId(key), ct);
        }
    }

    /// <summary>Skips an active writer when the caller already holds the aggregate row lock.</summary>
    internal static async Task<bool> TryAcquireAsync(MeisterProPRDbContext db, CodeInsightPullRequestKey key, CancellationToken ct)
    {
        return !db.Database.IsNpgsql() || await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({LockId(key)}) AS \"Value\"").SingleAsync(ct);
    }

    private static long LockId(CodeInsightPullRequestKey key)
    {
        var hash = SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    key.ClientId,
                    key.RepositoryId,
                    key.PullRequestId
                }));
        return BinaryPrimitives.ReadInt64LittleEndian(hash);
    }
}
