# Security policy

## Supported versions

Only the latest release receives fixes.

## Reporting a vulnerability

Please **do not open a public issue**. Use GitHub's private vulnerability reporting instead:
**Security → Report a vulnerability** on this repository. You can expect an acknowledgement within 5 working days. If
the report is confirmed, a fix or mitigation plan follows within 30 days.

Please include the affected version or commit, steps to reproduce and the impact you observed. Any way for one tenant
to read, change or infer another tenant's data is in scope and especially welcome, including side channels.

## Security-relevant design

See [docs/threat-model.md](docs/threat-model.md) for assets, actors, threats and mitigations. In short:

| Area | Default |
|---|---|
| Tenant isolation | PostgreSQL RLS, `ENABLE` + `FORCE` on every table in schema `app`, `USING` + `WITH CHECK` policies |
| Application login | `saas_app`: no superuser, no BYPASSRLS, owns nothing, no `UPDATE` on `tenant_id`; checked by `RlsGuard` at startup and in readiness |
| Tenant context | Validated JWT claim; set per transaction with `set_config(..., true)`; never session-level |
| Cross-tenant access | `saas_admin` may only execute SECURITY DEFINER functions that require an operator and a reason and write an append-only audit row |
| Side channels | Composite tenant foreign keys, per-tenant unique keys, UUIDv7 ids, per-tenant counters, coarse error mapping |
| Pooling | Safe behind PgBouncer transaction mode; demonstrated with tests against real PgBouncer |
| Secrets | None in the repository. `.env.example` holds clearly marked development-only values |
