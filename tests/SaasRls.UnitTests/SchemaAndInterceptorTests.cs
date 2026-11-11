using System.Text.RegularExpressions;
using Microsoft.IdentityModel.JsonWebTokens;
using Npgsql;
using SaasRls.Api.Auth;
using SaasRls.Api.Persistence;
using SaasRls.Tenancy.Context;
using SaasRls.Tenancy.EntityFrameworkCore;

namespace SaasRls.UnitTests;

public sealed partial class SchemaAndInterceptorTests
{
    [Fact]
    public void Migrations_are_embedded_in_order_with_stable_checksums()
    {
        var migrations = MigrationRunner.Load();
        migrations.Select(m => m.Version).Should().Equal(1, 2);
        migrations.Should().OnlyContain(m => m.Sha256.Length == 64 && !m.Sql.Contains('\r', StringComparison.Ordinal));
    }

    // A static lint over the migrations: a table in the tenant schema without ENABLE + FORCE, or without a policy,
    // fails here before it ever reaches a database. RlsGuard checks the same at runtime.
    [Fact]
    public void Every_table_in_the_tenant_schema_enables_forces_and_has_a_policy()
    {
        var sql = string.Join('\n', MigrationRunner.Load().Select(m => m.Sql));
        var tables = CreateTable().Matches(sql).Select(m => m.Groups[1].Value).ToArray();
        tables.Should().BeEquivalentTo(["tenants", "customers", "orders", "tenant_counters"]);
        foreach (var table in tables)
        {
            sql.Should().Contain($"ALTER TABLE app.{table} ENABLE ROW LEVEL SECURITY;");
            sql.Should().Contain($"ALTER TABLE app.{table} FORCE ROW LEVEL SECURITY;");
            sql.Should().MatchRegex($"CREATE POLICY \\w+ ON app\\.{table}\\b");
        }
    }

    [Fact]
    public void Every_view_in_the_tenant_schema_is_security_invoker()
    {
        var sql = string.Join('\n', MigrationRunner.Load().Select(m => m.Sql));
        var views = CreateView().Matches(sql).ToArray();
        views.Should().NotBeEmpty();
        views.Should().OnlyContain(v => v.Groups[2].Value.Contains("security_invoker = true", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_security_definer_function_pins_its_search_path()
    {
        var sql = string.Join('\n', MigrationRunner.Load().Select(m => m.Sql));
        var definers = DefinerFunction().Matches(sql).ToArray();
        definers.Should().HaveCount(4);
        definers.Should().OnlyContain(d => d.Value.Contains("SET search_path = pg_catalog, pg_temp", StringComparison.Ordinal));
    }

    [Fact]
    public void Command_guard_refuses_commands_outside_a_transaction()
    {
        using var command = new NpgsqlCommand("SELECT 1");
        var guard = new TenantCommandGuardInterceptor();
        FluentActions.Invoking(() => guard.ReaderExecuting(command, null!, default))
            .Should().Throw<TenantContextMissingException>().WithMessage("*outside a transaction*");
        FluentActions.Invoking(() => guard.NonQueryExecuting(command, null!, default)).Should().Throw<TenantContextMissingException>();
        FluentActions.Invoking(() => guard.ScalarExecuting(command, null!, default)).Should().Throw<TenantContextMissingException>();
    }

    [Fact]
    public void Transaction_interceptor_refuses_to_start_without_a_tenant()
    {
        var interceptor = new TenantTransactionInterceptor(new TenantContext(), new SaasRls.Tenancy.TenancyOptions());
        using var connection = new NpgsqlConnection();
        FluentActions.Invoking(() => interceptor.TransactionStarted(connection, null!, null!))
            .Should().Throw<TenantContextMissingException>();
    }

    [Fact]
    public void Dev_tokens_carry_subject_tenant_and_roles()
    {
        var options = new AuthOptions { SigningKey = new string('k', 32) };
        var tenant = Guid.CreateVersion7();
        var token = new JsonWebToken(new DevTokenIssuer(options).Issue("ops@example.test", tenant, [Policies.PlatformAdminRole]));

        token.Subject.Should().Be("ops@example.test");
        token.GetClaim("tenant_id").Value.Should().Be(tenant.ToString("D"));
        token.GetClaim("role").Value.Should().Be(Policies.PlatformAdminRole);
        token.Issuer.Should().Be(options.Issuer);
        token.Alg.Should().Be("HS256");
    }

    [Fact]
    public void Short_signing_keys_are_refused() =>
        FluentActions.Invoking(() => new DevTokenIssuer(new AuthOptions { SigningKey = "too-short" }).Issue("x"))
            .Should().Throw<InvalidOperationException>().WithMessage("*32 bytes*");

    [GeneratedRegex(@"CREATE TABLE app\.(\w+)")]
    private static partial Regex CreateTable();

    [GeneratedRegex(@"CREATE VIEW app\.(\w+)(.*?)AS", RegexOptions.Singleline)]
    private static partial Regex CreateView();

    [GeneratedRegex(@"CREATE FUNCTION [^$]*?SECURITY DEFINER[^$]*?AS \$\$", RegexOptions.Singleline)]
    private static partial Regex DefinerFunction();
}
