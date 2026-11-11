using Npgsql;

namespace SaasRls.Tenancy.Guard;

/// <summary>One way the connected role or the tenant schemas could let rows cross tenants.</summary>
public sealed record RlsViolation(string Code, string Relation, string Detail)
{
    public override string ToString() => $"{Code} {Relation}: {Detail}";
}

public sealed record RlsGuardReport(string Role, IReadOnlyList<RlsViolation> Violations)
{
    public bool IsSafe => Violations.Count == 0;
}

/// <summary>
/// Checks, as the role the application actually connects with, the conditions row-level security depends on. RLS is
/// silently skipped for superusers, for roles with BYPASSRLS and (without FORCE) for table owners, so a misconfigured
/// connection string turns every policy off without a single error. Run at startup and refuse to serve when unsafe.
/// </summary>
public static class RlsGuard
{
    // Every relation in a tenant schema must be protected; global data belongs in another schema. That rule is
    // stricter than "tables with a tenant_id column" and cannot be dodged by naming a column differently.
    private const string Sql = """
        WITH me AS (
            SELECT r.rolname, r.rolsuper, r.rolbypassrls FROM pg_roles r WHERE r.rolname = current_user
        )
        SELECT 'ROLE_SUPERUSER', me.rolname, 'connected role is a superuser: RLS does not apply' FROM me WHERE me.rolsuper
        UNION ALL
        SELECT 'ROLE_BYPASSRLS', me.rolname, 'connected role has BYPASSRLS: RLS does not apply' FROM me WHERE me.rolbypassrls
        UNION ALL
        SELECT 'TABLE_NO_RLS', format('%I.%I', n.nspname, c.relname), 'row level security is not enabled'
        FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = ANY($1) AND c.relkind IN ('r', 'p') AND NOT c.relrowsecurity
        UNION ALL
        SELECT 'TABLE_NO_FORCE', format('%I.%I', n.nspname, c.relname), 'FORCE ROW LEVEL SECURITY is off: the owner bypasses the policies'
        FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = ANY($1) AND c.relkind IN ('r', 'p') AND NOT c.relforcerowsecurity
        UNION ALL
        SELECT 'TABLE_NO_POLICY', format('%I.%I', n.nspname, c.relname), 'no policy: every row is hidden (default deny), probably a forgotten policy'
        FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = ANY($1) AND c.relkind IN ('r', 'p') AND NOT EXISTS (SELECT 1 FROM pg_policy p WHERE p.polrelid = c.oid)
        UNION ALL
        SELECT 'TABLE_OWNED_BY_APP', format('%I.%I', n.nspname, c.relname), format('connected role is (a member of) the owner %I and could ALTER or DISABLE row level security', pg_get_userbyid(c.relowner))
        FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = ANY($1) AND c.relkind IN ('r', 'p', 'v', 'm') AND pg_has_role(current_user, c.relowner, 'MEMBER')
        UNION ALL
        SELECT 'VIEW_NOT_INVOKER', format('%I.%I', n.nspname, c.relname), 'view runs with its owner''s rights; set security_invoker = true'
        FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = ANY($1) AND c.relkind = 'v'
          AND NOT coalesce('security_invoker=true' = ANY(c.reloptions) OR 'security_invoker=on' = ANY(c.reloptions), false)
        UNION ALL
        SELECT 'MATVIEW_IN_TENANT_SCHEMA', format('%I.%I', n.nspname, c.relname), 'materialized views cannot have row level security'
        FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = ANY($1) AND c.relkind = 'm'
        UNION ALL
        SELECT 'DEFINER_FUNCTION_BYPASSES_RLS', format('%I.%I(%s)', n.nspname, p.proname, pg_get_function_identity_arguments(p.oid)),
               format('SECURITY DEFINER function owned by %I (BYPASSRLS or superuser) is executable by the connected role', o.rolname)
        FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace JOIN pg_roles o ON o.oid = p.proowner
        WHERE p.prosecdef AND (o.rolbypassrls OR o.rolsuper)
          AND n.nspname NOT IN ('pg_catalog', 'information_schema')
          AND has_function_privilege(current_user, p.oid, 'EXECUTE')
        UNION ALL
        SELECT 'DEFINER_FUNCTION_NO_SEARCH_PATH', format('%I.%I(%s)', n.nspname, p.proname, pg_get_function_identity_arguments(p.oid)),
               'SECURITY DEFINER function without a pinned search_path can be hijacked with objects in a schema the caller controls'
        FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
        WHERE p.prosecdef AND n.nspname NOT IN ('pg_catalog', 'information_schema')
          AND NOT EXISTS (SELECT 1 FROM unnest(coalesce(p.proconfig, '{}')) cfg WHERE cfg LIKE 'search\_path=%')
        ORDER BY 1, 2
        """;

    public static async Task<RlsGuardReport> InspectAsync(
        NpgsqlDataSource appDataSource, IReadOnlyCollection<string> tenantSchemas, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(appDataSource);
        ArgumentNullException.ThrowIfNull(tenantSchemas);
        await using var connection = await appDataSource.OpenConnectionAsync(cancellationToken);

        string role;
        await using (var who = new NpgsqlCommand("SELECT current_user", connection))
        {
            role = (string)(await who.ExecuteScalarAsync(cancellationToken))!;
        }

        var violations = new List<RlsViolation>();
        await using var command = new NpgsqlCommand(Sql, connection);
        command.Parameters.Add(new NpgsqlParameter<string[]> { TypedValue = [.. tenantSchemas] });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            violations.Add(new RlsViolation(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return new RlsGuardReport(role, violations);
    }
}
