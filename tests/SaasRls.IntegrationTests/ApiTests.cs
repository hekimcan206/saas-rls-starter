using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SaasRls.Api.Api;
using SaasRls.Api.Auth;
using SaasRls.IntegrationTests.Infrastructure;

namespace SaasRls.IntegrationTests;

/// <summary>The HTTP surface: tenant resolution, isolation through EF Core and Dapper endpoints, and the admin path.</summary>
public sealed class ApiTests(DatabaseFixture db) : IAsyncLifetime
{
    private ApiFactory _api = null!;

    public ValueTask InitializeAsync()
    {
        _api = ApiFactory.Create(db);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _api.DisposeAsync();

    private async Task<(HttpClient A, HttpClient B, Guid TenantA, Guid TenantB)> TwoTenantClientsAsync()
    {
        var a = await db.CreateTenantAsync("Acme");
        var b = await db.CreateTenantAsync("Globex");
        return (_api.ClientFor(a.Value), _api.ClientFor(b.Value), a.Value, b.Value);
    }

    private static async Task<CustomerResponse> CreateCustomerAsync(HttpClient client, string name, object? extra = null)
    {
        var response = await client.PostAsJsonAsync("/api/customers", extra ?? new { name, email = $"{name.ToLowerInvariant()}.{Guid.NewGuid():N}@example.test" });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<CustomerResponse>())!;
    }

    [Fact]
    public async Task Each_tenant_lists_only_its_own_customers()
    {
        var (a, b, tenantA, tenantB) = await TwoTenantClientsAsync();
        var aCustomer = await CreateCustomerAsync(a, "Ayla");
        var bCustomer = await CreateCustomerAsync(b, "Burak");

        aCustomer.TenantId.Should().Be(tenantA, "the database derived tenant_id from the transaction");
        bCustomer.TenantId.Should().Be(tenantB);
        (await a.GetFromJsonAsync<CustomerResponse[]>("/api/customers")).Should().ContainSingle().Which.Id.Should().Be(aCustomer.Id);
        (await b.GetFromJsonAsync<CustomerResponse[]>("/api/customers")).Should().ContainSingle().Which.Id.Should().Be(bCustomer.Id);
    }

    [Fact]
    public async Task Another_tenants_customer_does_not_exist_for_the_caller()
    {
        var (a, b, _, _) = await TwoTenantClientsAsync();
        var bCustomer = await CreateCustomerAsync(b, "Burak");

        (await a.GetAsync($"/api/customers/{bCustomer.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await a.PutAsJsonAsync($"/api/customers/{bCustomer.Id}", new { name = "hijacked", email = "x@example.test" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await a.DeleteAsync($"/api/customers/{bCustomer.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await a.GetAsync($"/api/customers/{bCustomer.Id}/lifetime-value")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var stillThere = await b.GetFromJsonAsync<CustomerResponse>($"/api/customers/{bCustomer.Id}");
        stillThere!.Name.Should().Be("Burak");
    }

    [Fact]
    public async Task A_tenant_id_in_the_body_or_a_header_is_ignored()
    {
        var (a, _, tenantA, tenantB) = await TwoTenantClientsAsync();

        var planted = await CreateCustomerAsync(a, "Planted", new { name = "Planted", email = $"planted.{Guid.NewGuid():N}@example.test", tenantId = tenantB });
        planted.TenantId.Should().Be(tenantA);

        using var spoof = new HttpRequestMessage(HttpMethod.Get, "/api/tenant");
        spoof.Headers.Add("X-Tenant-Id", tenantB.ToString());
        var response = await a.SendAsync(spoof);
        (await response.Content.ReadFromJsonAsync<TenantResponse>())!.Id.Should().Be(tenantA, "claim mode never reads the header");
    }

    [Theory]
    [InlineData("none", HttpStatusCode.Unauthorized)]
    [InlineData("wrong-key", HttpStatusCode.Unauthorized)]
    [InlineData("no-tenant", HttpStatusCode.Forbidden)]
    [InlineData("bad-tenant", HttpStatusCode.BadRequest)]
    public async Task Requests_without_a_valid_tenant_never_reach_a_handler(string kind, HttpStatusCode expected)
    {
        var client = _api.CreateClient();
        var token = kind switch
        {
            "wrong-key" => new DevTokenIssuer(new AuthOptions { SigningKey = new string('x', 48) }).Issue("eve", Guid.NewGuid()),
            "no-tenant" => _api.Tokens.Issue("user"),
            "bad-tenant" => BadTenantToken(),
            _ => null,
        };
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        (await client.GetAsync("/api/customers")).StatusCode.Should().Be(expected);
    }

    private string BadTenantToken()
    {
        var handler = new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler();
        return handler.CreateToken(new Microsoft.IdentityModel.Tokens.SecurityTokenDescriptor
        {
            Issuer = "saas-rls-starter",
            Audience = "saas-rls-starter-api",
            Claims = new Dictionary<string, object> { ["sub"] = "user", ["tenant_id"] = "not-a-uuid" },
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new Microsoft.IdentityModel.Tokens.SigningCredentials(
                new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(ApiFactory.SigningKey)),
                Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256),
        });
    }

    [Fact]
    public async Task Orders_use_per_tenant_numbers_and_cannot_reference_another_tenants_customer()
    {
        var (a, b, _, _) = await TwoTenantClientsAsync();
        var aCustomer = await CreateCustomerAsync(a, "Ayla");
        var bCustomer = await CreateCustomerAsync(b, "Burak");

        var first = await a.PostAsJsonAsync("/api/orders", new { customerId = aCustomer.Id, totalCents = 1500 });
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        await b.PostAsJsonAsync("/api/orders", new { customerId = bCustomer.Id, totalCents = 99 });
        var second = await a.PostAsJsonAsync("/api/orders", new { customerId = aCustomer.Id, totalCents = 2500 });
        (await first.Content.ReadFromJsonAsync<OrderResponse>())!.Number.Should().Be(1);
        (await second.Content.ReadFromJsonAsync<OrderResponse>())!.Number.Should().Be(2, "B's order did not consume A's numbers");

        var foreign = await a.PostAsJsonAsync("/api/orders", new { customerId = bCustomer.Id, totalCents = 1 });
        var missing = await a.PostAsJsonAsync("/api/orders", new { customerId = Guid.NewGuid(), totalCents = 1 });
        foreign.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        missing.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ProblemTitle(foreign)).Should().Be(await ProblemTitle(missing), "the two cases must be indistinguishable");

        var bOrders = await b.GetFromJsonAsync<OrderResponse[]>("/api/orders");
        (await a.PatchAsJsonAsync($"/api/orders/{bOrders![0].Id}/status", new { status = "paid" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await a.GetAsync($"/api/orders/{bOrders[0].Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Reports_aggregate_only_the_callers_data()
    {
        var (a, b, _, _) = await TwoTenantClientsAsync();
        var aCustomer = await CreateCustomerAsync(a, "Ayla");
        var bCustomer = await CreateCustomerAsync(b, "Burak");
        await a.PostAsJsonAsync("/api/orders", new { customerId = aCustomer.Id, totalCents = 1000 });
        await a.PostAsJsonAsync("/api/orders", new { customerId = aCustomer.Id, totalCents = 2000 });
        await b.PostAsJsonAsync("/api/orders", new { customerId = bCustomer.Id, totalCents = 777_777 });

        var summary = await a.GetFromJsonAsync<SummaryResponse>("/api/reports/summary");
        summary!.Customers.Should().Be(1);
        summary.Orders.Should().Be(2);
        summary.RevenueCents.Should().Be(3000);
        summary.TopCustomers.Should().ContainSingle().Which.CustomerId.Should().Be(aCustomer.Id);
    }

    [Fact]
    public async Task Failed_requests_roll_back_and_constraint_errors_map_to_problem_details()
    {
        var (a, _, _, _) = await TwoTenantClientsAsync();
        (await a.PostAsJsonAsync("/api/customers", new { name = "No email", email = "" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await a.GetFromJsonAsync<CustomerResponse[]>("/api/customers")).Should().BeEmpty();

        var email = $"dup.{Guid.NewGuid():N}@example.test";
        await CreateCustomerAsync(a, "First", new { name = "First", email });
        (await a.PostAsJsonAsync("/api/customers", new { name = "Second", email })).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Admin_endpoints_need_the_admin_role_and_a_reason_and_are_audited()
    {
        var (a, _, tenantA, _) = await TwoTenantClientsAsync();
        await CreateCustomerAsync(a, "Ayla");
        var admin = _api.AdminClient("alice@ops.example");

        (await a.GetAsync("/admin/usage")).StatusCode.Should().Be(HttpStatusCode.Forbidden, "a tenant token is not an admin token");
        (await admin.GetAsync("/admin/usage")).StatusCode.Should().Be(HttpStatusCode.BadRequest, "no reason given");

        using var shortReason = new HttpRequestMessage(HttpMethod.Get, "/admin/usage");
        shortReason.Headers.Add(AdminEndpoints.ReasonHeader, "because");
        (await admin.SendAsync(shortReason)).StatusCode.Should().Be(HttpStatusCode.BadRequest, "the database requires 10+ characters");

        var reason = $"billing reconciliation {Guid.NewGuid():N}";
        using var usageRequest = new HttpRequestMessage(HttpMethod.Get, "/admin/usage");
        usageRequest.Headers.Add(AdminEndpoints.ReasonHeader, reason);
        var usage = await (await admin.SendAsync(usageRequest)).Content.ReadFromJsonAsync<TenantUsage[]>();
        usage.Should().Contain(u => u.TenantId == tenantA && u.Customers == 1);

        var audit = await admin.GetFromJsonAsync<AuditEntry[]>("/admin/audit?limit=50");
        audit.Should().Contain(e => e.Reason == reason && e.Operator == "alice@ops.example" && e.Action == "tenant.usage" && e.DbRole == "saas_admin");
    }

    [Fact]
    public async Task Admin_onboards_and_purges_a_tenant_end_to_end()
    {
        var admin = _api.AdminClient();
        admin.DefaultRequestHeaders.Add(AdminEndpoints.ReasonHeader, "end-to-end onboarding test");

        var created = await admin.PostAsJsonAsync("/admin/tenants", new { name = "Initech", plan = "enterprise" });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var tenantClient = _api.ClientFor(id);
        (await tenantClient.GetFromJsonAsync<TenantResponse>("/api/tenant"))!.Plan.Should().Be("enterprise");
        var customer = await CreateCustomerAsync(tenantClient, "Milton");
        await tenantClient.PostAsJsonAsync("/api/orders", new { customerId = customer.Id, totalCents = 100 });

        var orders = await admin.GetFromJsonAsync<AdminOrder[]>($"/admin/tenants/{id}/orders");
        orders.Should().ContainSingle();

        var purge = await admin.DeleteAsync($"/admin/tenants/{id}");
        (await purge.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rowsDeleted").GetInt64().Should().Be(2);
        (await tenantClient.GetAsync("/api/tenant")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await tenantClient.GetFromJsonAsync<CustomerResponse[]>("/api/customers")).Should().BeEmpty();
        (await admin.DeleteAsync($"/admin/tenants/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Readiness_includes_the_rls_guard()
    {
        var response = await _api.CreateClient().GetAsync("/health/ready");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("database-rls");
    }

    private static async Task<string?> ProblemTitle(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString();
}
