using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using SaasRls.IntegrationTests.Infrastructure;
using SaasRls.Tenancy.Context;
using SaasRls.Tenancy.Npgsql;

namespace SaasRls.IntegrationTests;

/// <summary>
/// Session state on pooled connections, shown failing first and then fixed. A "request" is: take a connection from the
/// pool, do some work, give it back. The pool has one connection, so the second request provably reuses the first
/// one's backend (asserted through pg_backend_pid).
/// </summary>
public sealed class ConnectionPoolPitfallTests(DatabaseFixture db)
{
    private sealed record RequestResult(int BackendPid, long VisibleCustomers, string? TenantSetting);

    private NpgsqlDataSource SingleConnectionPool(bool noResetOnClose, string user = "saas_app", string password = DatabaseFixture.AppPassword) =>
        NpgsqlDataSource.Create(db.ConnectionString(user, password, b =>
        {
            b.MaxPoolSize = 1;
            b.NoResetOnClose = noResetOnClose;
        }));

    private static async Task<RequestResult> RequestAsync(NpgsqlDataSource pool, Func<NpgsqlConnection, Task>? work = null)
    {
        await using var connection = await pool.OpenConnectionAsync();
        if (work is not null)
        {
            await work(connection);
        }

        return await connection.QuerySingleAsync<RequestResult>(
            "SELECT pg_backend_pid() AS BackendPid, (SELECT count(*) FROM app.customers) AS VisibleCustomers, current_setting('app.tenant_id', true) AS TenantSetting");
    }

    private static Func<NpgsqlConnection, Task> SessionLevelSet(TenantId tenant) =>
        c => c.ExecuteAsync("SELECT set_config('app.tenant_id', @t, false)", new { t = tenant.ToString() });

    [Fact]
    public async Task Broken_session_level_set_leaks_to_the_next_request_when_the_pool_does_not_reset()
    {
        var (a, _) = await db.TwoTenantsAsync();
        await using var pool = SingleConnectionPool(noResetOnClose: true);

        var first = await RequestAsync(pool, SessionLevelSet(a.Tenant));
        first.VisibleCustomers.Should().Be(a.Customers.Count);

        // Request 2 never names a tenant (an anonymous endpoint, a background job, a bug) and still gets tenant A.
        var second = await RequestAsync(pool);
        second.BackendPid.Should().Be(first.BackendPid);
        second.TenantSetting.Should().Be(a.Tenant.ToString());
        second.VisibleCustomers.Should().Be(a.Customers.Count, "the session-level SET survived on the pooled connection");
    }

    [Fact]
    public async Task Fixed_by_npgsql_reset_on_return_which_discards_session_state()
    {
        var (a, _) = await db.TwoTenantsAsync();
        await using var pool = SingleConnectionPool(noResetOnClose: false);

        var first = await RequestAsync(pool, SessionLevelSet(a.Tenant));
        var second = await RequestAsync(pool);

        second.BackendPid.Should().Be(first.BackendPid, "same physical connection, so the reset, not a new backend, cleared it");
        second.TenantSetting.Should().BeEmpty("RESET/DISCARD leave a seen placeholder as '' rather than NULL");
        second.VisibleCustomers.Should().Be(0);
    }

    [Fact]
    public async Task Fixed_by_set_local_even_when_the_pool_does_not_reset()
    {
        var (a, _) = await db.TwoTenantsAsync();
        await using var pool = SingleConnectionPool(noResetOnClose: true);
        var tenants = new TenantDataSource(pool, new TenantContext(), db.Tenancy);

        int firstPid;
        await using (var session = await tenants.BeginForTenantAsync(a.Tenant))
        {
            firstPid = await session.ExecuteScalarAsync<int>("SELECT pg_backend_pid()");
            (await session.ExecuteScalarAsync<long>("SELECT count(*) FROM app.customers")).Should().Be(a.Customers.Count);
            await session.CommitAsync();
        }

        var next = await RequestAsync(pool);
        next.BackendPid.Should().Be(firstPid);
        next.VisibleCustomers.Should().Be(0, "SET LOCAL ended with the transaction");
    }

    [Fact]
    public async Task Rolled_back_and_failed_sessions_do_not_leak_either()
    {
        var (a, _) = await db.TwoTenantsAsync();
        await using var pool = SingleConnectionPool(noResetOnClose: true);
        var tenants = new TenantDataSource(pool, new TenantContext(), db.Tenancy);

        await using (var session = await tenants.BeginForTenantAsync(a.Tenant))
        {
            // Disposed without commit: rolled back.
            (await session.ExecuteScalarAsync<long>("SELECT count(*) FROM app.customers")).Should().Be(a.Customers.Count);
        }

        (await RequestAsync(pool)).VisibleCustomers.Should().Be(0);

        await using (var session = await tenants.BeginForTenantAsync(a.Tenant))
        {
            var fail = () => session.ExecuteAsync("SELECT 1 / 0");
            await fail.Should().ThrowAsync<PostgresException>();
        }

        (await RequestAsync(pool)).VisibleCustomers.Should().Be(0);
    }

    [Fact]
    public async Task Npgsql_resets_a_returned_connection_with_discard_all_observed_in_the_server_log()
    {
        await db.TwoTenantsAsync();
        // A dedicated login whose statements the server logs, so the reset is observed on the wire, not assumed.
        var role = $"pool_probe_{Guid.NewGuid():N}"[..24];
        await db.SuperuserExecuteAsync($"""
            CREATE ROLE {role} LOGIN PASSWORD 'probe' IN ROLE saas_app;
            ALTER ROLE {role} SET log_statement = 'all';
            GRANT CONNECT ON DATABASE saas TO {role};
            """);
        try
        {
            async Task<(int Pid, string Log)> RunAsync(bool noResetOnClose)
            {
                await using var pool = SingleConnectionPool(noResetOnClose, role, "probe");
                var marker = $"marker_{Guid.NewGuid():N}";
                var first = await RequestAsync(pool, c => c.ExecuteAsync("SET application_name = 'leaky'"));
                await RequestAsync(pool, c => c.ExecuteAsync($"SELECT '{marker}'"));
                var log = await db.PostgresLogsAsync();
                // Only this backend's lines between its first statement and the marker.
                var lines = log.Split('\n').Where(l => l.Contains($"[{first.BackendPid}]", StringComparison.Ordinal)).ToList();
                var end = lines.FindIndex(l => l.Contains(marker, StringComparison.Ordinal));
                return (first.BackendPid, string.Join('\n', lines.Take(end + 1)));
            }

            var reset = await RunAsync(noResetOnClose: false);
            reset.Log.Should().Contain("DISCARD ALL", "Npgsql resets the session before the connection is reused");

            var noReset = await RunAsync(noResetOnClose: true);
            noReset.Log.Should().NotContain("DISCARD ALL");
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await db.SuperuserExecuteAsync($"REVOKE CONNECT ON DATABASE saas FROM {role}; DROP ROLE {role}");
        }
    }

    [Fact]
    public async Task Tenant_sessions_detect_a_session_level_leak_left_by_other_code_and_still_isolate()
    {
        var (a, b) = await db.TwoTenantsAsync();
        await using var pool = SingleConnectionPool(noResetOnClose: true);
        var logger = new CapturingLogger<TenantDataSource>();
        var tenants = new TenantDataSource(pool, new TenantContext(), db.Tenancy, logger);

        await RequestAsync(pool, SessionLevelSet(a.Tenant));

        await using var session = await tenants.BeginForTenantAsync(b.Tenant);
        (await session.QueryAsync<Guid>("SELECT DISTINCT tenant_id FROM app.customers")).Should().Equal(b.Tenant.Value);
        logger.Messages.Should().ContainSingle(m => m.Contains("already set at session level", StringComparison.Ordinal));
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
