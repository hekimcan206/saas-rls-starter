using SaasRls.Api.Auth;
using SaasRls.Tenancy.Npgsql;
using SaasRls.Tenancy.Resolution;

namespace SaasRls.Api.Api;

public sealed record OrderRequest(Guid CustomerId, long TotalCents);

public sealed record OrderStatusRequest(string? Status);

public sealed record OrderResponse(Guid Id, Guid TenantId, Guid CustomerId, string CustomerName, long Number, string Status, long TotalCents, DateTime CreatedAt);

public sealed record TenantResponse(Guid Id, string Name, string Plan, DateTime CreatedAt);

public sealed record StatusTotal(string Status, long Orders, long TotalCents);

public sealed record CustomerTotal(Guid CustomerId, string Name, long Orders, long TotalCents);

public sealed record SummaryResponse(long Customers, long Orders, long RevenueCents, IReadOnlyList<StatusTotal> ByStatus, IReadOnlyList<CustomerTotal> TopCustomers);

/// <summary>
/// Orders, reports and the current tenant through Dapper on a <see cref="TenantSession"/>. The SQL has no tenant
/// predicates: joins, aggregates, the security_invoker view and the per-tenant counter are all filtered by the policies.
/// </summary>
internal static class OrderEndpoints
{
    private const string OrderColumns = """
        o.id, o.tenant_id AS TenantId, o.customer_id AS CustomerId, c.name AS CustomerName, o.number, o.status,
        o.total_cents AS TotalCents, o.created_at AS CreatedAt
        """;

    public static IEndpointRouteBuilder MapOrderEndpoints(this IEndpointRouteBuilder routes)
    {
        var tenant = routes.MapGroup("/api")
            .RequireAuthorization(Policies.TenantUser)
            .RequireTenant();

        tenant.MapGet("/tenant", async Task<IResult> (TenantDataSource db, CancellationToken ct) =>
        {
            await using var session = await db.BeginAsync(ct);
            var row = await session.QuerySingleOrDefaultAsync<TenantResponse>(
                "SELECT id, name, plan, created_at AS CreatedAt FROM app.tenants", cancellationToken: ct);
            await session.CommitAsync(ct);
            // A tenant id in a valid token whose tenant was purged (or never created) sees no tenant row.
            return row is null ? TypedResults.NotFound() : TypedResults.Ok(row);
        }).WithTags("Tenant");

        var orders = tenant.MapGroup("/orders").WithTags("Orders (Dapper)");

        orders.MapGet("/", async (TenantDataSource db, int? limit, CancellationToken ct) =>
        {
            await using var session = await db.BeginAsync(ct);
            var rows = await session.QueryAsync<OrderResponse>($"""
                SELECT {OrderColumns}
                FROM app.orders o JOIN app.customers c ON c.tenant_id = o.tenant_id AND c.id = o.customer_id
                ORDER BY o.created_at DESC, o.id
                LIMIT @limit
                """, new { limit = Math.Clamp(limit ?? 50, 1, 200) }, ct);
            await session.CommitAsync(ct);
            return TypedResults.Ok(rows);
        });

        orders.MapGet("/{id:guid}", async Task<IResult> (TenantDataSource db, Guid id, CancellationToken ct) =>
        {
            await using var session = await db.BeginAsync(ct);
            var row = await session.QuerySingleOrDefaultAsync<OrderResponse>($"""
                SELECT {OrderColumns}
                FROM app.orders o JOIN app.customers c ON c.tenant_id = o.tenant_id AND c.id = o.customer_id
                WHERE o.id = @id
                """, new { id }, ct);
            await session.CommitAsync(ct);
            return row is null ? TypedResults.NotFound() : TypedResults.Ok(row);
        });

        orders.MapPost("/", async Task<IResult> (TenantDataSource db, OrderRequest request, CancellationToken ct) =>
        {
            if (request.TotalCents is < 0 or > 1_000_000_000_00)
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["totalCents"] = ["Must be between 0 and 100,000,000,000."] });
            }

            await using var session = await db.BeginAsync(ct);
            // Another tenant's customer id fails the composite foreign key (tenant_id, customer_id) exactly like a
            // customer id that does not exist at all: the caller cannot tell the two apart.
            var id = Guid.CreateVersion7();
            await session.ExecuteAsync("""
                INSERT INTO app.orders (id, customer_id, number, total_cents)
                VALUES (@id, @customerId, app.next_number('order'), @totalCents)
                """, new { id, customerId = request.CustomerId, totalCents = request.TotalCents }, ct);
            var row = await session.QuerySingleOrDefaultAsync<OrderResponse>($"""
                SELECT {OrderColumns}
                FROM app.orders o JOIN app.customers c ON c.tenant_id = o.tenant_id AND c.id = o.customer_id
                WHERE o.id = @id
                """, new { id }, ct);
            await session.CommitAsync(ct);
            return TypedResults.Created($"/api/orders/{id}", row);
        });

        orders.MapPatch("/{id:guid}/status", async Task<IResult> (TenantDataSource db, Guid id, OrderStatusRequest request, CancellationToken ct) =>
        {
            if (request.Status is not ("open" or "paid" or "cancelled"))
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["status"] = ["One of: open, paid, cancelled."] });
            }

            await using var session = await db.BeginAsync(ct);
            var updated = await session.ExecuteAsync(
                "UPDATE app.orders SET status = @status WHERE id = @id", new { id, status = request.Status }, ct);
            await session.CommitAsync(ct);
            return updated == 1 ? TypedResults.NoContent() : TypedResults.NotFound();
        });

        tenant.MapGet("/reports/summary", async (TenantDataSource db, CancellationToken ct) =>
        {
            await using var session = await db.BeginAsync(ct);
            var totals = await session.QuerySingleOrDefaultAsync<(long Customers, long Orders, long Revenue)>("""
                SELECT (SELECT count(*) FROM app.customers),
                       (SELECT count(*) FROM app.orders),
                       (SELECT coalesce(sum(total_cents), 0)::bigint FROM app.orders)
                """, cancellationToken: ct);
            var byStatus = await session.QueryAsync<StatusTotal>("""
                SELECT status, count(*) AS Orders, sum(total_cents)::bigint AS TotalCents
                FROM app.orders GROUP BY status ORDER BY status
                """, cancellationToken: ct);
            var top = await session.QueryAsync<CustomerTotal>("""
                SELECT customer_id AS CustomerId, name, orders, total_cents AS TotalCents
                FROM app.customer_order_totals ORDER BY total_cents DESC, customer_id LIMIT 5
                """, cancellationToken: ct);
            await session.CommitAsync(ct);
            return TypedResults.Ok(new SummaryResponse(totals.Customers, totals.Orders, totals.Revenue, [.. byStatus], [.. top]));
        }).WithTags("Reports (Dapper)");

        return routes;
    }
}
