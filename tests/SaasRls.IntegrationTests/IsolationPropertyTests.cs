using Dapper;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using SaasRls.IntegrationTests.Infrastructure;
using SaasRls.Tenancy.Context;
using SaasRls.Tenancy.Npgsql;

namespace SaasRls.IntegrationTests;

/// <summary>
/// FsCheck generates random multi-tenant data sets (2 to 5 tenants, 0 to 6 customers each, 0 to 4 orders per customer,
/// random amounts), loads them through tenant sessions, then queries as every tenant at once, concurrently on the same
/// pool. Property: no query ever returns a row of another tenant, and every count and sum equals the model of that
/// tenant's own data. A failing case is reported with its seed and the generated data set, so it can be replayed.
/// </summary>
public sealed class IsolationPropertyTests(DatabaseFixture db)
{
    public static Arbitrary<int[][][]> DataSets()
    {
        var orders = from n in Gen.Choose(0, 4) from cents in Gen.Choose(0, 100_000).ArrayOf(n) select cents;
        var customers = from n in Gen.Choose(0, 6) from c in orders.ArrayOf(n) select c;
        var tenants = from n in Gen.Choose(2, 5) from t in customers.ArrayOf(n) select t;
        return tenants.ToArbitrary();
    }

    [Property(Arbitrary = [typeof(IsolationPropertyTests)], MaxTest = 30)]
    public bool No_query_ever_returns_a_row_of_another_tenant(int[][][] dataSet) =>
        // Task.Run keeps the async work off any test synchronisation context while FsCheck drives a sync property.
        Task.Run(() => CheckAsync(dataSet)).GetAwaiter().GetResult();

    private sealed record Model(TenantId Tenant, Guid[] Customers, int Orders, long Cents);

    private async Task<bool> CheckAsync(int[][][] dataSet)
    {
        var models = new List<Model>();
        foreach (var tenantData in dataSet)
        {
            models.Add(await LoadAsync(tenantData));
        }

        var allCustomers = models.SelectMany(m => m.Customers).ToArray();
        var checks = await Task.WhenAll(models.Select(model => VerifyAsync(model, allCustomers.Except(model.Customers).ToArray())));
        return checks.All(ok => ok);
    }

    private async Task<Model> LoadAsync(int[][] customers)
    {
        var tenant = await db.CreateTenantAsync("Generated");
        var ids = new List<Guid>();
        await using var session = await db.Tenants.BeginForTenantAsync(tenant);
        foreach (var (orders, index) in customers.Select((o, i) => (o, i)))
        {
            var id = Guid.CreateVersion7();
            ids.Add(id);
            await session.ExecuteAsync("INSERT INTO app.customers (id, name, email) VALUES (@id, 'c', @email)",
                new { id, email = $"c{index}@{tenant}.example" });
            foreach (var cents in orders)
            {
                await session.ExecuteAsync(
                    "INSERT INTO app.orders (id, customer_id, number, total_cents) VALUES (@o, @id, app.next_number('order'), @cents)",
                    new { o = Guid.CreateVersion7(), id, cents = (long)cents });
            }
        }

        await session.CommitAsync();
        return new Model(tenant, [.. ids], customers.Sum(c => c.Length), customers.Sum(c => c.Sum(x => (long)x)));
    }

    private async Task<bool> VerifyAsync(Model model, Guid[] foreignCustomers)
    {
        var me = model.Tenant.Value;
        await using var session = await db.Tenants.BeginForTenantAsync(model.Tenant);

        var customerTenants = (await session.QueryAsync<Guid>("SELECT tenant_id FROM app.customers")).ToList();
        var joined = (await session.QueryAsync<(Guid O, Guid C)>(
            "SELECT o.tenant_id, c.tenant_id FROM app.orders o JOIN app.customers c ON c.id = o.customer_id")).ToList();
        var totals = await session.QuerySingleOrDefaultAsync<(long Count, long Sum)>(
            "SELECT count(*), coalesce(sum(total_cents), 0)::bigint FROM app.orders");
        var view = (await session.QueryAsync<(Guid TenantId, long Cents)>(
            "SELECT tenant_id, total_cents FROM app.customer_order_totals")).ToList();
        var probes = (await session.QueryAsync<Guid>(
            "SELECT id FROM app.customers WHERE id = ANY(@ids)", new { ids = foreignCustomers })).ToList();
        var groups = (await session.QueryAsync<Guid>("SELECT tenant_id FROM app.orders GROUP BY tenant_id")).ToList();
        var tenants = (await session.QueryAsync<Guid>("SELECT id FROM app.tenants")).ToList();

        return customerTenants.Count == model.Customers.Length && customerTenants.All(t => t == me)
            && joined.Count == model.Orders && joined.All(r => r.O == me && r.C == me)
            && totals.Count == model.Orders && totals.Sum == model.Cents
            && view.Count == model.Customers.Length && view.All(r => r.TenantId == me) && view.Sum(r => r.Cents) == model.Cents
            && probes.Count == 0
            && groups.Count <= 1 && groups.All(t => t == me)
            && tenants.Count == 1 && tenants[0] == me;
    }
}
