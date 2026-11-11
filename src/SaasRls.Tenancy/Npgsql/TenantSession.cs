using System.Data;
using Npgsql;
using SaasRls.Tenancy.Context;

namespace SaasRls.Tenancy.Npgsql;

/// <summary>
/// One tenant-scoped unit of work: an open connection, a transaction, and the tenant session variable set with
/// <c>set_config(name, tenant, is_local =&gt; true)</c>, the function form of <c>SET LOCAL</c>. The setting dies with
/// the transaction, so nothing tenant-specific survives on the pooled connection (or on a PgBouncer server connection
/// in transaction mode) after <see cref="CommitAsync"/> or <see cref="DisposeAsync"/>.
/// </summary>
public sealed class TenantSession : IAsyncDisposable
{
    private bool _completed;

    internal TenantSession(TenantId tenant, NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        Tenant = tenant;
        Connection = connection;
        Transaction = transaction;
    }

    public TenantId Tenant { get; }

    public NpgsqlConnection Connection { get; }

    public NpgsqlTransaction Transaction { get; }

    /// <summary>A command bound to this session's connection and transaction.</summary>
    public NpgsqlCommand CreateCommand(string sql) => new(sql, Connection, Transaction);

    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        await Transaction.CommitAsync(cancellationToken);
        _completed = true;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_completed && Connection.State == ConnectionState.Open)
            {
                // Not committed: roll back explicitly so the connection goes back to the pool idle, not "in transaction".
                await Transaction.RollbackAsync();
            }
        }
        finally
        {
            await Transaction.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
