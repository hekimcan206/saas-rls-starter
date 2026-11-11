using Dapper;
using Npgsql;
using SaasRls.IntegrationTests.Infrastructure;
using SaasRls.Tenancy.Context;
using SaasRls.Tenancy.Guard;
using SaasRls.Tenancy.Npgsql;

namespace SaasRls.IntegrationTests;

/// <summary>
/// The ways RLS is commonly defeated, each reproduced in a throw-away <c>lab</c> schema next to the safe pattern the
/// real schema uses. Every "Pitfall_" test asserts that the leak really happens, so the documentation is backed by a
/// failing configuration and not by folklore; the paired test shows the fix holding.
/// </summary>
public sealed class RlsPitfallTests(DatabaseFixture db) : IAsyncLifetime
{
    public async ValueTask InitializeAsync()
    {
        await db.SuperuserExecuteAsync("""
            DROP SCHEMA IF EXISTS lab CASCADE;
            CREATE SCHEMA lab AUTHORIZATION saas_migrator;
            GRANT USAGE ON SCHEMA lab TO saas_app;
            """);
    }

    public async ValueTask DisposeAsync() => await db.SuperuserExecuteAsync("DROP SCHEMA IF EXISTS lab CASCADE");

    [Fact]
    public async Task Pitfall_view_owned_by_a_bypassrls_role_leaks_every_tenant_and_security_invoker_fixes_it()
    {
        var (a, b) = await db.TwoTenantsAsync();
        // Created by the superuser, as a migration run with superuser credentials would: the view runs as its owner.
        await db.SuperuserExecuteAsync("""
            CREATE VIEW lab.orders_definer AS SELECT tenant_id, total_cents FROM app.orders;
            CREATE VIEW lab.orders_invoker WITH (security_invoker = true) AS SELECT tenant_id, total_cents FROM app.orders;
            GRANT SELECT ON lab.orders_definer, lab.orders_invoker TO saas_app;
            GRANT SELECT ON app.orders TO saas_app;
            """);

        await using var session = await db.Tenants.BeginForTenantAsync(a.Tenant);
        var leaked = (await session.QueryAsync<Guid>("SELECT DISTINCT tenant_id FROM lab.orders_definer")).ToList();
        leaked.Should().Contain([a.Tenant.Value, b.Tenant.Value], "the view runs with the superuser's rights, which bypass RLS");

        (await session.QueryAsync<Guid>("SELECT DISTINCT tenant_id FROM lab.orders_invoker")).Should().Equal(a.Tenant.Value);

        var report = await RlsGuard.InspectAsync(db.App, ["lab"]);
        report.Violations.Should().Contain(v => v.Code == "VIEW_NOT_INVOKER" && v.Relation == "lab.orders_definer");
        report.Violations.Should().NotContain(v => v.Relation == "lab.orders_invoker");
    }

    [Fact]
    public async Task Pitfall_enable_without_force_lets_the_owner_bypass_and_force_fixes_it()
    {
        var a = await db.CreateTenantAsync("Acme");
        var b = await db.CreateTenantAsync("Globex");
        await using (var migrator = await db.Migrator.OpenConnectionAsync())
        {
            await migrator.ExecuteAsync("""
                CREATE TABLE lab.notes (tenant_id uuid NOT NULL DEFAULT app.current_tenant_id(), body text NOT NULL);
                ALTER TABLE lab.notes ENABLE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON lab.notes USING (tenant_id = app.current_tenant_id());
                GRANT SELECT, INSERT ON lab.notes TO saas_app;
                -- A reporting view the owner created without thinking about RLS.
                CREATE VIEW lab.note_counts AS SELECT tenant_id, count(*) AS notes FROM lab.notes GROUP BY tenant_id;
                GRANT SELECT ON lab.note_counts TO saas_app;
                """);
        }

        foreach (var tenant in new[] { a, b })
        {
            await using var session = await db.Tenants.BeginForTenantAsync(tenant);
            await session.ExecuteAsync("INSERT INTO lab.notes (body) VALUES ('secret')");
            await session.CommitAsync();
        }

        async Task<int> TenantsVisibleThroughViewAsync()
        {
            await using var session = await db.Tenants.BeginForTenantAsync(a);
            return (await session.QueryAsync<Guid>("SELECT tenant_id FROM lab.note_counts")).Count();
        }

        (await TenantsVisibleThroughViewAsync()).Should().Be(2, "without FORCE the view's owner, the table owner, is exempt from the policy");

        await using (var migrator = await db.Migrator.OpenConnectionAsync())
        {
            await migrator.ExecuteAsync("ALTER TABLE lab.notes FORCE ROW LEVEL SECURITY");
        }

        (await TenantsVisibleThroughViewAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Pitfall_security_definer_function_owned_by_a_bypassrls_role_leaks_and_the_guard_reports_it()
    {
        var (a, b) = await db.TwoTenantsAsync();
        await db.SuperuserExecuteAsync("""
            CREATE FUNCTION lab.order_count() RETURNS bigint LANGUAGE sql SECURITY DEFINER
                AS $$ SELECT count(*) FROM app.orders $$;
            GRANT EXECUTE ON FUNCTION lab.order_count() TO saas_app;
            """);

        await using (var session = await db.Tenants.BeginForTenantAsync(a.Tenant))
        {
            (await session.ExecuteScalarAsync<long>("SELECT lab.order_count()"))
                .Should().BeGreaterThanOrEqualTo(a.Orders.Count + b.Orders.Count, "the function runs as the superuser");
        }

        var report = await RlsGuard.InspectAsync(db.App, ["app"]);
        report.IsSafe.Should().BeFalse();
        report.Violations.Should().Contain(v => v.Code == "DEFINER_FUNCTION_BYPASSES_RLS" && v.Relation == "lab.order_count()");
        report.Violations.Should().Contain(v => v.Code == "DEFINER_FUNCTION_NO_SEARCH_PATH" && v.Relation == "lab.order_count()");

        // The real schema's admin functions are definer functions too, but saas_app cannot execute them.
        report.Violations.Should().NotContain(v => v.Relation.StartsWith("admin.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pitfall_a_plain_foreign_key_bypasses_rls_and_answers_does_this_id_exist()
    {
        var a = await db.CreateTenantAsync("Acme");
        var b = await db.CreateTenantAsync("Globex");
        await using (var migrator = await db.Migrator.OpenConnectionAsync())
        {
            await migrator.ExecuteAsync("""
                CREATE TABLE lab.parents (id uuid PRIMARY KEY, tenant_id uuid NOT NULL DEFAULT app.current_tenant_id());
                CREATE TABLE lab.children (id uuid PRIMARY KEY, tenant_id uuid NOT NULL DEFAULT app.current_tenant_id(),
                                           parent_id uuid NOT NULL REFERENCES lab.parents (id));
                ALTER TABLE lab.parents ENABLE ROW LEVEL SECURITY;
                ALTER TABLE lab.parents FORCE ROW LEVEL SECURITY;
                ALTER TABLE lab.children ENABLE ROW LEVEL SECURITY;
                ALTER TABLE lab.children FORCE ROW LEVEL SECURITY;
                CREATE POLICY p ON lab.parents USING (tenant_id = app.current_tenant_id());
                CREATE POLICY p ON lab.children USING (tenant_id = app.current_tenant_id());
                GRANT SELECT, INSERT ON lab.parents, lab.children TO saas_app;
                """);
        }

        var bParent = Guid.CreateVersion7();
        await using (var asB = await db.Tenants.BeginForTenantAsync(b))
        {
            await asB.ExecuteAsync("INSERT INTO lab.parents (id) VALUES (@bParent)", new { bParent });
            await asB.CommitAsync();
        }

        async Task<string> AttachAsync(Guid parent)
        {
            await using var asA = await db.Tenants.BeginForTenantAsync(a);
            try
            {
                await asA.ExecuteAsync("INSERT INTO lab.children (id, parent_id) VALUES (@id, @parent)", new { id = Guid.CreateVersion7(), parent });
                await asA.CommitAsync();
                return "inserted";
            }
            catch (PostgresException ex)
            {
                return ex.SqlState;
            }
        }

        // The referential check runs without RLS: A can point at B's row it cannot see, and the answer differs from a
        // made-up id. The real schema uses a composite (tenant_id, customer_id) key, see TenantIsolationTests.
        (await AttachAsync(bParent)).Should().Be("inserted");
        (await AttachAsync(Guid.NewGuid())).Should().Be(PostgresErrorCodes.ForeignKeyViolation);
    }

    [Fact]
    public async Task Pitfall_a_global_unique_index_answers_does_another_tenant_have_this_value()
    {
        var a = await db.CreateTenantAsync("Acme");
        var b = await db.CreateTenantAsync("Globex");
        await using (var migrator = await db.Migrator.OpenConnectionAsync())
        {
            await migrator.ExecuteAsync("""
                CREATE TABLE lab.users (tenant_id uuid NOT NULL DEFAULT app.current_tenant_id(), email text NOT NULL UNIQUE);
                ALTER TABLE lab.users ENABLE ROW LEVEL SECURITY;
                ALTER TABLE lab.users FORCE ROW LEVEL SECURITY;
                CREATE POLICY p ON lab.users USING (tenant_id = app.current_tenant_id());
                GRANT SELECT, INSERT ON lab.users TO saas_app;
                """);
        }

        await using (var asB = await db.Tenants.BeginForTenantAsync(b))
        {
            await asB.ExecuteAsync("INSERT INTO lab.users (email) VALUES ('ceo@globex.example')");
            await asB.CommitAsync();
        }

        await using var asA = await db.Tenants.BeginForTenantAsync(a);
        (await asA.ExecuteScalarAsync<long>("SELECT count(*) FROM lab.users")).Should().Be(0, "A cannot see B's row");
        var probe = () => asA.ExecuteAsync("INSERT INTO lab.users (email) VALUES ('ceo@globex.example')");
        (await probe.Should().ThrowAsync<PostgresException>()).Which.SqlState
            .Should().Be(PostgresErrorCodes.UniqueViolation, "the unique index still sees every tenant: the error is the oracle");
    }

    [Fact]
    public async Task Pitfall_a_shared_sequence_leaks_how_much_other_tenants_insert()
    {
        var a = await db.CreateTenantAsync("Acme");
        var b = await db.CreateTenantAsync("Globex");
        await using (var migrator = await db.Migrator.OpenConnectionAsync())
        {
            await migrator.ExecuteAsync("""
                CREATE TABLE lab.invoices (id bigserial PRIMARY KEY, tenant_id uuid NOT NULL DEFAULT app.current_tenant_id());
                ALTER TABLE lab.invoices ENABLE ROW LEVEL SECURITY;
                ALTER TABLE lab.invoices FORCE ROW LEVEL SECURITY;
                CREATE POLICY p ON lab.invoices USING (tenant_id = app.current_tenant_id());
                GRANT SELECT, INSERT ON lab.invoices TO saas_app;
                GRANT USAGE ON SEQUENCE lab.invoices_id_seq TO saas_app;
                """);
        }

        async Task<long> InsertAsync(TenantId tenant)
        {
            await using var session = await db.Tenants.BeginForTenantAsync(tenant);
            var id = await session.ExecuteScalarAsync<long>("INSERT INTO lab.invoices DEFAULT VALUES RETURNING id");
            await session.CommitAsync();
            return id;
        }

        var before = await InsertAsync(a);
        for (var i = 0; i < 7; i++)
        {
            await InsertAsync(b);
        }

        var after = await InsertAsync(a);
        (after - before - 1).Should().Be(7, "A reads B's insert volume off its own ids; RLS does not cover sequences");
    }

    [Fact]
    public async Task Residual_channel_planner_statistics_reveal_total_row_counts_but_pg_stats_hides_values()
    {
        var (a, b) = await db.TwoTenantsAsync();
        await db.SuperuserExecuteAsync("ANALYZE app.orders");

        await using var session = await db.Tenants.BeginForTenantAsync(a.Tenant);
        // Not a row, but a number: catalog statistics are not filtered by RLS. Documented in docs/threat-model.md.
        var reltuples = await session.ExecuteScalarAsync<float>("SELECT reltuples FROM pg_class WHERE oid = 'app.orders'::regclass");
        reltuples.Should().BeGreaterThanOrEqualTo(a.Orders.Count + b.Orders.Count);

        // Column statistics (most common values, histograms) would leak actual values; PostgreSQL hides them for tables
        // with RLS from any role subject to the policies.
        (await session.ExecuteScalarAsync<long>("SELECT count(*) FROM pg_stats WHERE schemaname = 'app' AND tablename = 'orders'"))
            .Should().Be(0);
        (await db.SuperuserScalarAsync<long>("SELECT count(*) FROM pg_stats WHERE schemaname = 'app' AND tablename = 'orders'"))
            .Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Pitfall_a_naive_policy_without_nullif_breaks_after_the_first_transaction_on_a_connection()
    {
        var a = await db.CreateTenantAsync("Acme");
        await using (var migrator = await db.Migrator.OpenConnectionAsync())
        {
            await migrator.ExecuteAsync("""
                CREATE TABLE lab.docs (tenant_id uuid NOT NULL);
                ALTER TABLE lab.docs ENABLE ROW LEVEL SECURITY;
                ALTER TABLE lab.docs FORCE ROW LEVEL SECURITY;
                CREATE POLICY p ON lab.docs USING (tenant_id = current_setting('app.tenant_id', true)::uuid);
                GRANT SELECT ON lab.docs TO saas_app;
                """);
        }

        await using var dataSource = NpgsqlDataSource.Create(db.ConnectionString("saas_app", DatabaseFixture.AppPassword));
        await using var connection = await dataSource.OpenConnectionAsync();

        // A fresh session: the setting was never defined, current_setting(..., true) is NULL and the naive cast works.
        (await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM lab.docs")).Should().Be(0);
        (await connection.ExecuteScalarAsync<string?>("SELECT current_setting('app.tenant_id', true)")).Should().BeNull();

        await using (var tx = await connection.BeginTransactionAsync())
        {
            await connection.ExecuteAsync("SELECT set_config('app.tenant_id', @t, true)", new { t = a.ToString() }, tx);
            await tx.CommitAsync();
        }

        // After SET LOCAL ends the placeholder reads back as '' and ''::uuid raises. app.current_tenant_id() uses nullif.
        (await connection.ExecuteScalarAsync<string?>("SELECT current_setting('app.tenant_id', true)")).Should().BeEmpty();
        var naive = () => connection.ExecuteScalarAsync<long>("SELECT count(*) FROM lab.docs");
        (await naive.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InvalidTextRepresentation);
        (await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM app.customers")).Should().Be(0);
    }
}
