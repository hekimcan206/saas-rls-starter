using System.Text.RegularExpressions;

namespace SaasRls.Tenancy;

/// <summary>Where the tenant of a request comes from.</summary>
public enum TenantSource
{
    /// <summary>A claim of the authenticated principal (a validated JWT). The default and the only safe choice for
    /// requests that come straight from clients.</summary>
    Claim,

    /// <summary>A request header set by a trusted gateway that has already authenticated the caller. Never expose an
    /// API in this mode to clients directly: anyone could name any tenant.</summary>
    Header,
}

public sealed partial class TenancyOptions
{
    public const string SectionName = "Tenancy";

    public TenantSource Source { get; set; } = TenantSource.Claim;

    public string ClaimType { get; set; } = "tenant_id";

    public string HeaderName { get; set; } = "X-Tenant-Id";

    /// <summary>
    /// The custom PostgreSQL setting the RLS policies read with <c>current_setting(name, true)</c>. A custom setting
    /// name must contain a dot; it is validated because <c>SET LOCAL</c> statements embed it in SQL text.
    /// </summary>
    public string SessionVariable { get; set; } = "app.tenant_id";

    internal void Validate()
    {
        if (!SessionVariablePattern().IsMatch(SessionVariable))
        {
            throw new InvalidOperationException(
                $"Tenancy:SessionVariable '{SessionVariable}' must look like 'prefix.name' (lower-case letters, digits, underscores).");
        }

        if (string.IsNullOrWhiteSpace(ClaimType) || string.IsNullOrWhiteSpace(HeaderName))
        {
            throw new InvalidOperationException("Tenancy:ClaimType and Tenancy:HeaderName must not be empty.");
        }
    }

    [GeneratedRegex(@"^[a-z_][a-z0-9_]*\.[a-z_][a-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SessionVariablePattern();
}
