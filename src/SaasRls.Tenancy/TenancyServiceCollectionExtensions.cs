using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using SaasRls.Tenancy.Context;
using SaasRls.Tenancy.Npgsql;
using SaasRls.Tenancy.Resolution;

namespace SaasRls.Tenancy;

public static class TenancyServiceCollectionExtensions
{
    /// <summary>
    /// Registers the scoped tenant context, the resolver chosen by <see cref="TenancyOptions.Source"/> and a scoped
    /// <see cref="TenantDataSource"/> over <paramref name="appDataSource"/>, which must connect as the application role.
    /// </summary>
    public static IServiceCollection AddTenancy(
        this IServiceCollection services, NpgsqlDataSource appDataSource, Action<TenancyOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(appDataSource);
        var options = new TenancyOptions();
        configure?.Invoke(options);
        options.Validate();

        services.AddSingleton(options);
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddTenantResolver(options);
        services.AddScoped(sp => new TenantDataSource(
            appDataSource,
            sp.GetRequiredService<ITenantContext>(),
            options,
            sp.GetService<ILogger<TenantDataSource>>()));
        return services;
    }
}
