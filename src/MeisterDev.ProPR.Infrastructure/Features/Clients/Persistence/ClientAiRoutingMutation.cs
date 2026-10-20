// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace MeisterDev.ProPR.Infrastructure.Repositories;

/// <summary>Serializes client purpose-routing changes before their routing state is read.</summary>
internal sealed class ClientAiRoutingMutation(MeisterProPRDbContext db, IDbContextTransaction? transaction) : IAsyncDisposable
{
    public ClientRecord? Client { get; private set; }

    public static async Task<ClientAiRoutingMutation> BeginAsync(MeisterProPRDbContext db, Guid clientId, CancellationToken ct)
    {
        var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct)
            : null;
        var mutation = new ClientAiRoutingMutation(db, transaction);
        try
        {
            if (db.Database.IsRelational())
            {
                // Updating before reading obtains the client lock and invalidates an older serializable snapshot.
                // A lock-only read would not detect another routing writer that left the client row unchanged.
                await db.Clients.Where(client => client.Id == clientId)
                    .ExecuteUpdateAsync(update => update.SetProperty(client => client.AiRoutingRevision, client => client.AiRoutingRevision + 1), ct);
                mutation.Client = await db.Clients.AsNoTracking().SingleOrDefaultAsync(client => client.Id == clientId, ct);
            }
            else
            {
                mutation.Client = await db.Clients.SingleOrDefaultAsync(client => client.Id == clientId, ct);
                if (mutation.Client is { } client)
                {
                    client.AiRoutingRevision = checked(client.AiRoutingRevision + 1);
                }
            }

            return mutation;
        }
        catch
        {
            await mutation.DisposeAsync();
            throw;
        }
    }

    public async Task InitializeWorkspaceSelectionAsync(CancellationToken ct)
    {
        if (this.Client is not { } client)
        {
            throw new InvalidOperationException("The client no longer exists.");
        }

        if (db.Database.IsRelational())
        {
            await db.Clients.Where(row => row.Id == client.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(row => row.WorkspaceAiSelectionInitialized, true), ct);
        }
        else
        {
            client.WorkspaceAiSelectionInitialized = true;
        }
    }

    public async Task CommitAsync(CancellationToken ct)
    {
        if (transaction is not null)
        {
            await transaction.CommitAsync(ct);
        }
    }

    public ValueTask DisposeAsync() => transaction?.DisposeAsync() ?? ValueTask.CompletedTask;
}
