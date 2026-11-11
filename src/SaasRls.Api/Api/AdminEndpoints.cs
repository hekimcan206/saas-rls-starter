using Dapper;
using Npgsql;
using SaasRls.Api.Auth;
using SaasRls.Api.Persistence;

namespace SaasRls.Api.Api;

public sealed record CreateTenantRequest(string? Name, string? Plan);

public sealed record TenantUsage(Guid TenantId, string Name, string Plan, long Customers, long Orders, long RevenueCents);

public sealed record AdminOrder(Guid Id, Guid CustomerId, long Number, string Status, long TotalCents, DateTime CreatedAt);

public sealed record AuditEntry(long Id, DateTime At, string DbRole, string Operator, string Action, Guid? TenantId, string Reason, string Detail);

/// <summary>
/// Cross-tenant operations. They connect as <c>saas_admin</c>, which has no table privileges and can only call the
/// audited SECURITY DEFINER functions in schema <c>admin</c>. The API adds the human operator (token subject) and the
/// mandatory reason (X-Admin-Reason); the database refuses the call without them and records both before returning.
/// </summary>
internal static class AdminEndpoints
{
    public const string ReasonHeader = "X-Admin-Reason";

    public static RouteGroupBuilder MapAdminEndpoints(this IEndpointRouteBuilder routes)
    {
        var admin = routes.MapGroup("/admin")
            .WithTags("Admin (audited, cross-tenant)")
            .RequireAuthorization(Policies.PlatformAdmin);

        admin.MapPost("/tenants", async Task<IResult> (HttpContext http, CreateTenantRequest request, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 200)
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["Required, at most 200 characters."] });
            }

            var id = await RunAsync(http, (c, t, reason) => c.ExecuteScalarAsync<Guid>(new CommandDefinition(
                "SELECT admin.create_tenant(@name, @plan, @reason)",
                new { name = request.Name.Trim(), plan = request.Plan, reason }, t, cancellationToken: ct)), ct);
            return TypedResults.Created($"/admin/tenants/{id}", new { id, name = request.Name.Trim(), plan = request.Plan ?? "free" });
        });

        admin.MapGet("/usage", async (HttpContext http, CancellationToken ct) =>
            TypedResults.Ok(await RunAsync(http, async (c, t, reason) => (await c.QueryAsync<TenantUsage>(new CommandDefinition(
                """
                SELECT tenant_id AS TenantId, name, plan, customers, orders, revenue_cents AS RevenueCents
                FROM admin.tenant_usage(@reason)
                """,
                new { reason }, t, cancellationToken: ct))).AsList(), ct)));

        admin.MapGet("/tenants/{id:guid}/orders", async (HttpContext http, Guid id, int? limit, CancellationToken ct) =>
            TypedResults.Ok(await RunAsync(http, async (c, t, reason) => (await c.QueryAsync<AdminOrder>(new CommandDefinition(
                """
                SELECT id, customer_id AS CustomerId, number, status, total_cents AS TotalCents, created_at AS CreatedAt
                FROM admin.tenant_orders(@id, @reason, @limit)
                """,
                new { id, reason, limit = limit ?? 50 }, t, cancellationToken: ct))).AsList(), ct)));

        admin.MapDelete("/tenants/{id:guid}", async (HttpContext http, Guid id, CancellationToken ct) =>
        {
            var deleted = await RunAsync(http, (c, t, reason) => c.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT admin.purge_tenant(@id, @reason)", new { id, reason }, t, cancellationToken: ct)), ct);
            return TypedResults.Ok(new { tenantId = id, rowsDeleted = deleted });
        });

        // Reading the audit trail is itself not audited (it is append-only and contains no tenant data).
        admin.MapGet("/audit", async ([FromKeyedServices(DatabaseSetup.AdminDataSource)] NpgsqlDataSource dataSource, int? limit, CancellationToken ct) =>
        {
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            var rows = await connection.QueryAsync<AuditEntry>(new CommandDefinition(
                """
                SELECT id, at, db_role AS DbRole, operator, action, tenant_id AS TenantId, reason, detail::text AS Detail
                FROM audit.admin_actions ORDER BY id DESC LIMIT @limit
                """,
                new { limit = Math.Clamp(limit ?? 100, 1, 1000) }, cancellationToken: ct));
            return TypedResults.Ok(rows.AsList());
        });

        return admin;
    }

    /// <summary>
    /// One admin transaction: the operator goes into a transaction-local setting the audit function reads, so it cannot
    /// leak to another request on a pooled connection, then the work runs and commits together with its audit row.
    /// </summary>
    private static async Task<T> RunAsync<T>(HttpContext http, Func<NpgsqlConnection, NpgsqlTransaction, string, Task<T>> work, CancellationToken ct)
    {
        var reason = http.Request.Headers[ReasonHeader].ToString();
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new AdminReasonMissingException();
        }

        var dataSource = http.RequestServices.GetRequiredKeyedService<NpgsqlDataSource>(DatabaseSetup.AdminDataSource);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "SELECT set_config('admin.operator', @operator, true)", new { @operator = http.User.Operator() }, transaction, cancellationToken: ct));
        var result = await work(connection, transaction, reason);
        await transaction.CommitAsync(ct);
        return result;
    }
}

internal sealed class AdminReasonMissingException()
    : InvalidOperationException($"Cross-tenant admin calls require the {AdminEndpoints.ReasonHeader} header.");

