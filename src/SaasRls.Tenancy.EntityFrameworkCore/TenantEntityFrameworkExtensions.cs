using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SaasRls.Tenancy.Context;

namespace SaasRls.Tenancy.EntityFrameworkCore;

public static class TenantEntityFrameworkExtensions
{
    /// <summary>
    /// Adds the tenant interceptors to a DbContext registered with
    /// <c>AddDbContext((sp, options) =&gt; options.UseNpgsql(...).UseTenantRls(sp))</c>. The DbContext must be scoped
    /// (the default) because the interceptors read the scoped tenant context.
    /// </summary>
    public static DbContextOptionsBuilder UseTenantRls(this DbContextOptionsBuilder builder, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(services);
        return builder.AddInterceptors(
            new TenantTransactionInterceptor(services.GetRequiredService<ITenantContext>(), services.GetRequiredService<TenancyOptions>()),
            new TenantCommandGuardInterceptor(),
            new TenantSaveChangesInterceptor());
    }

    /// <summary>Runs <paramref name="work"/> in a tenant transaction, committing on success. Reuses an active transaction.</summary>
    public static async Task<T> InTenantTransactionAsync<T>(this DbContext db, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(work);
        if (db.Database.CurrentTransaction is not null)
        {
            return await work(cancellationToken);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var result = await work(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    /// <summary>
    /// Runs every handler of the endpoint (group) in one tenant transaction on <typeparamref name="TContext"/>:
    /// committed when the handler returns a non-error result, rolled back on an error status or an exception.
    /// </summary>
    public static RouteGroupBuilder WithTenantTransaction<TContext>(this RouteGroupBuilder builder)
        where TContext : DbContext =>
        builder.AddEndpointFilter(async (invocation, next) =>
        {
            var db = invocation.HttpContext.RequestServices.GetRequiredService<TContext>();
            var cancellationToken = invocation.HttpContext.RequestAborted;
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var result = await next(invocation);
            if (result is not IStatusCodeHttpResult { StatusCode: >= 400 })
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return result;
        });
}
