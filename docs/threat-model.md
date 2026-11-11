# Threat model

Scope: the tenant isolation boundary of saas-rls-starter (API, PostgreSQL schema and roles, PgBouncer) as shipped in
this repository. All data is synthetic.

## Assets

| Asset | Why it matters |
|---|---|
| Tenant rows (`app.*`) | Each tenant's customers and orders must be invisible and immutable to every other tenant |
| Existence and volume of other tenants' data | Even "does B have customer X" or "how many orders did B create today" is a leak |
| Database credentials (`saas_app`, `saas_admin`, `saas_migrator`) | Each login's privileges bound what a compromise can do |
| JWT signing key | Whoever holds it can mint a token for any tenant |
| Admin audit trail (`audit.admin_actions`) | The only record of cross-tenant access |

## Actors

- **Tenant user**: authenticated, legitimate for one tenant, possibly malicious towards others (id guessing, forged
  body fields, spoofed headers, crafted predicates through any future query surface).
- **Developer mistake**: a forgotten predicate, a Dapper call without the transaction, a session-level `SET`, a view
  or SECURITY DEFINER function written without RLS in mind, a migration run as superuser.
- **Platform operator**: legitimately needs cross-tenant access, which must be deliberate and recorded.
- **Attacker with the API's database credentials** (SQL injection, leaked config).
- **Database superuser**: out of scope as an adversary (see Accepted risks).

## Threats and mitigations (STRIDE)

| # | Threat | Mitigation | Evidence |
|---|---|---|---|
| S1 | Spoofing a tenant via a header or body field | Claim mode reads only the validated JWT claim; request DTOs have no tenant field; `tenant_id` comes from the column default under the transaction's setting | `A_tenant_id_in_the_body_or_a_header_is_ignored`, unit tests |
| S2 | Token for a tenant forged or tampered | HS256 with validated issuer, audience, lifetime and algorithm list; keys under 32 bytes refused; production should use the IdP's JWKS | `Requests_without_a_valid_tenant_never_reach_a_handler` |
| T1 | Writing a row into another tenant | `WITH CHECK` on every policy; `UPDATE` grants exclude `tenant_id`; composite FKs | `Insert_with_a_foreign_tenant_id_is_rejected_by_with_check`, `Update_cannot_move_a_row_to_another_tenant` |
| T2 | Modifying or deleting another tenant's rows | `USING` filters UPDATE/DELETE targets | `Update_cannot_touch_...`, `Delete_cannot_touch_...` |
| T3 | Bulk load bypassing checks | `COPY FROM` is refused by PostgreSQL on RLS tables for subject roles | `Copy_from_is_refused_on_rls_tables...` |
| I1 | Forgotten tenant predicate | No predicates needed: RLS adds them; FsCheck property over random data sets | `TenantIsolationTests`, `IsolationPropertyTests` |
| I2 | Joins, aggregates, views, functions, COPY TO returning other tenants' rows | Policies apply per table in every query; views are `security_invoker`; functions are SECURITY INVOKER | isolation suite |
| I3 | Session state leaking between requests on pooled connections or through PgBouncer | Tenant set only with `set_config(..., true)`; leaks by other code detected and logged | `ConnectionPoolPitfallTests`, `PgBouncerTests` |
| I4 | Connecting as a role that bypasses RLS (owner, superuser, BYPASSRLS) | `RlsGuard` at startup and in readiness; API refuses to start | `The_api_refuses_to_start_with_a_role_that_bypasses_rls`, compose CI step |
| I5 | Definer views and functions running with bypassing rights | Guard flags non-invoker views in tenant schemas and executable definer functions owned by bypassing roles | `RlsPitfallTests` |
| I6 | Existence oracles through FK and unique checks | Composite FKs, per-tenant unique keys, coarse error mapping (404/422) | ADR 0006, pitfall and API tests |
| I7 | Volume oracle through shared sequences | UUIDv7 ids, per-tenant counters | `Per_tenant_counters_reveal_nothing...` |
| I8 | Error-based probing of hidden rows | Policies are security-barrier quals evaluated before user predicates | `User_predicates_cannot_probe_hidden_rows_through_errors` |
| I9 | Cached plans reused across tenants | `current_setting` is evaluated at execution; generic plans tested | `Cached_generic_plans_do_not_capture_the_tenant` |
| D1 | Unbounded queries | Page sizes clamped (200 tenant, 1,000 admin), 64 KiB request bodies | code |
| E1 | Application credentials used for cross-tenant access | `saas_app` cannot execute admin functions or read the audit table | `The_admin_login_has_no_table_access_and_the_app_login_no_admin_access` |
| E2 | Admin access without accountability | Admin login can only call definer functions that require an operator and a reason and audit in the same transaction; audit table append-only even for its owner | `Admin_endpoints_need_the_admin_role_and_a_reason_and_are_audited`, `The_audit_trail_is_append_only_even_for_its_owner` |
| E3 | search_path hijack of definer functions | `SET search_path = pg_catalog, pg_temp`; guard and a static lint flag violations | unit lint test, guard |
| R1 | Operator denies a cross-tenant action | Audit row with login, operator, reason, action, tenant, details | API admin tests |

## Accepted risks and residual channels

- **Catalog statistics** (`pg_class.reltuples`, `pg_stat_user_tables.n_live_tup`, `EXPLAIN` row estimates) reveal the
  total row count across tenants. Column statistics (`pg_stats`) are hidden by PostgreSQL. Pinned by
  `Residual_channel_planner_statistics_reveal_total_row_counts_but_pg_stats_hides_values`.
- **Timing**: query latency depends on table size, not on other tenants' row contents. Not mitigated.
- **Superuser** can disable RLS, drop the audit triggers and read everything. Mitigated organisationally (no
  superuser credentials in applications). Tamper evidence against administrators is a separate project.
- **Admin operator identity** is asserted by the API process. A compromised API with the admin connection string can
  name any operator. Next step: per-operator database logins or a signed operator assertion verified in SQL.
- **Dev token endpoint** (`POST /dev/token`) exists only when `Auth:EnableDevTokens=true` (Development, compose demo).
  Never enable it elsewhere.
- **Compose PgBouncer** builds its userlist from environment variables, and all compose passwords are development-only
  defaults (`.env.example`). Production: secrets manager, `auth_query`, TLS between all hops.
