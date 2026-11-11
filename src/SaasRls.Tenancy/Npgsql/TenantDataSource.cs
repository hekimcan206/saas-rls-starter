using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SaasRls.Tenancy.Context;

namespace SaasRls.Tenancy.Npgsql;

/// <summary>
/// Opens tenant-scoped sessions on an <see cref="NpgsqlDataSource"/> that connects as the application role (a role that
/// neither owns the tables nor has BYPASSRLS). Register it scoped: it reads the tenant from the scoped
/// <see cref="ITenantContext"/>.
/// </summary>
public sealed partial class TenantDataSource(
    NpgsqlDataSource dataSource,
    ITenantContext tenantContext,
    TenancyOptions options,
    ILogger<TenantDataSource>? logger = null)
{
    public const string MeterName = "SaasRls.Tenancy";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> SessionLeaks = Meter.CreateCounter<long>(
        "tenancy.session_variable.leaks",
        description: "Sessions that started with the tenant variable already set at session level by other code.");

    private readonly ILogger _logger = logger ?? NullLogger<TenantDataSource>.Instance;

    public NpgsqlDataSource DataSource => dataSource;

    public TenancyOptions Options => options;

    /// <summary>Begins a session for the tenant of the current scope; throws when there is none (fail closed).</summary>
    public Task<TenantSession> BeginAsync(CancellationToken cancellationToken = default) =>
        BeginForTenantAsync(tenantContext.Required, cancellationToken);

    /// <summary>Begins a session for an explicit tenant, for work outside a request (jobs, message handlers).</summary>
    public async Task<TenantSession> BeginForTenantAsync(TenantId tenant, CancellationToken cancellationToken = default)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            // Npgsql sends BEGIN lazily, prepended to the next command, so BEGIN + set_config is a single round trip.
            var transaction = await connection.BeginTransactionAsync(cancellationToken);

            // One statement reads the value the session had *before* this transaction and sets the transaction-local
            // one. A non-empty previous value means some other code ran a session-level SET on this pooled connection:
            // harmless here (the local value wins), but a bug worth surfacing before it bites a path without this guard.
            await using var command = new NpgsqlCommand(
                "SELECT current_setting($1, true), set_config($1, $2, true)", connection, transaction);
            command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = options.SessionVariable });
            command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = tenant.ToString() });
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                await reader.ReadAsync(cancellationToken);
                var previous = await reader.IsDBNullAsync(0, cancellationToken) ? null : reader.GetString(0);
                if (!string.IsNullOrEmpty(previous))
                {
                    SessionLeaks.Add(1);
                    LogSessionLeak(_logger, options.SessionVariable);
                }
            }

            return new TenantSession(tenant, connection, transaction);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "A pooled connection arrived with {Variable} already set at session level; overriding it for this transaction. Some code path uses SET instead of SET LOCAL.")]
    private static partial void LogSessionLeak(ILogger logger, string variable);
}
