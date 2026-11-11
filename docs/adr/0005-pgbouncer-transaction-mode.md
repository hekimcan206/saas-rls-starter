# 5. PgBouncer in transaction mode is supported; session features are not used

- Status: accepted
- Date: 2026-11-11

## Context

Multi-tenant SaaS deployments often run many API instances against one PostgreSQL, with PgBouncer in transaction mode
to keep the number of server connections low. In transaction mode a client owns a server connection only for the
duration of a transaction. Anything session-scoped (`SET`, `SET ROLE`, advisory locks, `LISTEN`, temporary tables,
named prepared statements without protocol support) is left on the server connection for whoever comes next.
PgBouncer deliberately does not run `server_reset_query` in transaction mode unless `server_reset_query_always` is
enabled ([PgBouncer configuration][pgb-config]). `track_extra_parameters` (1.20+) can only track parameters the server
reports to the client, and a custom setting such as `app.tenant_id` is not reported.

## Options considered

1. **Session mode.** Works with session state, but one server connection per connected client defeats the pooling
   that motivated PgBouncer.
2. **Transaction mode with `server_reset_query_always = 1`.** Turns the reset back on, at the cost of a `DISCARD ALL`
   after every transaction. It does not help a client that is still connected while another client gets "its" server
   connection between two of its transactions.
3. **Transaction mode, with everything tenant-related transaction-scoped** (ADR 0002).

## Decision

Option 3. The compose stack runs the API as `saas_app` through PgBouncer 1.25.1 in transaction mode
(`No Reset On Close=true`, as Npgsql recommends behind a transaction pooler ([Npgsql compatibility notes][npgsql])).
The admin connection, which needs no pooling scale, and the migrator, which uses a session-level advisory lock,
connect to PostgreSQL directly.

The behaviour is demonstrated against real PgBouncer in Testcontainers (`PgBouncerTests`) and in the compose demo:

- a session-level `set_config` by client A is seen by a different client B on the same server connection;
- the leaked value outlives client A's disconnect;
- Npgsql's reset on return does not help, because it is deferred to the next use of A's pooled connection;
- `SET LOCAL`, through `TenantSession` or a one-round-trip batch, leaves nothing behind;
- session mode resets on disconnect;
- 200 concurrent tenant sessions stay isolated while another client keeps planting a tenant at session level.

## Consequences

- The application must never rely on session state. Prepared statements: Npgsql does not auto-prepare by default
  (`Max Auto Prepare=0`), so this repository works without PgBouncer's prepared-statement support. Enabling
  auto-prepare behind PgBouncer needs `max_prepared_statements` > 0 (PgBouncer 1.21+) and was not tested here.
- PgBouncer authenticates clients with SCRAM against a userlist that compose generates from the environment at start.
  Production would use `auth_query` with a dedicated lookup role.

[pgb-config]: https://www.pgbouncer.org/config.html
[npgsql]: https://www.npgsql.org/doc/compatibility.html
