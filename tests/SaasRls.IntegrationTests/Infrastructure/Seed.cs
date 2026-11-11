using Npgsql;
using SaasRls.Tenancy.Context;
using SaasRls.Tenancy.Npgsql;

namespace SaasRls.IntegrationTests.Infrastructure;

public sealed record SeededOrder(Guid Id, Guid CustomerId, long TotalCents);

public sealed record SeededTenant(TenantId Tenant, IReadOnlyList<Guid> Customers, IReadOnlyList<string> Emails, IReadOnlyList<SeededOrder> Orders)
{
    public long TotalCents => Orders.Sum(o => o.TotalCents);
}

public static class Seed
{
    /// <summary>Creates a tenant and inserts its rows through a tenant session as saas_app, the way the API does.</summary>
    public static async Task<SeededTenant> TenantAsync(this DatabaseFixture db, string name, int customers, int ordersPerCustomer, long centsBase = 1000)
    {
        var tenant = await db.CreateTenantAsync(name);
        var customerIds = new List<Guid>();
        var emails = new List<string>();
        var orders = new List<SeededOrder>();
        var tag = tenant.Value.ToString("N")[..8];

        await using var session = await db.Tenants.BeginForTenantAsync(tenant);
        for (var c = 0; c < customers; c++)
        {
            var id = Guid.CreateVersion7();
            var email = $"customer{c}.{tag}@example.test";
            await session.ExecuteAsync(
                "INSERT INTO app.customers (id, name, email) VALUES (@id, @name, @email)",
                new { id, name = $"{name} customer {c}", email });
            customerIds.Add(id);
            emails.Add(email);
            for (var o = 0; o < ordersPerCustomer; o++)
            {
                var orderId = Guid.CreateVersion7();
                var cents = centsBase + (c * 100) + o;
                await session.ExecuteAsync(
                    "INSERT INTO app.orders (id, customer_id, number, total_cents) VALUES (@orderId, @id, app.next_number('order'), @cents)",
                    new { orderId, id, cents });
                orders.Add(new SeededOrder(orderId, id, cents));
            }
        }

        await session.CommitAsync();
        return new SeededTenant(tenant, customerIds, emails, orders);
    }

    public static async Task<(SeededTenant A, SeededTenant B)> TwoTenantsAsync(this DatabaseFixture db) =>
        (await db.TenantAsync("Acme", customers: 3, ordersPerCustomer: 2, centsBase: 1000),
         await db.TenantAsync("Globex", customers: 4, ordersPerCustomer: 3, centsBase: 50_000));

    /// <summary>Runs <paramref name="work"/> on a raw connection as saas_app with NO tenant set (autocommit).</summary>
    public static async Task<T> WithoutTenantAsync<T>(this DatabaseFixture db, Func<NpgsqlConnection, Task<T>> work)
    {
        await using var connection = await db.App.OpenConnectionAsync();
        return await work(connection);
    }
}
