// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Data.Common;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MeisterDev.ProPR.Infrastructure.Features.Crawling.Persistence;

internal sealed class DiscussionDatabaseMetricsInterceptor : DbCommandInterceptor
{
    private static void Count(DbCommand command)
    {
        DiscussionSynchronizationMetrics.RecordDatabaseCommand(DiscussionSqlCommandClassifier.IsWrite(command.CommandText));
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken ct = default)
    {
        Count(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult DataReaderDisposing(DbCommand command, DataReaderDisposingEventData eventData, InterceptionResult result)
    {
        DiscussionSynchronizationMetrics.RecordDatabaseRows(eventData.ReadCount, Math.Max(0, eventData.RecordsAffected));
        return result;
    }

    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken ct = default)
    {
        Count(command);
        DiscussionSynchronizationMetrics.RecordDatabaseRows(0, Math.Max(0, result));
        return ValueTask.FromResult(result);
    }
}
