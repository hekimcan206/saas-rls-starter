using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using SaasRls.Tenancy.Guard;

namespace SaasRls.Api.Persistence;

/// <summary>
/// Three logins, three data sources. The API serves tenants as <c>saas_app</c> and cross-tenant admin calls as
/// <c>saas_admin</c>; <c>saas_migrator</c> (the table owner) is used only to apply migrations and never serves requests.
/// </summary>
internal static partial class DatabaseSetup
{
    public const string AppDataSource = "app";
    public const string AdminDataSource = "admin";

    /// <summary>The schemas whose every relation must be protected by row-level security.</summary>
    public static readonly string[] TenantSchemas = ["app"];

    private static readonly string TenantSchemaList = string.Join(",", TenantSchemas);

    public static NpgsqlDataSource CreateDataSource(string connectionString)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        // No Kerberos here; skipping GSS negotiation also stops Npgsql probing for libgssapi, which chiseled images lack.
        builder.ConnectionStringBuilder.GssEncryptionMode = GssEncryptionMode.Disable;
        return builder.Build();
    }

    public static string Required(IConfiguration configuration, string name) =>
        configuration.GetConnectionString(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Connection string '{name}' is not configured.");

    public static async Task MigrateAsync(IConfiguration configuration, ILoggerFactory loggers, CancellationToken cancellationToken = default)
    {
        await using var migrator = CreateDataSource(Required(configuration, "Migrator"));
        await new MigrationRunner(migrator, loggers.CreateLogger<MigrationRunner>()).MigrateAsync(cancellationToken);
    }

    /// <summary>
    /// Refuses to start when the application login could bypass row-level security (superuser, BYPASSRLS, table owner)
    /// or when a tenant table is not protected. Such a misconfiguration produces no error at all otherwise: every
    /// policy is silently skipped and every tenant sees every row.
    /// </summary>
    public static async Task VerifyRlsAsync(WebApplication app)
    {
        var report = await RlsGuard.InspectAsync(app.Services.GetRequiredKeyedService<NpgsqlDataSource>(AppDataSource), TenantSchemas);
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SaasRls.Api.RlsGuard");
        if (report.IsSafe)
        {
            LogGuardPassed(logger, report.Role, TenantSchemaList);
            return;
        }

        foreach (var violation in report.Violations)
        {
            LogGuardViolation(logger, report.Role, violation.Code, violation.Relation, violation.Detail);
        }

        if (app.Configuration.GetValue("Tenancy:RefuseUnsafeDatabase", true))
        {
            throw new InvalidOperationException(
                $"Refusing to start: {report.Violations.Count} RLS guard violation(s) for role '{report.Role}': " +
                string.Join("; ", report.Violations));
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "RLS guard passed for role {Role} on schemas {Schemas}")]
    private static partial void LogGuardPassed(ILogger logger, string role, string schemas);

    [LoggerMessage(Level = LogLevel.Critical, Message = "RLS guard violation for role {Role}: {Code} {Relation}: {Detail}")]
    private static partial void LogGuardViolation(ILogger logger, string role, string code, string relation, string detail);
}

/// <summary>Readiness: the database answers and the RLS guard still passes (catches a role or table altered at runtime).</summary>
internal sealed class RlsReadinessCheck([FromKeyedServices(DatabaseSetup.AppDataSource)] NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var report = await RlsGuard.InspectAsync(dataSource, DatabaseSetup.TenantSchemas, cancellationToken);
            return report.IsSafe
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy($"{report.Violations.Count} RLS guard violation(s).");
        }
        catch (NpgsqlException ex)
        {
            return HealthCheckResult.Unhealthy("Database unavailable.", ex);
        }
    }
}
