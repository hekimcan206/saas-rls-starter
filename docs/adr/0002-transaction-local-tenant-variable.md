# 2. Tenant context: a transaction-local session variable set from a request-scoped resolver

- Status: accepted
- Date: 2026-11-11

## Context

Policies need to know the tenant of the current request. The API runs on pooled connections, the Npgsql pool and
optionally PgBouncer in transaction mode (ADR 0005), so a physical PostgreSQL session serves many requests and many
tenants over its lifetime. Whatever carries the tenant into the database must not survive from one request into the
next.

## Options considered

1. **Session-level `SET app.tenant_id = ...`** (or `set_config(..., false)`) when the connection is opened. Correct
   only if every connection is reset before reuse. Npgsql does reset (`DISCARD ALL`) unless `No Reset On Close=true`,
   which the Npgsql docs tell you to set behind PgBouncer ([Npgsql compatibility notes][npgsql-compat]). PgBouncer in
   transaction mode never resets a server connection ([PgBouncer configuration][pgb-config]: `server_reset_query`
   is not used in transaction pooling).
2. **One database role per tenant, `SET ROLE` per request.** Policies on `current_user`. Thousands of roles, grants
   on every new table per role, and `SET ROLE` is session state too, so it has the same leak problem.
3. **Transaction-local variable: `set_config('app.tenant_id', $tenant, is_local => true)`** (the function form of
   `SET LOCAL`), sent as the first statement of every transaction. Policies read
   `current_setting('app.tenant_id', true)`.

## Decision

Option 3, with the tenant resolved once per request:

- `ClaimTenantResolver` (default) takes the `tenant_id` claim of the *authenticated* principal, i.e. a JWT whose
  signature, issuer, audience and lifetime were validated. `HeaderTenantResolver` exists for deployments behind a
  trusted gateway only. Conflicting or malformed values are rejected (400), never guessed. Missing values are refused
  for endpoints marked `RequireTenant()` (403) before a handler runs. The scoped `TenantContext` can be set once and
  cannot switch tenants mid-request.
- Dapper: `TenantDataSource.BeginAsync()` opens a connection, begins a transaction and runs
  `SELECT current_setting($1, true), set_config($1, $2, true)`. Npgsql prepends the lazy `BEGIN`, so this is one
  round trip. The returned `TenantSession` binds every Dapper call to its transaction. The same statement reads the
  previous value, so a pooled connection that arrives with the variable already set at session level (some other code
  used `SET`) is logged and counted (`tenancy.session_variable.leaks`). The local value still wins.
- EF Core: `TenantTransactionInterceptor` runs the same `set_config` in `TransactionStarted`, so it covers explicit
  transactions and the one SaveChanges opens. `TenantSaveChangesInterceptor` forces a transaction even for
  single-statement saves, which EF Core would otherwise run in autocommit. `TenantCommandGuardInterceptor` throws on
  any command outside a transaction, turning "silently zero rows" into an exception. `WithTenantTransaction<T>()` runs
  a whole route group in one transaction.
- Single-statement reads can use `QueryInImplicitTransactionAsync`: `SET LOCAL ...; <query>` sent as one batch, which
  PostgreSQL runs as one implicit transaction, so the setting is still transaction-scoped. That gives one round trip
  instead of three. The tenant is formatted from a `TenantId` (a canonical UUID), so the literal cannot carry SQL. A
  property test checks its shape.
- Fail closed: no tenant means the column default and the policy compare against NULL, so the role sees zero rows and
  inserts fail (`NOT NULL` or the policy). A malformed value raises `invalid input syntax for type uuid`. Both are
  tested.

## Consequences

- The isolation boundary is the transaction. Code that runs queries in autocommit mode sees nothing (Dapper) or
  throws (EF Core). That is intended, and the guard interceptor makes it visible early.
- Each tenant transaction costs one extra statement in the first round trip. The measured cost is in
  `docs/benchmarks.md` (part B).
- `ConnectionPoolPitfallTests` and `PgBouncerTests` show option 1 failing for real: a session-level `SET` reaching the
  next request on a pooled connection, reaching a *different client* through PgBouncer, and outliving the client that
  set it. They also show the fixes holding.

[npgsql-compat]: https://www.npgsql.org/doc/compatibility.html
[pgb-config]: https://www.pgbouncer.org/config.html
