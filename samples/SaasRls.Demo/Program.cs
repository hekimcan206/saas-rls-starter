using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Npgsql;
using SaasRls.Api.Api;
using SaasRls.Api.Auth;
using SaasRls.Tenancy;
using SaasRls.Tenancy.Context;
using SaasRls.Tenancy.Npgsql;

// The compose demo: two tenants on one database, isolation checked through the API, then the connection-pool
// pitfalls played for real against PostgreSQL and PgBouncer. Exits 0 only if every expectation holds, including that
// the deliberately broken configurations really leak.
var apiUrl = Environment.GetEnvironmentVariable("API_URL") ?? "http://localhost:58980/";
var signingKey = Environment.GetEnvironmentVariable("AUTH_SIGNING_KEY") ?? "dev-only-signing-key-change-me-0123456789abcdef";
var directDb = Environment.GetEnvironmentVariable("DIRECT_DB") ?? "Host=localhost;Port=58932;Database=saas;Username=saas_app;Password=app-dev-only";
var bouncerDb = Environment.GetEnvironmentVariable("PGBOUNCER_DB") ?? "Host=localhost;Port=58933;Database=saas_tiny;Username=saas_app;Password=app-dev-only";

var checks = new List<(string Name, bool Passed, string Detail)>();
void Check(string name, bool passed, string detail = "")
{
    checks.Add((name, passed, detail));
    Console.WriteLine($"  {(passed ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? $"  ({detail})" : "")}");
}

var tokens = new DevTokenIssuer(new AuthOptions { SigningKey = signingKey });
using var http = new HttpClient { BaseAddress = new Uri(apiUrl), Timeout = TimeSpan.FromSeconds(30) };
HttpClient Client(Guid? tenant, params string[] roles)
{
    var client = new HttpClient { BaseAddress = http.BaseAddress, Timeout = http.Timeout };
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.Issue("demo@example.test", tenant, roles));
    return client;
}

await WaitForApiAsync(http);

Console.WriteLine("== 1. Onboarding through the audited admin path (saas_admin, SECURITY DEFINER functions)");
using var admin = Client(null, Policies.PlatformAdminRole);
admin.DefaultRequestHeaders.Add(AdminEndpoints.ReasonHeader, "compose demo onboarding");
var acme = await CreateTenantAsync(admin, "Acme Lojistik");
var globex = await CreateTenantAsync(admin, "Globex Gıda");
Console.WriteLine($"  tenants: Acme {acme}, Globex {globex}");

Console.WriteLine("== 2. Each tenant writes its own data (EF Core customers, Dapper orders; no tenant in any request body)");
using var a = Client(acme);
using var b = Client(globex);
var aCustomers = new List<Guid>();
var bCustomers = new List<Guid>();
for (var i = 0; i < 3; i++)
{
    aCustomers.Add(await CreateCustomerAsync(a, $"Acme customer {i}"));
    bCustomers.Add(await CreateCustomerAsync(b, $"Globex customer {i}"));
    await a.PostAsJsonAsync("/api/orders", new { customerId = aCustomers[i], totalCents = 10_000 + i });
    await b.PostAsJsonAsync("/api/orders", new { customerId = bCustomers[i], totalCents = 900_000 + i });
}

Console.WriteLine("== 3. Tenant A can't see B");
var aList = await a.GetFromJsonAsync<CustomerResponse[]>("/api/customers");
Check("A lists only its 3 customers", aList!.Length == 3 && aList.All(c => c.TenantId == acme));
Check("A GET of B's customer is 404", (await a.GetAsync($"/api/customers/{bCustomers[0]}")).StatusCode == HttpStatusCode.NotFound);
Check("A PUT of B's customer is 404",
    (await a.PutAsJsonAsync($"/api/customers/{bCustomers[0]}", new { name = "hijack", email = "x@example.test" })).StatusCode == HttpStatusCode.NotFound);
Check("A DELETE of B's customer is 404", (await a.DeleteAsync($"/api/customers/{bCustomers[0]}")).StatusCode == HttpStatusCode.NotFound);
var foreign = await a.PostAsJsonAsync("/api/orders", new { customerId = bCustomers[0], totalCents = 1 });
var missing = await a.PostAsJsonAsync("/api/orders", new { customerId = Guid.NewGuid(), totalCents = 1 });
Check("order for B's customer looks exactly like a missing customer", foreign.StatusCode == missing.StatusCode && foreign.StatusCode == HttpStatusCode.UnprocessableEntity);
var planted = await a.PostAsJsonAsync("/api/customers", new { name = "planted", email = $"planted.{Guid.NewGuid():N}@example.test", tenantId = globex });
Check("tenantId in the body is ignored", (await planted.Content.ReadFromJsonAsync<CustomerResponse>())!.TenantId == acme);
using (var spoof = new HttpRequestMessage(HttpMethod.Get, "/api/tenant"))
{
    spoof.Headers.Add("X-Tenant-Id", globex.ToString());
    var tenant = await (await a.SendAsync(spoof)).Content.ReadFromJsonAsync<TenantResponse>();
    Check("X-Tenant-Id header spoofing is ignored in claim mode", tenant!.Id == acme);
}

var summary = await a.GetFromJsonAsync<SummaryResponse>("/api/reports/summary");
Check("A's report aggregates only A's orders", summary!.Orders == 3 && summary.RevenueCents == 30_003, $"{summary.Orders} orders, {summary.RevenueCents} cents");
using (var anonymous = new HttpClient { BaseAddress = http.BaseAddress })
{
    Check("no token: 401", (await anonymous.GetAsync("/api/customers")).StatusCode == HttpStatusCode.Unauthorized);
}

using (var noTenant = Client(null))
{
    Check("token without tenant: 403", (await noTenant.GetAsync("/api/customers")).StatusCode == HttpStatusCode.Forbidden);
}

Console.WriteLine("== 4. Connection-pool pitfalls, for real");
var options = new TenancyOptions();
var acmeId = new TenantId(acme);
await using (var leaky = Pool(directDb, b => { b.MaxPoolSize = 1; b.NoResetOnClose = true; }))
{
    await using (var c = await leaky.OpenConnectionAsync())
    {
        await c.ExecuteAsync("SELECT set_config('app.tenant_id', @t, false)", new { t = acme.ToString() });
    }

    await using (var c = await leaky.OpenConnectionAsync())
    {
        var seen = await c.ExecuteScalarAsync<long>("SELECT count(*) FROM app.customers");
        Check("BROKEN: session SET + No Reset On Close leaks A to the next request", seen == 4, $"next request saw {seen} customers");
    }
}

await using (var reset = Pool(directDb, b => b.MaxPoolSize = 1))
{
    await using (var c = await reset.OpenConnectionAsync())
    {
        await c.ExecuteAsync("SELECT set_config('app.tenant_id', @t, false)", new { t = acme.ToString() });
    }

    await using (var c = await reset.OpenConnectionAsync())
    {
        var seen = await c.ExecuteScalarAsync<long>("SELECT count(*) FROM app.customers");
        Check("FIXED: Npgsql's reset on return (DISCARD ALL) clears it", seen == 0, $"next request saw {seen}");
    }
}

await using (var bouncerA = Pool(bouncerDb, b => { b.Pooling = false; b.NoResetOnClose = true; }))
await using (var bouncerB = Pool(bouncerDb, b => { b.Pooling = false; b.NoResetOnClose = true; }))
{
    await using (var c = await bouncerA.OpenConnectionAsync())
    {
        await c.ExecuteAsync("SELECT set_config('app.tenant_id', @t, false)", new { t = acme.ToString() });
    }

    await using (var c = await bouncerB.OpenConnectionAsync())
    {
        var seen = await c.ExecuteScalarAsync<long>("SELECT count(*) FROM app.customers");
        Check("BROKEN: PgBouncer transaction mode hands A's session SET to another client", seen == 4, $"client B saw {seen}");
        await c.ExecuteAsync("DISCARD ALL");
    }

    var tenants = new TenantDataSource(bouncerA, new TenantContext(), options);
    await using (var session = await tenants.BeginForTenantAsync(acmeId))
    {
        await session.ExecuteScalarAsync<long>("SELECT count(*) FROM app.customers");
        await session.CommitAsync();
    }

    await using (var c = await bouncerB.OpenConnectionAsync())
    {
        var seen = await c.ExecuteScalarAsync<long>("SELECT count(*) FROM app.customers");
        Check("FIXED: SET LOCAL through PgBouncer leaves nothing behind", seen == 0, $"client B saw {seen}");
    }
}

Console.WriteLine("== 5. Cross-tenant work is audited");
var usage = await admin.GetFromJsonAsync<TenantUsage[]>("/admin/usage");
Check("admin usage report sees both tenants", usage!.Count(u => u.TenantId == acme || u.TenantId == globex) == 2);
var audit = await admin.GetFromJsonAsync<AuditEntry[]>("/admin/audit?limit=20") ?? [];
Check("every admin call left an audit row with operator and reason",
    audit.Count(e => e.Reason == "compose demo onboarding") >= 3 && audit.All(e => e.Operator.Length > 0));
foreach (var entry in audit.Take(4))
{
    Console.WriteLine($"    #{entry.Id} {entry.Action,-14} by {entry.Operator} as {entry.DbRole}: \"{entry.Reason}\"");
}

var failures = checks.Where(c => !c.Passed).Select(c => c.Name).ToArray();
Console.WriteLine(JsonSerializer.Serialize(new { checks = checks.Count, passed = checks.Count - failures.Length, failures }));
return failures.Length == 0 ? 0 : 1;

static async Task WaitForApiAsync(HttpClient http)
{
    for (var attempt = 0; attempt < 60; attempt++)
    {
        try
        {
            if ((await http.GetAsync("/health/ready")).IsSuccessStatusCode)
            {
                return;
            }
        }
        catch (HttpRequestException)
        {
            // Not up yet.
        }

        await Task.Delay(1000);
    }

    throw new TimeoutException("The API did not become ready within 60 s.");
}

static async Task<Guid> CreateTenantAsync(HttpClient admin, string name)
{
    var response = await admin.PostAsJsonAsync("/admin/tenants", new { name, plan = "pro" });
    response.EnsureSuccessStatusCode();
    return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
}

static async Task<Guid> CreateCustomerAsync(HttpClient client, string name)
{
    var response = await client.PostAsJsonAsync("/api/customers", new { name, email = $"{Guid.NewGuid():N}@example.test" });
    response.EnsureSuccessStatusCode();
    return (await response.Content.ReadFromJsonAsync<CustomerResponse>())!.Id;
}

static NpgsqlDataSource Pool(string connectionString, Action<NpgsqlConnectionStringBuilder> configure)
{
    // No Kerberos: skipping GSS negotiation also stops Npgsql probing for libgssapi, which chiseled images lack.
    var builder = new NpgsqlConnectionStringBuilder(connectionString) { GssEncryptionMode = GssEncryptionMode.Disable };
    configure(builder);
    return NpgsqlDataSource.Create(builder.ConnectionString);
}
