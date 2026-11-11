using SaasRls.Api.Auth;

namespace SaasRls.Api.Api;

public sealed record DevTokenRequest(string? Subject, Guid? TenantId, string[]? Roles);

/// <summary>
/// POST /dev/token: mints a short-lived token with the API's own signing key, so the quick start works with curl alone.
/// Mapped only when Auth:EnableDevTokens is true (Development and the compose demo). In production tokens come from
/// the identity provider and this endpoint does not exist.
/// </summary>
internal static class DevTokenEndpoints
{
    public static IEndpointRouteBuilder MapDevTokenEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/dev/token", (DevTokenIssuer issuer, DevTokenRequest request) =>
            {
                var subject = string.IsNullOrWhiteSpace(request.Subject) ? "dev-user" : request.Subject.Trim();
                var token = issuer.Issue(subject, request.TenantId, request.Roles, TimeSpan.FromMinutes(30));
                return TypedResults.Ok(new { access_token = token, token_type = "Bearer", expires_in = 1800 });
            })
            .WithTags("Development only")
            .AllowAnonymous();
        return routes;
    }
}
