rows per table: 500000, runs: 3, server: PostgreSQL 17.11

| Layout | Query | no RLS p50 (ms) | RLS p50 (ms) | overhead p50 | no RLS p95 | RLS p95 |
|---|---|---:|---:|---:|---:|---:|
| 1 tenant | point lookup by id | 0.366 | 0.370 | +1.1% | 0.672 | 0.664 |
| 1 tenant | latest 50 orders | 0.386 | 0.385 | -0.3% | 0.843 | 0.831 |
| 1 tenant | count + sum of tenant | 32.430 | 202.685 | +525.0% | 41.362 | 244.900 |
| 1 tenant | point lookup by id [policy: (SELECT fn())] | 0.316 | 0.324 | +2.3% | 0.901 | 0.900 |
| 1 tenant | latest 50 orders [policy: (SELECT fn())] | 0.351 | 0.355 | +1.1% | 0.974 | 1.032 |
| 1 tenant | count + sum of tenant [policy: (SELECT fn())] | 33.041 | 32.666 | -1.1% | 41.408 | 41.496 |
| 1,000 tenants | point lookup by id | 0.324 | 0.325 | +0.5% | 0.605 | 0.602 |
| 1,000 tenants | latest 50 orders | 0.404 | 0.412 | +2.1% | 0.946 | 0.922 |
| 1,000 tenants | count + sum of tenant | 0.732 | 0.744 | +1.6% | 1.126 | 1.154 |
| 1,000 tenants | point lookup by id [policy: (SELECT fn())] | 0.339 | 0.348 | +2.8% | 0.846 | 0.783 |
| 1,000 tenants | latest 50 orders [policy: (SELECT fn())] | 0.378 | 0.387 | +2.6% | 0.767 | 0.793 |
| 1,000 tenants | count + sum of tenant [policy: (SELECT fn())] | 0.768 | 0.783 | +1.9% | 1.347 | 1.369 |
| 1,000 tenants | latest 50 orders, NAIVE policy (tenant_id::text = setting) | 0.643 | 84.266 | +12997.0% | 0.985 | 100.099 |

| Transaction shape (1,000-tenant table, latest 50) | p50 (ms) | p95 (ms) |
|---|---:|---:|
| no RLS, autocommit, WHERE tenant_id = $1 (1 round trip) | 0.477 | 0.963 |
| no RLS, BEGIN + query, COMMIT (2 round trips) | 0.746 | 1.763 |
| RLS, BEGIN + set_config(..., true), query, COMMIT (3 round trips) | 1.269 | 2.627 |
| RLS, SET LOCAL + query in one batch (1 round trip) | 0.617 | 1.453 |
| RLS, session variable preset, autocommit (1 round trip, lower bound) | 0.510 | 1.244 |
