using Microsoft.AspNetCore.Http;
using SaasRls.Tenancy.Context;

namespace SaasRls.Tenancy.Resolution;

public interface ITenantResolver
{
    TenantResolution Resolve(HttpContext context);
}

/// <summary>
/// Reads the tenant from a claim of the authenticated principal. The claim is only trusted when the principal is
/// authenticated, i.e. the token signature, issuer, audience and lifetime were validated by the authentication handler.
/// A principal with several different tenant claims is rejected rather than guessed at.
/// </summary>
public sealed class ClaimTenantResolver(string claimType) : ITenantResolver
{
    public TenantResolution Resolve(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var user = context.User;
        if (user.Identity?.IsAuthenticated != true)
        {
            return TenantResolution.Missing;
        }

        return FromValues(user.FindAll(claimType).Select(c => c.Value).ToArray(), $"claim '{claimType}'");
    }

    internal static TenantResolution FromValues(string[] values, string source)
    {
        var distinct = values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return distinct.Length switch
        {
            0 => TenantResolution.Missing,
            > 1 => TenantResolution.Invalid($"{source} carries {distinct.Length} different tenants."),
            _ => TenantId.TryParse(distinct[0], out var tenant)
                ? TenantResolution.Resolved(tenant)
                : TenantResolution.Invalid($"{source} is not a tenant id (canonical UUID expected)."),
        };
    }
}

/// <summary>
/// Reads the tenant from a header set by a trusted gateway. Only for deployments where the API is reachable solely
/// through that gateway (see <see cref="TenantSource.Header"/>).
/// </summary>
public sealed class HeaderTenantResolver(string headerName) : ITenantResolver
{
    public TenantResolution Resolve(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.Request.Headers.TryGetValue(headerName, out var values) || values.Count == 0)
        {
            return TenantResolution.Missing;
        }

        return ClaimTenantResolver.FromValues(
            values.Where(v => !string.IsNullOrEmpty(v)).Select(v => v!.Trim()).ToArray(),
            $"header '{headerName}'");
    }
}

