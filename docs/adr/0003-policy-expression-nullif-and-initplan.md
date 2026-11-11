# 3. The policy expression: `tenant_id = (SELECT app.current_tenant_id())` with a NULL-safe helper

- Status: accepted
- Date: 2026-11-11

## Context

The policy runs on every row a query touches. Three details decide whether it is correct and whether it is cheap.

1. **Undefined vs empty setting.** `current_setting('app.tenant_id')` raises if the setting was never defined, and
   `current_setting(..., true)` returns NULL instead. After a transaction that used `SET LOCAL` ends, or after
   `RESET`/`DISCARD ALL`, a custom setting that the session has seen reads back as `''`, not NULL ([`set_config` and
   `current_setting`][admin-fn]). `''::uuid` raises. A naive `current_setting('app.tenant_id', true)::uuid` therefore
   works on a fresh connection and fails on every pooled connection after the first tenant transaction.
   `Pitfall_a_naive_policy_without_nullif_breaks_after_the_first_transaction_on_a_connection` reproduces exactly this.
2. **Index use.** Comparing `tenant_id::text = current_setting(...)` casts the *column*, so the `(tenant_id, ...)`
   index cannot be used.
3. **Per-row evaluation.** A bare call to a STABLE function in a filter is evaluated for each row the scan visits.
   Wrapping it in a scalar subquery turns it into an InitPlan, which is evaluated once per query. This is widely
   recommended for RLS ([Supabase, RLS performance and best practices][supabase]). One source suggests the planner
   already handles `current_setting` well enough on its own ([chat2db][chat2db]), so this was measured rather than
   assumed.

## Options considered (all measured, `docs/benchmarks.md`, 500,000 rows, PostgreSQL 17)

| Policy | Latest 50 orders, 1,000 tenants (p50) | Count + sum of one tenant, 1 tenant (p50) |
|---|---:|---:|
| none (plain table, explicit `WHERE tenant_id = $1`) | 0.404 ms | 32.430 ms |
| `tenant_id::text = current_setting('app.tenant_id', true)` | 84.266 ms (seq scan) | not measured |
| `tenant_id = app.current_tenant_id()` (bare, inlined) | 0.412 ms | 202.685 ms |
| `tenant_id = (SELECT app.current_tenant_id())` (InitPlan) | 0.387 ms (plain 0.378) | 32.666 ms (plain 33.041) |

The plans (`benchmark-results/rls-plans-*.json`) show why. All three 1-tenant variants run the same sequential scan
over 500,000 rows. With the bare call, the filter `tenant_id = (NULLIF(current_setting(...), ''))::uuid` is computed
for every row. With the InitPlan, the filter compares against `(InitPlan 1).col1`, computed once. Index scans are
unaffected either way: the index condition is computed once in both forms.

## Decision

- `app.current_tenant_id()` is `LANGUAGE sql STABLE PARALLEL SAFE` and returns
  `nullif(current_setting('app.tenant_id', true), '')::uuid`. Undefined and empty both mean NULL, which matches no
  row and fails `NOT NULL` on insert. A malformed value raises.
- Every policy is `USING (tenant_id = (SELECT app.current_tenant_id())) WITH CHECK (same)`.
- Column defaults use the bare call (`DEFAULT app.current_tenant_id()`): a default is computed once per inserted row,
  and there is nothing to scan.

## Consequences

- With the InitPlan form, RLS cost the same as a hand-written predicate within measurement noise at both 1 and 1,000
  tenants (-1.1% to +2.8% p50 on sub-millisecond queries, about 0.01 ms).
- The bare form would have cost 6.2x on the one query shape where the tenant's rows dominate the table (a large
  tenant, full scans for reports). Such a regression is easy to miss in tests with small tables.
- `CONTRIBUTING.md` asks new policies to keep the subquery form.

[admin-fn]: https://www.postgresql.org/docs/17/functions-admin.html#FUNCTIONS-ADMIN-SET
[supabase]: https://supabase.com/docs/guides/troubleshooting/rls-performance-and-best-practices-Z5Jjwv
[chat2db]: https://chat2db.ai/resources/blog/postgres-rls-performance-tuning
