using Dapper;
using Npgsql;
using SaasRls.IntegrationTests.Infrastructure;
using SaasRls.Tenancy.Context;
using SaasRls.Tenancy.Npgsql;

namespace SaasRls.IntegrationTests;

/// <summary>
/// PgBouncer in transaction mode hands a server connection to a client for one transaction only. Session state set
/// outside a transaction therefore stays on the server connection and reaches whichever client runs the next
/// transaction on it, even a client that never shared an application-side pool with the first. These tests run real
/// PgBouncer 1.25 in front of PostgreSQL; the <c>saas_tiny</c> pool has a single server connection, which turns "might
/// leak under load" into "leaks every time".
/// </summary>
public sealed class PgBouncerTests(DatabaseFixture db) : IAsyncLifetime
{
    // The leaks below outlive the test (and the client) that caused them: in transaction mode PgBouncer never resets a
    // server connection on its own. Each test therefore starts and ends by discarding the single saas_tiny connection's
    // session state, or one test's leak shows up as another test's failure (it did, before this was added).
    public ValueTask InitializeAsync() => DiscardTinyPoolStateAsync();

    public ValueTask DisposeAsync() => DiscardTinyPoolStateAsync();

    private async ValueTask DiscardTinyPoolStateAsync()
    {
        await using var client = Client("saas_tiny");
        await using var connection = await client.OpenConnectionAsync();
        await connection.ExecuteAsync("DISCARD ALL");
    }

    private sealed record Observation(int ServerPid, string? TenantSetting, long VisibleCustomers);

    private const string Observe =
        "SELECT pg_backend_pid() AS ServerPid, current_setting('app.tenant_id', true) AS TenantSetting, (SELECT count(*) FROM app.customers) AS VisibleCustomers";

    // Separate data sources with Npgsql pooling off: two genuinely different client connections to PgBouncer.
    private NpgsqlDataSource Client(string pool, bool npgsqlPooling = false, bool noResetOnClose = true) =>
        NpgsqlDataSource.Create(db.PgBouncerConnectionString(pool, b =>
        {
            b.Pooling = npgsqlPooling;
            b.NoResetOnClose = noResetOnClose;
        }));

    [Fact]
    public async Task Broken_transaction_mode_session_set_leaks_across_different_clients()
    {
        var (a, _) = await db.TwoTenantsAsync();
        await using var clientA = Client("saas_tiny");
        await using var clientB = Client("saas_tiny");

        await using var connectionA = await clientA.OpenConnectionAsync();
        // Autocommit, session level: a one-statement transaction, after which PgBouncer takes the server connection back.
        var aPid = await connectionA.QuerySingleAsync<int>(
            "SELECT pg_backend_pid() FROM (SELECT set_config('app.tenant_id', @t, false)) s", new { t = a.Tenant.ToString() });

        // Client A is still connected and idle. Client B, a different connection that never named a tenant:
        await using var connectionB = await clientB.OpenConnectionAsync();
        var seen = await connectionB.QuerySingleAsync<Observation>(Observe);

        seen.ServerPid.Should().Be(aPid, "PgBouncer gave B the server connection A used");
        seen.TenantSetting.Should().Be(a.Tenant.ToString());
        seen.VisibleCustomers.Should().Be(a.Customers.Count, "B reads tenant A's rows");
    }

    [Fact]
    public async Task Broken_the_leaked_setting_outlives_the_client_that_set_it()
    {
        var (a, _) = await db.TwoTenantsAsync();
        await using (var clientA = Client("saas_tiny"))
        await using (var connectionA = await clientA.OpenConnectionAsync())
        {
            await connectionA.ExecuteAsync("SELECT set_config('app.tenant_id', @t, false)", new { t = a.Tenant.ToString() });
        }

        // Client A has disconnected. Transaction mode runs no server_reset_query on disconnect (server_reset_query_always
        // defaults to 0), so the server connection keeps A's tenant for as long as it lives.
        await using var clientB = Client("saas_tiny");
        await using var connectionB = await clientB.OpenConnectionAsync();
        (await connectionB.QuerySingleAsync<Observation>(Observe)).VisibleCustomers.Should().Be(a.Customers.Count);
    }

    [Fact]
    public async Task Broken_npgsql_reset_on_return_does_not_help_behind_a_transaction_pooler()
    {
        var (a, _) = await db.TwoTenantsAsync();
        // Npgsql pooling with its default reset: DISCARD ALL is only sent when this pooled connection is used again,
        // and by then PgBouncer has long given the server connection to someone else.
        await using var clientA = Client("saas_tiny", npgsqlPooling: true, noResetOnClose: false);
        await using var clientB = Client("saas_tiny");

        await using (var connectionA = await clientA.OpenConnectionAsync())
        {
            await connectionA.ExecuteAsync("SELECT set_config('app.tenant_id', @t, false)", new { t = a.Tenant.ToString() });
        }

        await using var connectionB = await clientB.OpenConnectionAsync();
        (await connectionB.QuerySingleAsync<Observation>(Observe)).VisibleCustomers.Should().Be(a.Customers.Count);
    }

    [Fact]
    public async Task Fixed_set_local_dies_with_the_transaction_so_the_next_client_sees_nothing()
    {
        var (a, _) = await db.TwoTenantsAsync();
        await using var clientA = Client("saas_tiny");
        await using var clientB = Client("saas_tiny");
        var tenants = new TenantDataSource(clientA, new TenantContext(), db.Tenancy);

        int aPid;
        await using (var session = await tenants.BeginForTenantAsync(a.Tenant))
        {
            aPid = await session.ExecuteScalarAsync<int>("SELECT pg_backend_pid()");
            (await session.ExecuteScalarAsync<long>("SELECT count(*) FROM app.customers")).Should().Be(a.Customers.Count);
            await session.CommitAsync();
        }

        await using var connectionB = await clientB.OpenConnectionAsync();
        var seen = await connectionB.QuerySingleAsync<Observation>(Observe);
        seen.ServerPid.Should().Be(aPid);
        seen.VisibleCustomers.Should().Be(0);
    }

    [Fact]
    public async Task One_round_trip_set_local_batches_are_safe_through_pgbouncer()
    {
        var (a, b) = await db.TwoTenantsAsync();
        await using var client = Client("saas_tiny");
        var tenants = new TenantDataSource(client, new TenantContext(), db.Tenancy);

        (await tenants.QueryInImplicitTransactionAsync<Guid>(a.Tenant, "SELECT tenant_id FROM app.customers"))
            .Should().HaveCount(a.Customers.Count).And.OnlyContain(t => t == a.Tenant.Value);

        await using var other = Client("saas_tiny");
        await using var connection = await other.OpenConnectionAsync();
        (await connection.QuerySingleAsync<Observation>(Observe)).VisibleCustomers.Should().Be(0);
        (await tenants.QueryInImplicitTransactionAsync<Guid>(b.Tenant, "SELECT tenant_id FROM app.customers")).Should().HaveCount(b.Customers.Count);
    }

    [Fact]
    public async Task Session_mode_runs_server_reset_query_when_the_client_disconnects()
    {
        var (a, _) = await db.TwoTenantsAsync();
        await using var clientA = Client("saas_session");
        await using var clientB = Client("saas_session");

        int aPid;
        await using (var connectionA = await clientA.OpenConnectionAsync())
        {
            aPid = await connectionA.ExecuteScalarAsync<int>("SELECT pg_backend_pid()");
            await connectionA.ExecuteAsync("SELECT set_config('app.tenant_id', @t, false)", new { t = a.Tenant.ToString() });
        }

        // In session mode B could not even get the (single) server connection while A held it. After A disconnects,
        // PgBouncer runs server_reset_query (DISCARD ALL) before handing the connection on.
        await using var connectionB = await clientB.OpenConnectionAsync();
        var seen = await connectionB.QuerySingleAsync<Observation>(Observe);
        seen.ServerPid.Should().Be(aPid);
        seen.VisibleCustomers.Should().Be(0);
    }

    [Fact]
    public async Task Concurrent_tenant_sessions_stay_isolated_through_pgbouncer_even_next_to_a_leaky_client()
    {
        var (a, b) = await db.TwoTenantsAsync();
        await using var pool = Client("saas_lab", npgsqlPooling: true);
        var tenants = new TenantDataSource(pool, new TenantContext(), db.Tenancy);
        await using var leaky = Client("saas_lab");
        using var stop = new CancellationTokenSource();

        // A misbehaving client keeps planting tenant B at session level on whatever server connection it gets.
        var polluter = Task.Run(async () =>
        {
            var planted = 0;
            while (!stop.IsCancellationRequested)
            {
                await using var connection = await leaky.OpenConnectionAsync();
                await connection.ExecuteAsync("SELECT set_config('app.tenant_id', @t, false)", new { t = b.Tenant.ToString() });
                planted++;
            }

            return planted;
        });

        var workers = Enumerable.Range(0, 200).Select(async i =>
        {
            var tenant = i % 2 == 0 ? a : b;
            await using var session = await tenants.BeginForTenantAsync(tenant.Tenant);
            var seen = (await session.QueryAsync<Guid>("SELECT tenant_id FROM app.orders")).ToList();
            await session.CommitAsync();
            return (Expected: tenant, Seen: seen);
        });
        var results = await Task.WhenAll(workers);
        await stop.CancelAsync();
        (await polluter).Should().BeGreaterThan(0);

        foreach (var (expected, seen) in results)
        {
            seen.Should().HaveCount(expected.Orders.Count).And.OnlyContain(t => t == expected.Tenant.Value);
        }
    }
}
