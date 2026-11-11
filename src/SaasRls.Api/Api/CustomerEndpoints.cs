using Microsoft.EntityFrameworkCore;
using SaasRls.Api.Auth;
using SaasRls.Api.Data;
using SaasRls.Tenancy.EntityFrameworkCore;
using SaasRls.Tenancy.Resolution;

namespace SaasRls.Api.Api;

public sealed record CustomerRequest(string? Name, string? Email);

public sealed record CustomerResponse(Guid Id, Guid TenantId, string Name, string Email, DateTime CreatedAt);

/// <summary>
/// Customers through EF Core. Every handler runs in one tenant transaction (WithTenantTransaction), whose first
/// statement sets the tenant; the queries themselves carry no tenant predicate at all. A customer of another tenant is
/// not "forbidden" but simply does not exist for this caller: 404, never 403, so ids cannot be probed.
/// </summary>
internal static class CustomerEndpoints
{
    public static RouteGroupBuilder MapCustomerEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/customers")
            .WithTags("Customers (EF Core)")
            .RequireAuthorization(Policies.TenantUser)
            .RequireTenant()
            .WithTenantTransaction<SaasDbContext>();

        group.MapGet("/", async (SaasDbContext db, int? limit, CancellationToken ct) =>
        {
            var take = Math.Clamp(limit ?? 50, 1, 200);
            var rows = await db.Customers.AsNoTracking()
                .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
                .Take(take)
                .Select(c => new CustomerResponse(c.Id, c.TenantId, c.Name, c.Email, c.CreatedAt))
                .ToListAsync(ct);
            return TypedResults.Ok(rows);
        });

        group.MapGet("/{id:guid}", async Task<IResult> (SaasDbContext db, Guid id, CancellationToken ct) =>
            await db.Customers.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct) is { } c
                ? TypedResults.Ok(ToResponse(c))
                : TypedResults.NotFound());

        group.MapPost("/", async Task<IResult> (SaasDbContext db, CustomerRequest request, CancellationToken ct) =>
        {
            if (Validate(request) is { } problem)
            {
                return problem;
            }

            // The request has no tenant field and none is set here: the column default derives it from the transaction.
            var customer = new Customer { Id = Guid.CreateVersion7(), Name = request.Name!.Trim(), Email = request.Email!.Trim() };
            db.Customers.Add(customer);
            await db.SaveChangesAsync(ct);
            return TypedResults.Created($"/api/customers/{customer.Id}", ToResponse(customer));
        });

        group.MapPut("/{id:guid}", async Task<IResult> (SaasDbContext db, Guid id, CustomerRequest request, CancellationToken ct) =>
        {
            if (Validate(request) is { } problem)
            {
                return problem;
            }

            var customer = await db.Customers.SingleOrDefaultAsync(c => c.Id == id, ct);
            if (customer is null)
            {
                return TypedResults.NotFound();
            }

            customer.Name = request.Name!.Trim();
            customer.Email = request.Email!.Trim();
            await db.SaveChangesAsync(ct);
            return TypedResults.Ok(ToResponse(customer));
        });

        group.MapDelete("/{id:guid}", async Task<IResult> (SaasDbContext db, Guid id, CancellationToken ct) =>
            await db.Customers.Where(c => c.Id == id).ExecuteDeleteAsync(ct) == 1
                ? TypedResults.NoContent()
                : TypedResults.NotFound());

        // A SECURITY INVOKER SQL function: it runs under the caller's policies, so another tenant's id yields 0.
        group.MapGet("/{id:guid}/lifetime-value", async Task<IResult> (SaasDbContext db, Guid id, CancellationToken ct) =>
        {
            if (!await db.Customers.AnyAsync(c => c.Id == id, ct))
            {
                return TypedResults.NotFound();
            }

            var cents = await db.Database
                .SqlQuery<long>($"SELECT app.customer_lifetime_value({id}) AS \"Value\"")
                .SingleAsync(ct);
            return TypedResults.Ok(new { customerId = id, lifetimeValueCents = cents });
        });

        return group;
    }

    private static CustomerResponse ToResponse(Customer c) => new(c.Id, c.TenantId, c.Name, c.Email, c.CreatedAt);

    private static Microsoft.AspNetCore.Http.HttpResults.ValidationProblem? Validate(CustomerRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 200)
        {
            errors["name"] = ["Required, at most 200 characters."];
        }

        if (string.IsNullOrWhiteSpace(request.Email) || request.Email.Trim().Length > 320 || !request.Email.Contains('@', StringComparison.Ordinal))
        {
            errors["email"] = ["A valid e-mail address is required."];
        }

        return errors.Count == 0 ? null : TypedResults.ValidationProblem(errors);
    }
}
