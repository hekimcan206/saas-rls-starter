using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SaasRls.Tenancy.Context;
using SaasRls.Tenancy.Resolution;

namespace SaasRls.UnitTests;

public sealed class TenantResolutionTests
{
    private const string TenantA = "6d3c1f2a-5b4e-4d6f-9a8b-7c6d5e4f3a21";
    private const string TenantB = "9a8b7c6d-5e4f-4a3b-8c2d-1e0f9a8b7c6d";

    private static DefaultHttpContext Authenticated(params Claim[] claims) =>
        new() { User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Bearer")) };

    [Fact]
    public void Claim_resolver_reads_the_tenant_of_an_authenticated_principal()
    {
        var resolution = new ClaimTenantResolver("tenant_id").Resolve(Authenticated(new Claim("tenant_id", TenantA)));
        resolution.Status.Should().Be(TenantResolutionStatus.Resolved);
        resolution.Tenant!.Value.ToString().Should().Be(TenantA);
    }

    [Fact]
    public void Claim_resolver_ignores_claims_of_an_unauthenticated_principal()
    {
        // An unauthenticated identity is what a token that failed validation leaves behind; its claims prove nothing.
        var context = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("tenant_id", TenantA)])) };
        new ClaimTenantResolver("tenant_id").Resolve(context).Status.Should().Be(TenantResolutionStatus.Missing);
    }

    [Fact]
    public void Claim_resolver_never_reads_the_header()
    {
        var context = Authenticated();
        context.Request.Headers["X-Tenant-Id"] = TenantB;
        new ClaimTenantResolver("tenant_id").Resolve(context).Status.Should().Be(TenantResolutionStatus.Missing);
    }

    [Fact]
    public void Two_different_tenant_claims_are_invalid_not_guessed() =>
        new ClaimTenantResolver("tenant_id")
            .Resolve(Authenticated(new Claim("tenant_id", TenantA), new Claim("tenant_id", TenantB)))
            .Status.Should().Be(TenantResolutionStatus.Invalid);

    [Fact]
    public void Repeated_identical_claims_are_fine() =>
        new ClaimTenantResolver("tenant_id")
            .Resolve(Authenticated(new Claim("tenant_id", TenantA), new Claim("tenant_id", TenantA.ToUpperInvariant())))
            .Status.Should().Be(TenantResolutionStatus.Resolved);

    [Theory]
    [InlineData("not-a-uuid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void Malformed_claims_are_invalid(string value) =>
        new ClaimTenantResolver("tenant_id").Resolve(Authenticated(new Claim("tenant_id", value)))
            .Status.Should().Be(TenantResolutionStatus.Invalid);

    [Fact]
    public void Header_resolver_reads_and_trims_the_header()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Tenant-Id"] = $"  {TenantA} ";
        var resolution = new HeaderTenantResolver("X-Tenant-Id").Resolve(context);
        resolution.Status.Should().Be(TenantResolutionStatus.Resolved);
        resolution.Tenant!.Value.ToString().Should().Be(TenantA);
    }

    [Fact]
    public void Header_resolver_rejects_two_different_values()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Tenant-Id"] = new Microsoft.Extensions.Primitives.StringValues([TenantA, TenantB]);
        new HeaderTenantResolver("X-Tenant-Id").Resolve(context).Status.Should().Be(TenantResolutionStatus.Invalid);
    }

    [Fact]
    public void Header_resolver_without_header_is_missing() =>
        new HeaderTenantResolver("X-Tenant-Id").Resolve(new DefaultHttpContext()).Status.Should().Be(TenantResolutionStatus.Missing);

    [Fact]
    public void Tenant_context_can_be_set_once_but_not_switched()
    {
        var context = new TenantContext();
        TenantId.TryParse(TenantA, out var a).Should().BeTrue();
        TenantId.TryParse(TenantB, out var b).Should().BeTrue();

        context.Set(a);
        context.Set(a);
        FluentActions.Invoking(() => context.Set(b)).Should().Throw<InvalidOperationException>();
        context.Current.Should().Be(a);
    }

    [Fact]
    public void Required_tenant_fails_closed_when_none_is_set() =>
        FluentActions.Invoking(() => ((ITenantContext)new TenantContext()).Required).Should().Throw<TenantContextMissingException>();

    [Theory]
    [InlineData(true, null, 403)]
    [InlineData(true, "garbage", 400)]
    [InlineData(true, TenantA, 200)]
    [InlineData(false, null, 200)]
    public async Task Middleware_refuses_tenant_endpoints_without_a_valid_tenant(bool requireTenant, string? claim, int expectedStatus)
    {
        var context = claim is null ? Authenticated() : Authenticated(new Claim("tenant_id", claim));
        context.RequestServices = new ServiceCollection().AddProblemDetails().AddLogging().BuildServiceProvider();
        context.Response.Body = new MemoryStream();
        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            requireTenant ? new EndpointMetadataCollection(RequireTenantMetadata.Instance) : EndpointMetadataCollection.Empty,
            "test"));

        var nextCalled = false;
        var tenantContext = new TenantContext();
        var middleware = new TenantResolutionMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, NullLogger<TenantResolutionMiddleware>.Instance);

        await middleware.InvokeAsync(context, new ClaimTenantResolver("tenant_id"), tenantContext);

        context.Response.StatusCode.Should().Be(expectedStatus);
        nextCalled.Should().Be(expectedStatus == 200);
        if (claim == TenantA)
        {
            tenantContext.Current!.Value.ToString().Should().Be(TenantA);
        }
    }
}
