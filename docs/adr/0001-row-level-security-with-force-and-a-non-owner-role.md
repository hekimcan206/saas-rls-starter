# 1. Tenant isolation in the database: RLS with FORCE and a non-owner application role

- Status: accepted
- Date: 2026-11-11

## Context

A shared-schema multi-tenant SaaS keeps every tenant's rows in the same tables, distinguished by `tenant_id`. The
classic failure is one forgotten `WHERE tenant_id = @tenant` (a new endpoint, a report, an ad-hoc join, an ORM
`Include` that walks into another table) that returns another tenant's data. Code review and tests reduce that risk.
They do not remove it, because every query is a new chance to forget the predicate.

PostgreSQL row-level security (RLS) moves the predicate into the database: a policy is added to every query on the
table, whatever the application sends. But RLS is easy to switch off without noticing. Superusers and roles with
`BYPASSRLS` skip it entirely, and table owners skip it unless the table also has `FORCE ROW LEVEL SECURITY`
([PostgreSQL 17 docs, 5.9 Row Security Policies][rls]). None of these cases raises an error. Every policy is simply
skipped.

## Options considered

1. **Application-level filtering only** (EF Core global query filters, a repository base class). Cheap and portable,
   but raw SQL, Dapper, reports and migrations all bypass it, and a missed filter fails open.
2. **Schema per tenant or database per tenant.** Strong isolation and easy per-tenant restore, but migrations run N
   times, connection pools multiply per tenant, and cross-tenant analytics need federation. Worth it for a few large
   tenants. It is a poor default for thousands of small ones.
3. **Shared schema with RLS, `ENABLE` + `FORCE`, a dedicated application role that owns nothing and has no
   BYPASSRLS, and a startup guard that checks all of this.**

## Decision

Option 3.

- Four roles (`deploy/postgres/init/01-roles.sh`): `saas_migrator` owns the schemas and tables and runs migrations.
  `saas_app` is the API's login: `NOSUPERUSER NOBYPASSRLS`, owner of nothing, with column-level `UPDATE` grants that
  exclude `tenant_id`. `saas_admin` is covered in ADR 0004, and `saas_admin_definer` (NOLOGIN, BYPASSRLS) owns the
  admin functions.
- Every table in schema `app` has `ENABLE` and `FORCE ROW LEVEL SECURITY` and one policy with both `USING` and
  `WITH CHECK`. FORCE matters even though the application is not the owner. Without it, anything that runs *as the
  owner* (a view the owner created, a SECURITY DEFINER function, a maintenance script) sees every tenant. The pitfall
  test `Pitfall_enable_without_force_lets_the_owner_bypass_and_force_fixes_it` shows that leak through an
  owner-created view.
- No application-side tenant filter is added on top. A global query filter would hide a broken policy from the tests
  that are supposed to catch it. If defence in depth is wanted later, it must not be enabled in the isolation tests.
- `RlsGuard` runs at startup and in `/health/ready`, as the role the API actually connects with. It refuses to start
  if that role is a superuser, has BYPASSRLS, or is (a member of) the owner, or if a relation in a tenant schema lacks
  ENABLE, FORCE or a policy, or if a view is not `security_invoker`, or if a SECURITY DEFINER function owned by a
  bypassing role is executable by the connected role. Integration tests prove the API refuses to start as
  `saas_migrator` and as the superuser.

## Consequences

- Isolation no longer depends on every query being written correctly. The SQL in this repository contains no tenant
  predicates at all, and the isolation and property tests pass.
- RLS is not free. ADR 0003 covers how the policy expression is written so that it costs about as much as a
  hand-written `WHERE` (measured in `docs/benchmarks.md`).
- Some channels are not covered by RLS: foreign-key and unique checks, sequences, planner statistics. ADR 0006 and the
  threat model describe them, and each one is pinned by a test.
- Operations that really are cross-tenant need a deliberate, audited path (ADR 0004).

[rls]: https://www.postgresql.org/docs/17/ddl-rowsecurity.html
