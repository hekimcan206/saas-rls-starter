using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using SaasRls.Api.Api;
using SaasRls.Api.Auth;
using SaasRls.Api.Data;
using SaasRls.Api.Infrastructure;
using SaasRls.Api.Persistence;
using SaasRls.Tenancy;
using SaasRls.Tenancy.EntityFrameworkCore;
using SaasRls.Tenancy.Resolution;
using Scalar.AspNetCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.AddObservability();
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 64 * 1024);

if (args.Contains("--migrate-only"))
{
    // One-shot migration step (compose "migrator" service): connects as the table owner, applies, exits.
    using var loggers = LoggerFactory.Create(l => l.AddSimpleConsole());
    await DatabaseSetup.MigrateAsync(builder.Configuration, loggers);
    return;
}

var appDataSource = DatabaseSetup.CreateDataSource(DatabaseSetup.Required(builder.Configuration, "App"));
var adminDataSource = DatabaseSetup.CreateDataSource(DatabaseSetup.Required(builder.Configuration, "Admin"));
builder.Services.AddKeyedSingleton(DatabaseSetup.AppDataSource, (_, _) => appDataSource);
builder.Services.AddKeyedSingleton(DatabaseSetup.AdminDataSource, (_, _) => adminDataSource);

var tenancy = builder.Configuration.GetSection(TenancyOptions.SectionName);
builder.Services.AddTenancy(appDataSource, options => tenancy.Bind(options));
builder.Services.AddDbContext<SaasDbContext>((services, options) => options
    .UseNpgsql(appDataSource)
    .UseTenantRls(services));

builder.Services.AddSaasAuth(builder.Configuration);
builder.Services.AddHealthChecks().AddCheck<RlsReadinessCheck>("database-rls", tags: [HealthEndpoints.ReadyTag]);

// RFC 9457 problem details for every error, with the trace id so a reported error can be found in traces.
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
    context.ProblemDetails.Extensions.TryAdd("traceId", Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier));
builder.Services.AddExceptionHandler<DatabaseExceptionHandler>();

builder.Services.AddOpenApi("v1", options => options.AddDocumentTransformer((document, _, _) =>
{
    document.Info.Title = "saas-rls-starter";
    document.Info.Version = "v1";
    document.Info.Description = "Multi-tenant SaaS skeleton on PostgreSQL row-level security. Tenant data is isolated by the database, not by WHERE clauses.";
    return Task.CompletedTask;
}));

var app = builder.Build();

if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    await DatabaseSetup.MigrateAsync(app.Configuration, app.Services.GetRequiredService<ILoggerFactory>());
}

await DatabaseSetup.VerifyRlsAsync(app);

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseSerilogRequestLogging(options => options.GetLevel = (context, _, exception) =>
    exception is null && context.Response.StatusCode < 400 && context.Request.Path.StartsWithSegments("/health")
        ? Serilog.Events.LogEventLevel.Verbose
        : Serilog.Events.LogEventLevel.Information);

app.UseAuthentication();
app.UseAuthorization();
// After authorization: an anonymous caller gets 401 from the policy, not "tenant required".
app.UseTenantResolution();

if (app.Configuration.GetValue<bool>("OpenApi:Enabled"))
{
    app.MapOpenApi();
    app.MapScalarApiReference(options => options.WithTitle("saas-rls-starter"));
}

app.MapHealthEndpoints();
app.MapCustomerEndpoints();
app.MapOrderEndpoints();
app.MapAdminEndpoints();
if (app.Services.GetRequiredService<AuthOptions>().EnableDevTokens)
{
    app.MapDevTokenEndpoints();
}

await app.RunAsync();

/// <summary>Entry point; public so WebApplicationFactory can boot the app in integration tests.</summary>
public partial class Program;
