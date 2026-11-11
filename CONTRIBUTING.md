# Contributing

Thanks for taking the time to improve saas-rls-starter.

## Prerequisites

- .NET SDK 10.0.400 or later (`global.json` rolls forward to the latest feature band)
- Docker (integration tests start PostgreSQL 17 and PgBouncer through Testcontainers; `docker compose up` runs the demo)

## Workflow

1. Open an issue first for anything larger than a small fix, so we can agree on the approach.
2. Branch from `main` and keep each pull request to one concern.
3. Before pushing:

   ```bash
   dotnet build                 # must be warning-free: warnings are errors
   dotnet test                  # unit, property and integration tests (Docker running)
   dotnet format --verify-no-changes
   ```

## Commit messages

[Conventional Commits](https://www.conventionalcommits.org/) are required. They keep the history readable and make the
hand-written CHANGELOG easy to assemble at release time: `feat:`, `fix:`, `feat!:` or a `BREAKING CHANGE:` footer, and
`docs:`, `test:`, `build:`, `ci:`, `refactor:`, `perf:`, `chore:` for everything else.

## Adding a tenant table

Every relation in schema `app` must be tenant-scoped. A new table needs, in a new migration:

1. `tenant_id uuid NOT NULL DEFAULT app.current_tenant_id()`, and composite foreign keys `(tenant_id, x_id)` to other
   tenant tables (never a plain `x_id` reference, see ADR 0006);
2. `ALTER TABLE ... ENABLE ROW LEVEL SECURITY` **and** `FORCE ROW LEVEL SECURITY`;
3. a policy with `USING` and `WITH CHECK` on `tenant_id = (SELECT app.current_tenant_id())` (keep the subquery, see
   ADR 0003);
4. unique keys that start with `tenant_id`, and no shared sequences for values tenants can see;
5. least-privilege grants to `saas_app` (column-level `UPDATE`, never on `tenant_id`).

The static lint in `tests/SaasRls.UnitTests/SchemaAndInterceptorTests.cs` and `RlsGuard` at startup fail if 2 or 3 are
missing; add isolation tests for anything else.

## Database migrations

Migrations are numbered SQL files in `src/SaasRls.Api/Persistence/Migrations/` (`NNNN_name.sql`), embedded in the
assembly and applied by `--migrate-only` as `saas_migrator`. Never edit a migration that has been applied anywhere: the
runner compares checksums and refuses to start. Add a new, higher-numbered file instead.

## Architecture decisions

A change to a decision recorded in `docs/adr/` needs a new ADR that supersedes the old one (MADR format).
