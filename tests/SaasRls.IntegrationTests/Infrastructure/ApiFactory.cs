using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using SaasRls.Api.Auth;

namespace SaasRls.IntegrationTests.Infrastructure;

/// <summary>The real API in memory, connected to the test database with the real roles (or deliberately wrong ones).</summary>
public sealed class ApiFactory(string appConnectionString, string adminConnectionString, string migratorConnectionString) : WebApplicationFactory<Program>
{
    public const string SigningKey = "integration-test-signing-key-0123456789abcdef";

    public static ApiFactory Create(DatabaseFixture db, string? appConnectionString = null) => new(
        appConnectionString ?? db.ConnectionString("saas_app", DatabaseFixture.AppPassword),
        db.ConnectionString("saas_admin", DatabaseFixture.AdminPassword),
        db.ConnectionString("saas_migrator", DatabaseFixture.MigratorPassword));

    public DevTokenIssuer Tokens { get; } = new(new AuthOptions { SigningKey = SigningKey });

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:App", appConnectionString);
        builder.UseSetting("ConnectionStrings:Admin", adminConnectionString);
        builder.UseSetting("ConnectionStrings:Migrator", migratorConnectionString);
        builder.UseSetting("Auth:SigningKey", SigningKey);
        builder.UseSetting("Database:MigrateOnStartup", "false");
        builder.UseSetting("Serilog:MinimumLevel:Default", "Warning");
        builder.UseSetting("Serilog:MinimumLevel:Override:Microsoft.Hosting.Lifetime", "Warning");
    }

    public HttpClient ClientFor(Guid? tenant, string subject = "user@example.test", params string[] roles)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Tokens.Issue(subject, tenant, roles));
        return client;
    }

    public HttpClient AdminClient(string subject = "ops@example.test") => ClientFor(null, subject, Policies.PlatformAdminRole);
}
