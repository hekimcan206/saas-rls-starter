# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [1.0.0] - 2026-11-11

### Added

- PostgreSQL row-level security schema: `ENABLE` + `FORCE ROW LEVEL SECURITY` on every table in schema `app`, one
  `USING` + `WITH CHECK` policy per table on `(SELECT app.current_tenant_id())` (NULL-safe after `SET LOCAL` ends,
  evaluated once per query), composite tenant foreign keys, per-tenant unique keys, UUIDv7 ids and per-tenant
  counters instead of shared sequences, and a `security_invoker` view.
- Least-privilege roles: `saas_migrator` (owner), `saas_app` (no BYPASSRLS, owns nothing, no `UPDATE` on
  `tenant_id`), `saas_admin` (EXECUTE on admin functions only) and the NOLOGIN BYPASSRLS `saas_admin_definer`.
- `SaasRls.Tenancy`: validated `TenantId`, claim and trusted-header resolvers, middleware that refuses tenant endpoints
  without a tenant, `TenantDataSource`/`TenantSession` (BEGIN + `set_config(..., true)` in one round trip) with Dapper
  helpers bound to the transaction, one-round-trip `SET LOCAL` reads, detection of session-level leaks left by other
  code, and `RlsGuard`, which inspects the connected role and tenant schemas.
- `SaasRls.Tenancy.EntityFrameworkCore`: transaction interceptor that sets the tenant on every EF transaction, a guard
  that refuses commands outside a transaction, forced transactions for single-statement saves, and a route-group
  transaction filter.
- ASP.NET Core API: customers through EF Core, orders and reports through Dapper, without a single tenant predicate;
  JWT tenant claims; audited `/admin` endpoints (operator + mandatory reason) over SECURITY DEFINER functions with
  pinned `search_path` and an append-only audit table; refusal to start when the RLS guard fails; OpenTelemetry and
  structured logs.
- Tests: unit and FsCheck property tests, a static migration lint, an isolation suite (read, update, delete, insert
  with a foreign tenant, joins, aggregates, views, functions, COPY, sequences, error oracles, cached plans), an RLS
  pitfall lab, connection-pool and PgBouncer session-state pitfalls shown failing and then fixed, an FsCheck
  random multi-tenant isolation property, API and role tests, all against PostgreSQL 17 and PgBouncer 1.25 in
  Testcontainers.
- `docker compose up` demo with PgBouncer in transaction mode and a self-checking demo client.
- Benchmarks: RLS vs no RLS at 1 and 1,000 tenants, the cost of a cast in the policy, the InitPlan effect, and the
  per-transaction cost of setting the tenant.
