using System.Diagnostics.CodeAnalysis;

namespace SaasRls.Tenancy.Context;

/// <summary>
/// A tenant identifier. Wrapping the <see cref="Guid"/> keeps "a tenant" and "some other id" from being swapped by
/// accident, and <see cref="TryParse"/> is the single place where untrusted input (a claim, a header) becomes a tenant.
/// </summary>
public readonly record struct TenantId
{
    public TenantId(Guid value)
    {
        if (value == Guid.Empty)
        {
            // The empty GUID is what a missing or defaulted value looks like; accepting it would turn a bug into a
            // tenant that every unconfigured caller shares.
            throw new ArgumentException("The empty GUID is not a tenant id.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    /// <summary>Parses the canonical 36-character form only. Braces, parentheses and the 32-digit form are rejected.</summary>
    public static bool TryParse([NotNullWhen(true)] string? text, out TenantId tenant)
    {
        tenant = default;
        if (text is null || text.Length != 36 || !Guid.TryParseExact(text, "D", out var value) || value == Guid.Empty)
        {
            return false;
        }

        tenant = new TenantId(value);
        return true;
    }

    /// <summary>Canonical lower-case form, exactly what PostgreSQL prints for a <c>uuid</c>.</summary>
    public override string ToString() => Value.ToString("D");
}
