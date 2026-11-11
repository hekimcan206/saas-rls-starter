using Dapper;
using Npgsql;
using SaasRls.IntegrationTests.Infrastructure;
using SaasRls.Tenancy.Context;
using SaasRls.Tenancy.Npgsql;

namespace SaasRls.IntegrationTests;

/// <summary>
/// "Tenant A can't see B", statement by statement, against the real schema as the real application role. None of the
/// SQL below contains a tenant predicate: whatever isolation the assertions observe comes from the RLS policies.
/// </summary>
public sealed class TenantIsolationTests(DatabaseFixture db)
{
    [Fact]
    public async Task Read_returns_only_the_callers_rows()
    {
        var (a, b) = await db.TwoTenantsAsync();
        await using var session = await db.Tenants.BeginForTenantAsync(a.Tenant);

        var customers = (await session.QueryAsync<(Guid Id, Guid TenantId)>("SELECT id, tenant_id FROM app.customers")).ToList();
        customers.Select(c => c.Id).Should().BeEquivalentTo(a.Customers);
        customers.Should().OnlyContain(c => c.TenantId == a.Tenant.Value);

        var orders = (await session.QueryAsync<Guid>("SELECT id FROM app.orders")).ToList();
        orders.Should().BeEquivalentTo(a.Orders.Select(o => o.Id));

        // Asking for B's rows by primary key is no different from asking for ids that do not exist.
        (await session.QueryAsync<Guid>("SELECT id FROM app.customers WHERE id = ANY(@ids)", new { ids = b.Customers.ToArray() }))
            .Should().BeEmpty();
        (await session.QueryAsync<Guid>("SELECT id FROM app.tenants")).Should().Equal(a.Tenant.Value);
    }

    [Fact]
    public async Task Update_cannot_touch_another_tenants_rows()
    {
        var (a, b) = await db.TwoTenantsAsync();
        await using (var session = await db.Tenants.BeginForTenantAsync(a.Tenant))
        {
            (await session.ExecuteAsync("UPDATE app.customers SET name = 'hijacked' WHERE id = @id", new { id = b.Customers[0] })).Should().Be(0);
            // No WHERE clause at all: still only A's rows.
            (await session.ExecuteAsync("UPDATE app.orders SET status = 'paid'")).Should().Be(a.Orders.Count);
            await session.CommitAsync();
        }

        await using var asB = await db.Tenants.BeginForTenantAsync(b.Tenant);
        (await asB.QueryAsync<string>("SELECT name FROM app.customers")).Should().NotContain("hijacked");
        (await asB.QueryAsync<string>("SELECT DISTINCT status FROM app.orders")).Should().Equal("open");
    }

    [Fact]
    public async Task Update_cannot_move_a_row_to_another_tenant()
    {
        var (a, b) = await db.TwoTenantsAsync();
        await using var session = await db.Tenants.BeginForTenantAsync(a.Tenant);

        // Column privilege first: saas_app may not update tenant_id at all. Were the grant wider, WITH CHECK would reject it.
        var act = () => session.ExecuteAsync("UPDATE app.customers SET tenant_id = @b WHERE id = @id", new { b = b.Tenant.Value, id = a.Customers[0] });
        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Delete_cannot_touch_another_tenants_rows()
    {
        var (a, b) = await db.TwoTenantsAsync();
        await using (var session = await db.Tenants.BeginForTenantAsync(a.Tenant))
        {
            (await session.ExecuteAsync("DELETE FROM app.orders WHERE id = ANY(@ids)", new { ids = b.Orders.Select(o => o.Id).ToArray() })).Should().Be(0);
            (await session.ExecuteAsync("DELETE FROM app.orders")).Should().Be(a.Orders.Count);
            await session.CommitAsync();
        }

        await using var asB = await db.Tenants.BeginForTenantAsync(b.Tenant);
        (await asB.ExecuteScalarAsync<long>("SELECT count(*) FROM app.orders")).Should().Be(b.Orders.Count);
    }

    [Fact]
    public async Task Insert_with_a_foreign_tenant_id_is_rejected_by_with_check()
    {
        var (a, b) = await db.TwoTenantsAsync();
        await using var session = await db.Tenants.BeginForTenantAsync(a.Tenant);

        var act = () => session.ExecuteAsync(
            "INSERT INTO app.customers (id, tenant_id, name, email) VALUES (@id, @tenant, 'planted', 'planted@example.test')",
            new { id = Guid.CreateVersion7(), tenant = b.Tenant.Value });
        var error = (await act.Should().ThrowAsync<PostgresException>()).Which;
        error.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        error.MessageText.Should().Contain("violates row-level security policy");
    }

    [Fact]
    public async Task Referencing_another_tenants_row_fails_exactly_like_a_missing_row()
    {
        var (a, b) = await db.TwoTenantsAsync();

        async Task<string> InsertOrderFor(Guid customerId)
        {
            await using var session = await db.Tenants.BeginForTenantAsync(a.Tenant);
            try
            {
                await session.ExecuteAsync(
                    "INSERT INTO app.orders (id, customer_id, number, total_cents) VALUES (@id, @customerId, 999999, 1)",
                    new { id = Guid.CreateVersion7(), customerId });
                return "inserted";
            }
            catch (PostgresException ex)
            {
                return ex.SqlState;
            }
        }

        // Composite foreign key (tenant_id, customer_id): B's real customer and a random id are indistinguishable.
        (await InsertOrderFor(b.Customers[0])).Should().Be(PostgresErrorCodes.ForeignKeyViolation);
        (await InsertOrderFor(Guid.NewGuid())).Should().Be(PostgresErrorCodes.ForeignKeyViolation);
    }

    [Fact]
    public async Task Joins_only_ever_combine_the_callers_rows()
    {
        var (a, _) = await db.TwoTenantsAsync();
        await using var session = await db.Tenants.BeginForTenantAsync(a.Tenant);

        // Joined on customer_id only, deliberately ignoring tenant_id.
        var joined = (await session.QueryAsync<(Guid OrderTenant, Guid CustomerTenant)>(
            "SELECT o.tenant_id, c.tenant_id FROM app.orders o JOIN app.customers c ON c.id = o.customer_id")).ToList();
        joined.Should().HaveCount(a.Orders.Count).And.OnlyContain(r => r.OrderTenant == a.Tenant.Value && r.CustomerTenant == a.Tenant.Value);

        (await session.ExecuteScalarAsync<long>("SELECT count(*) FROM app.orders CROSS JOIN app.customers"))
            .Should().Be(a.Orders.Count * a.Customers.Count);
    }

    [Fact]
    public async Task Aggregates_are_computed_over_the_callers_rows_only()
    {
        var (a, _) = await db.TwoTenantsAsync();
        await using var session = await db.Tenants.BeginForTenantAsync(a.Tenant);

        var row = await session.QuerySingleOrDefaultAsync<(long Count, long Sum, long Max, long Tenants)>(
            "SELECT count(*), sum(total_cents)::bigint, max(total_cents), count(DISTINCT tenant_id) FROM app.orders");
        row.Count.Should().Be(a.Orders.Count);
        row.Sum.Should().Be(a.TotalCents);
        row.Max.Should().Be(a.Orders.Max(o => o.TotalCents));
        row.Tenants.Should().Be(1);

        (await session.QueryAsync<Guid>("SELECT tenant_id FROM app.orders GROUP BY tenant_id")).Should().Equal(a.Tenant.Value);
    }

    [Fact]
    public async Task Security_invoker_view_applies_the_callers_policies()
    {
        var (a, _) = await db.TwoTenantsAsync();
        await using var session = await db.Tenants.BeginForTenantAsync(a.Tenant);

        var rows = (await session.QueryAsync<(Guid TenantId, Guid CustomerId, long TotalCents)>(
            "SELECT tenant_id, customer_id, total_cents FROM app.customer_order_totals")).ToList();
        rows.Select(r => r.CustomerId).Should().BeEquivalentTo(a.Customers);
        rows.Sum(r => r.TotalCents).Should().Be(a.TotalCents);
        rows.Should().OnlyContain(r => r.TenantId == a.Tenant.Value);
    }

    [Fact]
    public async Task Security_invoker_function_sees_nothing_of_another_tenant()
    {
        var (a, b) = await db.TwoTenantsAsync();
        await using var session = await db.Tenants.BeginForTenantAsync(a.Tenant);

        (await session.ExecuteScalarAsync<long>("SELECT app.customer_lifetime_value(@id)", new { id = b.Customers[0] })).Should().Be(0);
        (await session.ExecuteScalarAsync<long>("SELECT app.customer_lifetime_value(@id)", new { id = a.Customers[0] }))
            .Should().Be(a.Orders.Where(o => o.CustomerId == a.Customers[0]).Sum(o => o.TotalCents));
    }

    [Fact]
    public async Task Copy_to_exports_only_the_callers_rows()
    {
        var (a, b) = await db.TwoTenantsAsync();
        await using var session = await db.Tenants.BeginForTenantAsync(a.Tenant);

        async Task<string[]> ExportAsync(string copy)
        {
            using var reader = await session.Connection.BeginTextExportAsync(copy);
            var lines = new List<string>();
            while (await reader.ReadLineAsync() is { } line)
            {
                lines.Add(line);
            }

            return [.. lines];
        }

        var table = await ExportAsync("COPY app.customers (id, tenant_id) TO STDOUT");
        table.Should().HaveCount(a.Customers.Count).And.OnlyContain(l => l.EndsWith(a.Tenant.ToString(), StringComparison.Ordinal));
        var query = await ExportAsync("COPY (SELECT id FROM app.orders) TO STDOUT");
        query.Should().HaveCount(a.Orders.Count);
        string.Join('\n', table.Concat(query)).Should().NotContain(b.Tenant.ToString());
    }

    [Fact]
    public async Task Copy_from_is_refused_on_rls_tables_so_bulk_loads_cannot_bypass_with_check()
    {
        var (a, b) = await db.TwoTenantsAsync();
        await using var session = await db.Tenants.BeginForTenantAsync(a.Tenant);

        var act = async () =>
        {
            await using var writer = await session.Connection.BeginTextImportAsync("COPY app.customers (id, tenant_id, name, email) FROM STDIN");
            await writer.WriteLineAsync($"{Guid.CreateVersion7()}\t{b.Tenant}\tbulk\tbulk@example.test");
        };
        var error = (await act.Should().ThrowAsync<PostgresException>()).Which;
        error.SqlState.Should().Be(PostgresErrorCodes.FeatureNotSupported);
        error.MessageText.Should().Contain("row-level security");
    }

    [Fact]
    public async Task Per_tenant_counters_reveal_nothing_about_other_tenants_activity()
    {
        var (a, b) = await db.TwoTenantsAsync();
        // Interleave: B creates orders between two of A's. A's numbers stay consecutive; a shared sequence would show a gap.
        async Task<long> NextAsync(TenantId tenant)
        {
            await using var session = await db.Tenants.BeginForTenantAsync(tenant);
            var value = await session.ExecuteScalarAsync<long>("SELECT app.next_number('order')");
            await session.CommitAsync();
            return value;
        }

        var first = await NextAsync(a.Tenant);
        for (var i = 0; i < 5; i++)
        {
            await NextAsync(b.Tenant);
        }

        (await NextAsync(a.Tenant)).Should().Be(first + 1);
    }

    [Fact]
    public async Task Without_a_tenant_nothing_is_visible_and_nothing_can_be_written()
    {
        var (a, _) = await db.TwoTenantsAsync();
        var counts = await db.WithoutTenantAsync(async c => await c.QuerySingleAsync<(long, long, long, long, long)>(
            """
            SELECT (SELECT count(*) FROM app.tenants), (SELECT count(*) FROM app.customers), (SELECT count(*) FROM app.orders),
                   (SELECT count(*) FROM app.tenant_counters), (SELECT count(*) FROM app.customer_order_totals)
            """));
        counts.Should().Be((0L, 0L, 0L, 0L, 0L));

        (await db.WithoutTenantAsync(c => c.ExecuteAsync("UPDATE app.orders SET status = 'paid'"))).Should().Be(0);
        (await db.WithoutTenantAsync(c => c.ExecuteAsync("DELETE FROM app.customers"))).Should().Be(0);

        var insert = () => db.WithoutTenantAsync(c => c.ExecuteAsync(
            "INSERT INTO app.customers (id, name, email) VALUES (@id, 'ghost', 'ghost@example.test')", new { id = Guid.CreateVersion7() }));
        // tenant_id defaults to NULL: the NOT NULL constraint or the policy refuses it, whichever PostgreSQL checks first.
        (await insert.Should().ThrowAsync<PostgresException>()).Which.SqlState
            .Should().BeOneOf(PostgresErrorCodes.NotNullViolation, PostgresErrorCodes.InsufficientPrivilege);

        var planted = () => db.WithoutTenantAsync(c => c.ExecuteAsync(
            "INSERT INTO app.customers (id, tenant_id, name, email) VALUES (@id, @tenant, 'ghost', 'ghost@example.test')",
            new { id = Guid.CreateVersion7(), tenant = a.Tenant.Value }));
        (await planted.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task A_malformed_tenant_value_raises_instead_of_matching_anything()
    {
        await db.TwoTenantsAsync();
        var act = () => db.WithoutTenantAsync(async c =>
        {
            await using var tx = await c.BeginTransactionAsync();
            await c.ExecuteAsync("SELECT set_config('app.tenant_id', 'not-a-uuid', true)", transaction: tx);
            return await c.ExecuteScalarAsync<long>("SELECT count(*) FROM app.customers", transaction: tx);
        });
        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InvalidTextRepresentation);
    }

    [Fact]
    public async Task User_predicates_cannot_probe_hidden_rows_through_errors()
    {
        var (a, b) = await db.TwoTenantsAsync();
        await using var session = await db.Tenants.BeginForTenantAsync(a.Tenant);

        // If this predicate ran on B's rows, 1/0 would raise "division by zero" exactly when B has that e-mail. Policies
        // are security-barrier quals: evaluated before any non-leakproof user predicate, so the oracle never fires.
        var rows = await session.QueryAsync<Guid>(
            "SELECT id FROM app.customers WHERE 1 / (CASE WHEN email = @email THEN 0 ELSE 1 END) = 1",
            new { email = b.Emails[0] });
        rows.Should().HaveCount(a.Customers.Count);
    }

    [Fact]
    public async Task Per_tenant_unique_keys_do_not_reveal_other_tenants_values()
    {
        var (a, b) = await db.TwoTenantsAsync();
        await using var session = await db.Tenants.BeginForTenantAsync(a.Tenant);

        // B's e-mail is free for A: no unique violation, hence no "someone else has this address" signal.
        (await session.ExecuteAsync(
            "INSERT INTO app.customers (id, name, email) VALUES (@id, 'same address', @email)",
            new { id = Guid.CreateVersion7(), email = b.Emails[0] })).Should().Be(1);
    }

    [Fact]
    public async Task Cached_generic_plans_do_not_capture_the_tenant()
    {
        var (a, b) = await db.TwoTenantsAsync();
        await using var connection = await db.App.OpenConnectionAsync();
        await using (var mode = new NpgsqlCommand("SET plan_cache_mode = force_generic_plan", connection))
        {
            await mode.ExecuteNonQueryAsync();
        }

        await using var select = new NpgsqlCommand("SELECT count(*) FROM app.orders WHERE total_cents >= $1", connection);
        select.Parameters.Add(new NpgsqlParameter<long> { TypedValue = 0 });
        await select.PrepareAsync();

        async Task<long> CountAs(TenantId tenant)
        {
            await using var tx = await connection.BeginTransactionAsync();
            await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', $1, true)", connection, tx))
            {
                set.Parameters.Add(new NpgsqlParameter<string> { TypedValue = tenant.ToString() });
                await set.ExecuteNonQueryAsync();
            }

            select.Transaction = tx;
            var count = (long)(await select.ExecuteScalarAsync())!;
            await tx.CommitAsync();
            return count;
        }

        // The same prepared, generic plan runs for both tenants: current_setting is evaluated at execution, not at plan time.
        (await CountAs(a.Tenant)).Should().Be(a.Orders.Count);
        (await CountAs(b.Tenant)).Should().Be(b.Orders.Count);
        (await CountAs(a.Tenant)).Should().Be(a.Orders.Count);
    }

    [Fact]
    public async Task One_round_trip_reads_with_set_local_in_an_implicit_transaction_are_isolated()
    {
        var (a, b) = await db.TwoTenantsAsync();
        var rows = await db.Tenants.QueryInImplicitTransactionAsync<Guid>(a.Tenant, "SELECT tenant_id FROM app.orders");
        rows.Should().HaveCount(a.Orders.Count).And.OnlyContain(t => t == a.Tenant.Value);

        // And the setting died with that implicit transaction: the same pooled connection now sees nothing.
        (await db.WithoutTenantAsync(c => c.ExecuteScalarAsync<long>("SELECT count(*) FROM app.orders"))).Should().Be(0);
        (await db.Tenants.QueryInImplicitTransactionAsync<Guid>(b.Tenant, "SELECT tenant_id FROM app.orders")).Should().HaveCount(b.Orders.Count);
    }
}
