using System.Net.Http.Json;
using Dapper;
using Npgsql;
using SaasRls.Api.Api;
using SaasRls.IntegrationTests.Infrastructure;
using SaasRls.Tenancy.Guard;

namespace SaasRls.IntegrationTests;

/// <summary>The role design itself: who can do what, and the startup guard that refuses a dangerous connection string.</summary>
public sealed class DatabaseRoleTests(DatabaseFixture db)
{
    [Fact]
    public async Task The_application_role_passes_the_guard()
    {
        var report = await RlsGuard.InspectAsync(db.App, ["app"]);
        report.Role.Should().Be("saas_app");
        report.Violations.Should().BeEmpty();
    }

    [Fact]
    public async Task The_owner_and_the_superuser_fail_the_guard()
    {
        var owner = await RlsGuard.InspectAsync(db.Migrator, ["app"]);
        owner.Violations.Should().Contain(v => v.Code == "TABLE_OWNED_BY_APP" && v.Relation == "app.customers");

        var superuser = await RlsGuard.InspectAsync(db.Superuser, ["app"]);
        superuser.Violations.Select(v => v.Code).Should().Contain(["ROLE_SUPERUSER", "ROLE_BYPASSRLS"]);
    }

    [Theory]
    [InlineData("saas_migrator", DatabaseFixture.MigratorPassword, "TABLE_OWNED_BY_APP")]
    [InlineData(null, null, "ROLE_SUPERUSER")]
    public async Task The_api_refuses_to_start_with_a_role_that_bypasses_rls(string? user, string? password, string expected)
    {
        await using var api = ApiFactory.Create(db, db.ConnectionString(user, password));
        var start = () => api.CreateClient();
        start.Should().Throw<InvalidOperationException>().WithMessage($"*Refusing to start*{expected}*");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task The_admin_login_has_no_table_access_and_the_app_login_no_admin_access()
    {
        await using (var admin = await db.Admin.OpenConnectionAsync())
        {
            var read = () => admin.ExecuteScalarAsync<long>("SELECT count(*) FROM app.customers");
            (await read.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        }

        await using var app = await db.App.OpenConnectionAsync();
        var usage = () => app.ExecuteAsync("SELECT * FROM admin.tenant_usage('trying to bypass rls')");
        (await usage.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        var audit = () => app.ExecuteAsync("SELECT * FROM audit.admin_actions");
        (await audit.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Admin_functions_refuse_to_run_without_an_operator()
    {
        await using var admin = await db.Admin.OpenConnectionAsync();
        var act = () => admin.ExecuteAsync("SELECT * FROM admin.tenant_usage('a perfectly good reason')");
        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InvalidAuthorizationSpecification);
    }

    [Fact]
    public async Task The_audit_trail_is_append_only_even_for_its_owner()
    {
        await db.CreateTenantAsync("Audited");
        await using var owner = await db.Migrator.OpenConnectionAsync();
        var update = () => owner.ExecuteAsync("UPDATE audit.admin_actions SET reason = 'nothing to see'");
        (await update.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("append-only");
        var delete = () => owner.ExecuteAsync("DELETE FROM audit.admin_actions");
        (await delete.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("append-only");
        var truncate = () => owner.ExecuteAsync("TRUNCATE audit.admin_actions");
        (await truncate.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("append-only");
    }

    [Fact]
    public async Task The_api_serves_tenants_through_pgbouncer_in_transaction_mode()
    {
        // The compose stack runs the API behind PgBouncer; this is the same wiring, under concurrent load.
        await using var api = ApiFactory.Create(db, db.PgBouncerConnectionString("saas"));
        var a = await db.CreateTenantAsync("Acme");
        var b = await db.CreateTenantAsync("Globex");
        var clients = new[] { (Tenant: a.Value, Client: api.ClientFor(a.Value)), (Tenant: b.Value, Client: api.ClientFor(b.Value)) };
        foreach (var (_, client) in clients)
        {
            (await client.PostAsJsonAsync("/api/customers", new { name = "c", email = $"c.{Guid.NewGuid():N}@example.test" })).EnsureSuccessStatusCode();
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 200).Select(async i =>
        {
            var (tenant, client) = clients[i % 2];
            var rows = await client.GetFromJsonAsync<CustomerResponse[]>("/api/customers");
            return rows!.All(r => r.TenantId == tenant) && rows!.Length == 1;
        }));
        results.Should().OnlyContain(ok => ok);
    }
}
