using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using SaasRls.Tenancy.Context;

namespace SaasRls.Tenancy.EntityFrameworkCore;

/// <summary>
/// Sets the tenant session variable with <c>set_config(name, tenant, true)</c> as the first statement of every
/// transaction EF Core starts: an explicit <c>BeginTransaction</c> as well as the one SaveChanges opens. The setting
/// is transaction-local, so it never outlives the transaction on a pooled or PgBouncer-shared connection.
/// </summary>
public sealed class TenantTransactionInterceptor(ITenantContext tenantContext, TenancyOptions options) : DbTransactionInterceptor
{
    public override DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
    {
        using var command = CreateSetConfig(connection, result);
        command.ExecuteNonQuery();
        return result;
    }

    public override async ValueTask<DbTransaction> TransactionStartedAsync(
        DbConnection connection, TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
    {
        await using var command = CreateSetConfig(connection, result);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return result;
    }

    private NpgsqlCommand CreateSetConfig(DbConnection connection, DbTransaction transaction)
    {
        // Throws without a tenant: an EF transaction in a tenant-less scope is a bug, not a request for "no rows".
        var tenant = tenantContext.Required;
        var command = new NpgsqlCommand(
            "SELECT set_config($1, $2, true)", (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = options.SessionVariable });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = tenant.ToString() });
        return command;
    }
}

/// <summary>
/// Refuses commands that run outside a transaction. In autocommit mode the tenant variable is not set, so PostgreSQL
/// would answer every query with zero rows: safe, but a silent empty page is a hard bug to find. This turns it into an
/// immediate, explicit exception during development and in tests.
/// </summary>
public sealed class TenantCommandGuardInterceptor : DbCommandInterceptor
{
    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        EnsureInTransaction(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        EnsureInTransaction(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        EnsureInTransaction(command);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        EnsureInTransaction(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        EnsureInTransaction(command);
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        EnsureInTransaction(command);
        return ValueTask.FromResult(result);
    }

    private static void EnsureInTransaction(DbCommand command)
    {
        if (command.Transaction is null)
        {
            throw new TenantContextMissingException(
                "A tenant-scoped EF Core command ran outside a transaction, so no tenant is set and PostgreSQL would return no rows. " +
                "Wrap the work in InTenantTransactionAsync or the TenantTransaction endpoint filter.");
        }
    }
}

/// <summary>
/// EF Core skips the transaction for a SaveChanges that sends a single statement. Without a transaction there is no
/// tenant, so the insert would fail the policy. Forcing a transaction keeps every write on the tenant path.
/// </summary>
public sealed class TenantSaveChangesInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ForceTransaction(eventData);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        ForceTransaction(eventData);
        return ValueTask.FromResult(result);
    }

    private static void ForceTransaction(DbContextEventData eventData)
    {
        if (eventData.Context is { } context)
        {
            context.Database.AutoTransactionBehavior = AutoTransactionBehavior.Always;
        }
    }
}
