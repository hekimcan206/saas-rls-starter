using System.Text.RegularExpressions;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using SaasRls.Tenancy;
using SaasRls.Tenancy.Context;
using SaasRls.Tenancy.Npgsql;

namespace SaasRls.UnitTests;

public sealed partial class TenantIdTests
{
    [Theory]
    [InlineData("3f1c2b6e-9d4a-4c1e-8f0a-1b2c3d4e5f60")]
    [InlineData("3F1C2B6E-9D4A-4C1E-8F0A-1B2C3D4E5F60")]
    public void Canonical_uuids_parse(string text)
    {
        TenantId.TryParse(text, out var tenant).Should().BeTrue();
        tenant.ToString().Should().Be(text.ToLowerInvariant());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("{3f1c2b6e-9d4a-4c1e-8f0a-1b2c3d4e5f60}")]
    [InlineData("3f1c2b6e9d4a4c1e8f0a1b2c3d4e5f60")]
    [InlineData(" 3f1c2b6e-9d4a-4c1e-8f0a-1b2c3d4e5f60")]
    [InlineData("3f1c2b6e-9d4a-4c1e-8f0a-1b2c3d4e5f6g")]
    [InlineData("3f1c2b6e-9d4a-4c1e-8f0a-1b2c3d4e5f60'; DROP TABLE app.customers; --")]
    public void Anything_else_is_rejected(string? text) =>
        TenantId.TryParse(text, out _).Should().BeFalse();

    [Fact]
    public void The_empty_guid_is_not_a_tenant() =>
        FluentActions.Invoking(() => new TenantId(Guid.Empty)).Should().Throw<ArgumentException>();

    [Property(MaxTest = 500)]
    public Property Every_non_empty_guid_round_trips() =>
        Prop.ForAll(ArbMap.Default.ArbFor<Guid>().Filter(g => g != Guid.Empty), guid =>
            TenantId.TryParse(new TenantId(guid).ToString(), out var parsed) && parsed.Value == guid);

    [Property(MaxTest = 1000)]
    public Property Arbitrary_strings_never_throw_and_only_canonical_ones_parse() =>
        Prop.ForAll(ArbMap.Default.ArbFor<string>(), text =>
            !TenantId.TryParse(text, out _) || (text.Length == 36 && Guid.TryParseExact(text, "D", out _)));

    // SET cannot take bind parameters, so the one-round-trip helper embeds the tenant in SQL text. The only input is a
    // TenantId, so the literal can only ever contain hex digits and dashes.
    [Property(MaxTest = 500)]
    public Property Set_local_statement_contains_nothing_but_a_canonical_uuid() =>
        Prop.ForAll(ArbMap.Default.ArbFor<Guid>().Filter(g => g != Guid.Empty), guid =>
            SetLocalPattern().IsMatch(TenantDapperExtensions.SetLocalStatement("app.tenant_id", new TenantId(guid))));

    [Theory]
    [InlineData("app.tenant_id", true)]
    [InlineData("tenant.current_1", true)]
    [InlineData("tenant_id", false)]
    [InlineData("app.tenant_id; RESET ALL", false)]
    [InlineData("App.Tenant", false)]
    [InlineData("app.tenant id", false)]
    [InlineData("app..tenant", false)]
    public void Session_variable_names_are_validated(string name, bool valid)
    {
        var options = new TenancyOptions { SessionVariable = name };
        var validate = () => options.Validate();
        if (valid)
        {
            validate.Should().NotThrow();
        }
        else
        {
            validate.Should().Throw<InvalidOperationException>();
        }
    }

    [GeneratedRegex("^SET LOCAL app\\.tenant_id = '[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}';\n$")]
    private static partial Regex SetLocalPattern();
}
