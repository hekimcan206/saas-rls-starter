namespace SaasRls.Tenancy.Context;

/// <summary>The tenant of the current unit of work (an HTTP request, a message, a job).</summary>
public interface ITenantContext
{
    /// <summary>The resolved tenant, or <see langword="null"/> when none was resolved. Data access then fails closed.</summary>
    TenantId? Current { get; }

    /// <summary>The tenant, or a <see cref="TenantContextMissingException"/> when there is none.</summary>
    TenantId Required => Current ?? throw new TenantContextMissingException();
}

/// <summary>
/// Request-scoped tenant holder. The tenant can be set once per scope: switching tenants half-way through a request is
/// a bug class (a cached connection or entity from the first tenant used for the second), so it is refused.
/// </summary>
public sealed class TenantContext : ITenantContext
{
    public TenantId? Current { get; private set; }

    public void Set(TenantId tenant)
    {
        if (Current is { } existing && existing != tenant)
        {
            throw new InvalidOperationException("The tenant of this scope is already set and cannot be changed.");
        }

        Current = tenant;
    }
}

/// <summary>Thrown when tenant-scoped data access is attempted without a tenant.</summary>
public sealed class TenantContextMissingException : InvalidOperationException
{
    public TenantContextMissingException()
        : base("No tenant is set for this unit of work. Tenant-scoped data access is refused (fail closed).")
    {
    }

    public TenantContextMissingException(string message)
        : base(message)
    {
    }

    public TenantContextMissingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
