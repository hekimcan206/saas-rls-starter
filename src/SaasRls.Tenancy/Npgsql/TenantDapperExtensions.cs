using Dapper;
using SaasRls.Tenancy.Context;

namespace SaasRls.Tenancy.Npgsql;

/// <summary>
/// Dapper over a <see cref="TenantSession"/>. Every call is bound to the session's transaction, so it runs with the
/// transaction-local tenant setting; forgetting to pass the transaction (the classic Dapper mistake) is impossible.
/// </summary>
public static class TenantDapperExtensions
{
    public static Task<IEnumerable<T>> QueryAsync<T>(this TenantSession session, string sql, object? param = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.Connection.QueryAsync<T>(Command(session, sql, param, cancellationToken));
    }

    public static Task<T?> QuerySingleOrDefaultAsync<T>(this TenantSession session, string sql, object? param = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.Connection.QuerySingleOrDefaultAsync<T>(Command(session, sql, param, cancellationToken));
    }

    public static Task<T?> ExecuteScalarAsync<T>(this TenantSession session, string sql, object? param = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.Connection.ExecuteScalarAsync<T>(Command(session, sql, param, cancellationToken));
    }

    public static Task<int> ExecuteAsync(this TenantSession session, string sql, object? param = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.Connection.ExecuteAsync(Command(session, sql, param, cancellationToken));
    }

    /// <summary>
    /// A read in a single round trip: <c>SET LOCAL</c> and the query travel as one batch, which PostgreSQL runs as one
    /// implicit transaction, so the setting is still transaction-scoped (and safe under PgBouncer transaction mode).
    /// <c>SET</c> cannot take a bind parameter; the tenant is a <see cref="TenantId"/> formatted as a canonical UUID,
    /// so the literal consists of hex digits and dashes only and cannot inject SQL.
    /// </summary>
    public static async Task<IReadOnlyList<T>> QueryInImplicitTransactionAsync<T>(
        this TenantDataSource dataSource, TenantId tenant, string sql, object? param = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        await using var connection = await dataSource.DataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<T>(new CommandDefinition(
            SetLocalStatement(dataSource.Options.SessionVariable, tenant) + sql, param, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    /// <summary>The <c>SET LOCAL</c> statement for a tenant; the variable name is validated by <see cref="TenancyOptions"/>.</summary>
    public static string SetLocalStatement(string variable, TenantId tenant) =>
        $"SET LOCAL {variable} = '{tenant.Value:D}';\n";

    private static CommandDefinition Command(TenantSession session, string sql, object? param, CancellationToken cancellationToken) =>
        new(sql, param, session.Transaction, cancellationToken: cancellationToken);
}
