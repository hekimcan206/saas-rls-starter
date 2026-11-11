using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace SaasRls.Api.Auth;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    public string Issuer { get; set; } = "saas-rls-starter";

    public string Audience { get; set; } = "saas-rls-starter-api";

    /// <summary>HMAC-SHA256 key for the demo's self-issued tokens. In production, validate tokens from your identity
    /// provider instead (Authority + JWKS); the tenant claim works the same way.</summary>
    public string SigningKey { get; set; } = "";

    /// <summary>Exposes POST /dev/token. For local development and the compose demo only.</summary>
    public bool EnableDevTokens { get; set; }

    internal SymmetricSecurityKey Key()
    {
        var bytes = Encoding.UTF8.GetBytes(SigningKey);
        if (bytes.Length < 32)
        {
            throw new InvalidOperationException("Auth:SigningKey must be at least 32 bytes (256 bits) for HMAC-SHA256.");
        }

        return new SymmetricSecurityKey(bytes);
    }
}

public static class Policies
{
    public const string TenantUser = "tenant-user";
    public const string PlatformAdmin = "platform-admin";
    public const string PlatformAdminRole = "platform_admin";
}

internal static class AuthSetup
{
    public static IServiceCollection AddSaasAuth(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();
        var key = options.Key();
        services.AddSingleton(options);
        services.AddSingleton(new DevTokenIssuer(options));

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(jwt =>
            {
                // Keep claim names as issued ("sub", "tenant_id", "role") instead of the legacy WS-* URIs.
                jwt.MapInboundClaims = false;
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = options.Issuer,
                    ValidAudience = options.Audience,
                    IssuerSigningKey = key,
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    NameClaimType = JwtRegisteredClaimNames.Sub,
                    RoleClaimType = "role",
                    ClockSkew = TimeSpan.FromSeconds(30),
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.TenantUser, policy => policy.RequireAuthenticatedUser())
            .AddPolicy(Policies.PlatformAdmin, policy => policy.RequireAuthenticatedUser().RequireRole(Policies.PlatformAdminRole));
        return services;
    }

    public static string Operator(this ClaimsPrincipal user) =>
        user.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
        ?? throw new InvalidOperationException("Authenticated principal without a subject.");
}

/// <summary>Issues HMAC-signed JWTs with the API's own key: used by tests, the compose demo and POST /dev/token.</summary>
public sealed class DevTokenIssuer(AuthOptions options)
{
    private readonly JsonWebTokenHandler _handler = new();

    public string Issue(string subject, Guid? tenant = null, IEnumerable<string>? roles = null, TimeSpan? lifetime = null)
    {
        var claims = new Dictionary<string, object> { [JwtRegisteredClaimNames.Sub] = subject };
        if (tenant is { } t)
        {
            claims["tenant_id"] = t.ToString("D");
        }

        var roleList = roles?.ToArray() ?? [];
        if (roleList.Length > 0)
        {
            claims["role"] = roleList;
        }

        return _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = options.Issuer,
            Audience = options.Audience,
            Claims = claims,
            Expires = DateTime.UtcNow.Add(lifetime ?? TimeSpan.FromHours(1)),
            SigningCredentials = new SigningCredentials(options.Key(), SecurityAlgorithms.HmacSha256),
        });
    }
}
