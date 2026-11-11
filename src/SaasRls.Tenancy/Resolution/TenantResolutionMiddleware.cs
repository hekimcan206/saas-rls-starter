using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SaasRls.Tenancy.Context;

namespace SaasRls.Tenancy.Resolution;

/// <summary>Endpoint metadata: the endpoint touches tenant data and must not run without a tenant.</summary>
public sealed class RequireTenantMetadata
{
    public static RequireTenantMetadata Instance { get; } = new();
}

/// <summary>
/// Resolves the tenant once per request (after authentication) and stores it in the scoped <see cref="TenantContext"/>.
/// Endpoints marked with <see cref="RequireTenantMetadata"/> are short-circuited when no valid tenant was resolved,
/// so a handler never runs "tenant-less". The database would return zero rows anyway; refusing early turns a silent
/// empty page into an explicit error.
/// </summary>
public sealed partial class TenantResolutionMiddleware(RequestDelegate next, ILogger<TenantResolutionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, ITenantResolver resolver, TenantContext tenantContext)
    {
        ArgumentNullException.ThrowIfNull(context);
        var resolution = resolver.Resolve(context);
        if (resolution.Status == TenantResolutionStatus.Resolved)
        {
            tenantContext.Set(resolution.Tenant!.Value);
        }

        var required = context.GetEndpoint()?.Metadata.GetMetadata<RequireTenantMetadata>() is not null;
        if (required && resolution.Status != TenantResolutionStatus.Resolved)
        {
            var invalid = resolution.Status == TenantResolutionStatus.Invalid;
            if (invalid)
            {
                LogInvalidTenant(logger, resolution.Error);
            }

            await Results.Problem(
                    statusCode: invalid ? StatusCodes.Status400BadRequest : StatusCodes.Status403Forbidden,
                    title: invalid ? "Invalid tenant" : "Tenant required",
                    detail: invalid ? resolution.Error : "This endpoint needs a tenant; none was resolved for the caller.")
                .ExecuteAsync(context);
            return;
        }

        await next(context);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected request with an invalid tenant: {Reason}")]
    private static partial void LogInvalidTenant(ILogger logger, string? reason);
}

public static class TenantEndpointExtensions
{
    /// <summary>Marks endpoints as tenant-scoped: they are refused without a resolved tenant.</summary>
    public static TBuilder RequireTenant<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(RequireTenantMetadata.Instance);
        return builder;
    }

    /// <summary>Adds tenant resolution. Call after <c>UseAuthentication</c> and before the endpoints run.</summary>
    public static IApplicationBuilder UseTenantResolution(this IApplicationBuilder app) =>
        app.UseMiddleware<TenantResolutionMiddleware>();

    internal static IServiceCollection AddTenantResolver(this IServiceCollection services, TenancyOptions options) =>
        services.AddSingleton<ITenantResolver>(options.Source switch
        {
            TenantSource.Header => new HeaderTenantResolver(options.HeaderName),
            _ => new ClaimTenantResolver(options.ClaimType),
        });
}
