// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Concurrent;
using System.Data.Common;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Models;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace MeisterDev.ProPR.Infrastructure.Features.Crawling.Persistence;

/// <summary>Validates and locks the observation fence in the same transaction as each consumer write.</summary>
internal sealed class DiscussionWriteFenceInterceptor : DbCommandInterceptor
{
    private readonly ConcurrentDictionary<DbCommand, DbTransaction> _ownedTransactions = new();

    // Usage accounting, protocol diagnostics and audit records remain writable after observation supersession.
    private static bool WritesEvidence(string sql) =>
        sql.Contains("code_insight_", StringComparison.OrdinalIgnoreCase) ||
        sql.Contains("reviewer_performance_", StringComparison.OrdinalIgnoreCase) ||
        sql.Contains("retained_pull_requests", StringComparison.OrdinalIgnoreCase) ||
        sql.Contains("retained_threads", StringComparison.OrdinalIgnoreCase) ||
        sql.Contains("retained_thread_comments", StringComparison.OrdinalIgnoreCase) ||
        sql.Contains("retained_file_diffs", StringComparison.OrdinalIgnoreCase) ||
        sql.Contains("thread_memory_records", StringComparison.OrdinalIgnoreCase) ||
        sql.Contains("review_pr_scan_threads", StringComparison.OrdinalIgnoreCase);

    private static NpgsqlCommand CreateGuard(DbCommand command, DiscussionWriteFence fence) => new(
        "SELECT 1 FROM discussion_observation_fences WHERE id=@id AND token=@token AND expires_at>clock_timestamp() FOR SHARE",
        (NpgsqlConnection)command.Connection!, (NpgsqlTransaction?)command.Transaction)
    {
        Parameters = { new("id", fence.SourceId), new("token", fence.Token) },
    };

    private static bool RequiresFence(DbCommand command, DiscussionWriteFence? fence) =>
        fence is not null && command.Connection is NpgsqlConnection && DiscussionSqlCommandClassifier.IsWrite(command.CommandText) &&
        WritesEvidence(command.CommandText);

    private void Guard(DbCommand command)
    {
        var fence = DiscussionWriteFenceContext.Current;
        if (!RequiresFence(command, fence))
        {
            return;
        }

        try
        {
            if (command.Transaction is null)
            {
                var transaction = command.Connection!.BeginTransaction();
                command.Transaction = transaction;
                this._ownedTransactions.TryAdd(command, transaction);
            }

            using var guard = CreateGuard(command, fence!);
            if (guard.ExecuteScalar() is null)
            {
                throw new InvalidOperationException("The discussion observation was superseded before evidence persistence.");
            }
        }
        catch
        {
            this.Rollback(command);
            throw;
        }
    }

    private async Task GuardAsync(DbCommand command, CancellationToken ct)
    {
        var fence = DiscussionWriteFenceContext.Current;
        if (!RequiresFence(command, fence))
        {
            return;
        }

        try
        {
            if (command.Transaction is null)
            {
                var transaction = await command.Connection!.BeginTransactionAsync(ct);
                command.Transaction = transaction;
                this._ownedTransactions.TryAdd(command, transaction);
            }

            await using var guard = CreateGuard(command, fence!);
            if (await guard.ExecuteScalarAsync(ct) is null)
            {
                throw new InvalidOperationException("The discussion observation was superseded before evidence persistence.");
            }
        }
        catch
        {
            await this.RollbackAsync(command);
            throw;
        }
    }

    private void Commit(DbCommand command, DbDataReader? reader = null)
    {
        if (!this._ownedTransactions.TryRemove(command, out var transaction))
        {
            return;
        }

        try
        {
            reader?.Close();
            transaction.Commit();
        }
        finally
        {
            command.Transaction = null;
            transaction.Dispose();
        }
    }

    private void Rollback(DbCommand command)
    {
        if (!this._ownedTransactions.TryRemove(command, out var transaction))
        {
            return;
        }

        try
        {
            transaction.Rollback();
        }
        finally
        {
            command.Transaction = null;
            transaction.Dispose();
        }
    }

    private async Task CommitAsync(DbCommand command, DbDataReader? reader = null, CancellationToken ct = default)
    {
        if (!this._ownedTransactions.TryRemove(command, out var transaction))
        {
            return;
        }

        try
        {
            if (reader is not null)
            {
                await reader.CloseAsync();
            }

            await transaction.CommitAsync(ct);
        }
        finally
        {
            command.Transaction = null;
            await transaction.DisposeAsync();
        }
    }

    private async Task RollbackAsync(DbCommand command)
    {
        if (!this._ownedTransactions.TryRemove(command, out var transaction))
        {
            return;
        }

        try
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
        finally
        {
            command.Transaction = null;
            await transaction.DisposeAsync();
        }
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData,
        InterceptionResult<DbDataReader> result, CancellationToken ct = default)
    {
        await this.GuardAsync(command, ct);
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData,
        InterceptionResult<int> result, CancellationToken ct = default)
    {
        await this.GuardAsync(command, ct);
        return result;
    }

    public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData,
        InterceptionResult<object> result, CancellationToken ct = default)
    {
        await this.GuardAsync(command, ct);
        return result;
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        this.Guard(command);
        return result;
    }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        this.Guard(command);
        return result;
    }

    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        this.Guard(command);
        return result;
    }

    public override InterceptionResult DataReaderClosing(DbCommand command, DataReaderClosingEventData eventData, InterceptionResult result)
    {
        this.Commit(command, eventData.DataReader);
        return result;
    }

    public override async ValueTask<InterceptionResult> DataReaderClosingAsync(
        DbCommand command, DataReaderClosingEventData eventData, InterceptionResult result)
    {
        await this.CommitAsync(command, eventData.DataReader);
        return result;
    }

    public override InterceptionResult DataReaderDisposing(DbCommand command, DataReaderDisposingEventData eventData, InterceptionResult result)
    {
        this.Commit(command, eventData.DataReader);
        return result;
    }

    public override async ValueTask<int> NonQueryExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken ct = default)
    {
        await this.CommitAsync(command, ct: ct);
        return result;
    }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        this.Commit(command);
        return result;
    }

    public override async ValueTask<object?> ScalarExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken ct = default)
    {
        await this.CommitAsync(command, ct: ct);
        return result;
    }

    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        this.Commit(command);
        return result;
    }

    public override void CommandCanceled(DbCommand command, CommandEndEventData eventData) => this.Rollback(command);

    public override Task CommandCanceledAsync(DbCommand command, CommandEndEventData eventData, CancellationToken ct = default)
        => this.RollbackAsync(command);

    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData) => this.Rollback(command);

    public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken ct = default)
        => this.RollbackAsync(command);
}
