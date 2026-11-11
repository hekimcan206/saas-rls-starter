# Benchmarks

Two questions: what does row-level security cost compared with the same query without it, and what does setting the
tenant variable cost per transaction. All numbers below were measured on the machine described here, and the raw
per-run results are committed in `benchmark-results/`.

## Environment

| | |
|---|---|
| Host | Windows 11 laptop, Intel Core i7-13620H (10 cores / 16 threads), 15.7 GB RAM |
| Docker | Docker Desktop 29.9 on WSL2, VM limited to 7.6 GB RAM, shared with another agent's containers |
| Host load | Not idle: Windows CPU load sampled at 17% before and 38% right after the run; 4 unrelated containers running |
| Database | PostgreSQL 17.11 (`postgres:17-alpine`) via Testcontainers, `shared_buffers=512MB`, `jit=off`, `max_parallel_workers_per_gather=0` (one backend per query, so the comparison is not dominated by parallel-worker start-up) |
| Client | .NET 10.0, Npgsql 10.0.3, prepared statements, one connection, over the Docker loopback port |
| Data | 500,000 rows per table (synthetic); every variant is a copy of the same rows with the same indexes: PK on `id`, `(tenant_id, created_at DESC)` |

## Method

`benchmarks/SaasRls.Benchmarks` (`dotnet run -c Release --project benchmarks/SaasRls.Benchmarks -- 500000 3`):

- **Part A, RLS vs no RLS.** The plain table is queried with an explicit `WHERE tenant_id = $1`, which is what an
  application without RLS must send. The RLS table is queried with no tenant predicate. The tenant variable is set
  once per connection, so part A isolates the cost of evaluating the policy. Two layouts of the same 500,000 rows:
  **1 tenant** (the tenant owns every row) and **1,000 tenants** (500 rows each). Plain and RLS statements are
  interleaved, alternating which runs first, so drift on the shared host affects both equally.
  3,000 measured iterations per query (150 for the 1-tenant full aggregates), after warm-up.
- **Part B, setting the variable.** Five transaction shapes for the same "latest 50 orders" read, 3,000 iterations
  each after 300 warm-up iterations.
- Three complete runs. The tables report the **median across runs** of each run's p50 and p95, as computed by
  `node benchmarks/summarize.mjs benchmark-results/rls-overhead-20261111-192037.json`. Client-side wall time per
  statement, including the loopback round trip.

## Results: part A, RLS overhead

The shipped policy form is `tenant_id = (SELECT app.current_tenant_id())` (ADR 0003).

| Layout | Query | no RLS p50 (ms) | RLS p50 (ms) | overhead p50 | no RLS p95 | RLS p95 |
|---|---|---:|---:|---:|---:|---:|
| 1 tenant | point lookup by id | 0.316 | 0.324 | +2.3% | 0.901 | 0.900 |
| 1 tenant | latest 50 orders | 0.351 | 0.355 | +1.1% | 0.974 | 1.032 |
| 1 tenant | count + sum of tenant (500,000 rows) | 33.041 | 32.666 | -1.1% | 41.408 | 41.496 |
| 1,000 tenants | point lookup by id | 0.339 | 0.348 | +2.8% | 0.846 | 0.783 |
| 1,000 tenants | latest 50 orders | 0.378 | 0.387 | +2.6% | 0.767 | 0.793 |
| 1,000 tenants | count + sum of tenant (500 rows) | 0.768 | 0.783 | +1.9% | 1.347 | 1.369 |

The same queries with the policy calling the function *without* the subquery wrapper:

| Layout | Query | no RLS p50 (ms) | RLS p50 (ms) | overhead p50 | no RLS p95 | RLS p95 |
|---|---|---:|---:|---:|---:|---:|
| 1 tenant | point lookup by id | 0.366 | 0.370 | +1.1% | 0.672 | 0.664 |
| 1 tenant | latest 50 orders | 0.386 | 0.385 | -0.3% | 0.843 | 0.831 |
| 1 tenant | count + sum of tenant (500,000 rows) | 32.430 | **202.685** | **+525.0%** | 41.362 | 244.900 |
| 1,000 tenants | point lookup by id | 0.324 | 0.325 | +0.5% | 0.605 | 0.602 |
| 1,000 tenants | latest 50 orders | 0.404 | 0.412 | +2.1% | 0.946 | 0.922 |
| 1,000 tenants | count + sum of tenant (500 rows) | 0.732 | 0.744 | +1.6% | 1.126 | 1.154 |

And a policy that casts the column (`tenant_id::text = current_setting('app.tenant_id', true)`):

| Layout | Query | no RLS p50 (ms) | RLS p50 (ms) | overhead p50 | no RLS p95 | RLS p95 |
|---|---|---:|---:|---:|---:|---:|
| 1,000 tenants | latest 50 orders | 0.643 | **84.266** | **+12,997%** | 0.985 | 100.099 |

### What the plans show

From `benchmark-results/rls-plans-20261111-192139.json` (`EXPLAIN ANALYZE`, each with its own layout's tenant set):

- RLS latest 50, 1,000 tenants: `Index Scan using rls_t1000_tenant_id_created_at_idx`, with the policy as the index
  condition. The same plan as the explicit `WHERE`.
- Cast policy: `Seq Scan on naive_t1000`, `Rows Removed by Filter: 499500`. The cast on the column defeats the index.
- 1-tenant aggregate: plain, bare-call and InitPlan variants all run a `Seq Scan` over 500,000 rows. The bare variant's
  filter is `tenant_id = (NULLIF(current_setting(...), ''))::uuid`, computed per row. The InitPlan variant's filter is
  `tenant_id = (InitPlan 1).col1`, computed once. That is the whole 6.2x.

(The timing file `rls-overhead-20261111-192037.json` also contains plans. Its two 1-tenant plans were captured with the
1,000-tenant layout's tenant set and show 0 rows; the harness was corrected and the plans-only file above supersedes
them.)

### Reading part A

- With the shipped policy form, RLS costs the same as a hand-written tenant predicate within measurement noise, at
  both 1 and 1,000 tenants: -1.1% to +2.8% p50, on the order of 0.01 ms for sub-millisecond queries.
- The cost is not in RLS itself but in how the policy is written. Write a cast, and you lose the index. Leave the call
  unwrapped, and a large tenant's reports pay a function call per row.

## Results: part B, the per-transaction cost of setting the tenant

"Latest 50 orders" on the 1,000-tenant RLS table (no-RLS shapes use the plain table with an explicit predicate):

| Transaction shape | round trips | p50 (ms) | p95 (ms) |
|---|---:|---:|---:|
| no RLS, autocommit, `WHERE tenant_id = $1` | 1 | 0.477 | 0.963 |
| no RLS, `BEGIN` + query, `COMMIT` | 2 | 0.746 | 1.763 |
| RLS, `BEGIN` + `set_config(..., true)`, query, `COMMIT` (`TenantSession`) | 3 | 1.269 | 2.627 |
| RLS, `SET LOCAL ...; query` as one batch (`QueryInImplicitTransactionAsync`) | 1 | 0.617 | 1.453 |
| RLS, variable preset for the session, autocommit (unsafe with pooling; lower bound) | 1 | 0.510 | 1.244 |

### Reading part B

- Setting the variable is cheap for the server. What costs is the round trip. `TenantSession` adds one round trip to
  an explicit transaction: 0.746 to 1.269 ms p50, so +0.52 ms on this loopback connection. Every extra round trip
  costs about the same.
- Sent in the same batch as the query, the variable adds 0.11 ms over the unsafe session-level lower bound (0.617 vs
  0.510 ms), and the whole read is still a single round trip, faster than an explicit no-RLS transaction.
- On a real network (0.5 to 1 ms per round trip between hosts), the saving from the one-batch form grows accordingly.
  For multi-statement units of work, the round trip is amortised over the transaction.

## Not measured

- Throughput under concurrency, and PgBouncer's own overhead: functional behaviour through PgBouncer is tested, but no
  numbers were taken.
- Write paths (insert/update with `WITH CHECK`).
- Tables larger than 500,000 rows. The machine is shared and the Docker VM has 7.6 GB, so the data set was kept at a
  size that loads in about 13 s.

## Reproduce

```bash
dotnet run -c Release --project benchmarks/SaasRls.Benchmarks -- 500000 3 benchmark-results
node benchmarks/summarize.mjs benchmark-results/rls-overhead-<timestamp>.json
# plans only (no timing): pass 0 runs
dotnet run -c Release --project benchmarks/SaasRls.Benchmarks -- 500000 0 benchmark-results
```
