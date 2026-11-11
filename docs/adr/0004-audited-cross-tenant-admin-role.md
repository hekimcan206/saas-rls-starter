# 4. Cross-tenant operations through a separate, audited role

- Status: accepted
- Date: 2026-11-11

## Context

Some work really is cross-tenant: onboarding a tenant, a billing report over all tenants, support access to one
tenant's data, offboarding (erasure). If that work used the owner, a superuser or a BYPASSRLS login from the API,
those credentials would sit next to the application's and could do anything, with no record of who did what or why.

## Options considered

1. **A BYPASSRLS login used by admin endpoints.** Simple, but any SQL goes and nothing is recorded.
2. **Admin policies (`USING (true)` for an admin role).** RLS stays on, but again any SQL goes, and the audit would
   need triggers on every table, which cannot see reads.
3. **A login with no table privileges that can only `EXECUTE` a fixed set of SECURITY DEFINER functions.** The
   functions are owned by a NOLOGIN BYPASSRLS role, and each one writes an audit row in the same transaction.

## Decision

Option 3.

- `saas_admin` (LOGIN) has `USAGE` on schemas `admin` and `audit`, `EXECUTE` on four functions, and `SELECT` on the
  audit table. It has nothing on `app.*` (tested).
- `saas_admin_definer` (NOLOGIN, BYPASSRLS) owns `admin.create_tenant`, `admin.tenant_usage`, `admin.tenant_orders` and
  `admin.purge_tenant`. The migrator creates them with `SET LOCAL ROLE saas_admin_definer`; its membership is
  `WITH INHERIT FALSE, SET TRUE`, so it does not carry the definer's rights into its other statements.
- Every function calls `admin.record(action, tenant, reason, detail)`. It raises unless `admin.operator` (the human
  operator, taken by the API from the validated admin token's `sub` and set transaction-locally) is present and the
  reason has at least 10 characters. It then inserts `session_user` (the login that called), the operator, the action,
  the tenant, the reason and details into `audit.admin_actions`. The insert commits or rolls back with the work.
- `audit.admin_actions` is append-only for everyone except a superuser: no UPDATE/DELETE grants, plus row and
  TRUNCATE triggers that raise even for the owner (tested).
- SECURITY DEFINER hygiene ([CREATE FUNCTION, "Writing SECURITY DEFINER Functions Safely"][definer]): every function has
  `SET search_path = pg_catalog, pg_temp` and schema-qualified references. PUBLIC's default EXECUTE is revoked by
  default privileges and explicitly. Grants are issued by the owner, because a GRANT by a non-owner is only a warning
  and silently grants nothing. `RlsGuard` flags any definer function without a pinned search_path, and any definer
  function owned by a bypassing role that the application role can execute.
- The API exposes these as `/admin/*`, requires the `platform_admin` role claim and an `X-Admin-Reason` header, and
  connects with the separate `ConnectionStrings:Admin`.

## Consequences

- Cross-tenant capabilities are an explicit, reviewable list in one migration. Adding one means writing a function
  that calls `admin.record`, and review can check that.
- Reads are audited too (usage report, support access), which per-table triggers cannot do.
- The audit row records the operator the API asserts. A compromised API process with the admin connection string
  could name any operator. Signing the operator assertion, or using a per-operator database login, is listed as a next
  step in the threat model.
- A superuser can still disable the triggers. That is out of scope here; see the merkle-audit project for tamper
  evidence against an administrator.

[definer]: https://www.postgresql.org/docs/17/sql-createfunction.html#SQL-CREATEFUNCTION-SECURITY
