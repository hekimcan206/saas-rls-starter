using SaasRls.Tenancy.Context;

namespace SaasRls.Tenancy.Resolution;

public enum TenantResolutionStatus
{
    Resolved,
    Missing,
    Invalid,
}

/// <summary>The outcome of resolving the tenant of a request. Invalid input is kept apart from missing input so the
/// API can answer 400 for a malformed tenant and 401/403 for an absent one.</summary>
public readonly record struct TenantResolution(TenantResolutionStatus Status, TenantId? Tenant, string? Error)
{
    public static TenantResolution Missing { get; } = new(TenantResolutionStatus.Missing, null, null);

    public static TenantResolution Resolved(TenantId tenant) => new(TenantResolutionStatus.Resolved, tenant, null);

    public static TenantResolution Invalid(string error) => new(TenantResolutionStatus.Invalid, null, error);
}
