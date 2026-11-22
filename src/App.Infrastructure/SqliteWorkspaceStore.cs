using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Portfel.Application;
using Portfel.Domain;

namespace Portfel.Infrastructure;

public sealed class PortfelDb(DbContextOptions<PortfelDb> options) : DbContext(options)
{
    internal DbSet<WorkspaceRow> Workspaces => Set<WorkspaceRow>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        var workspace = model.Entity<WorkspaceRow>();
        workspace.ToTable("workspaces");
        workspace.HasKey(row => row.OwnerId);
        workspace.Property(row => row.OwnerId).HasColumnName("owner_id");
        workspace.Property(row => row.Data).HasColumnName("data");
    }
}

internal sealed class WorkspaceRow
{
    public required string OwnerId { get; init; }
    public required string Data { get; set; }
}

public sealed class SqliteWorkspaceStore(PortfelDb db) : IWorkspaceStore
{
    public async Task<Workspace> ReadAsync(string ownerId, CancellationToken cancellationToken)
    {
        WorkspaceRow? row = await db.Workspaces.AsNoTracking().SingleOrDefaultAsync(row => row.OwnerId == ownerId, cancellationToken);
        return row is null ? Workspace.Empty : Decode(row.Data);
    }

    public async Task<T> UpdateAsync<T>(string ownerId, Func<Workspace, (Workspace Next, T Result)> change,
        CancellationToken cancellationToken)
    {
        // Microsoft.Data.Sqlite acquires the writer lock before reading the current state.
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        WorkspaceRow? row = await db.Workspaces.AsNoTracking().SingleOrDefaultAsync(row => row.OwnerId == ownerId, cancellationToken);
        Workspace previous = row is null ? Workspace.Empty : Decode(row.Data);
        (Workspace next, T result) = change(previous);
        if (!ReferenceEquals(previous, next))
        {
            string json = JsonSerializer.Serialize(next, PortfelJson.Options);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO workspaces (owner_id, data) VALUES ({ownerId}, {json})
                ON CONFLICT (owner_id) DO UPDATE SET data = excluded.data
                """, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public Task<bool> ReadyAsync(CancellationToken cancellationToken) => db.Database.CanConnectAsync(cancellationToken);

    private static Workspace Decode(string json) => JsonSerializer.Deserialize<Workspace>(json, PortfelJson.Options)
        ?? throw new InvalidDataException("Состояние портфеля не читается.");
}

internal sealed class DatabaseStartup(IServiceScopeFactory scopes) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<PortfelDb>().Database.MigrateAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
