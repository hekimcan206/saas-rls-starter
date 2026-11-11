using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Npgsql;
using Testcontainers.PostgreSql;

// RLS overhead and tenant-variable overhead, measured end to end from Npgsql against PostgreSQL 17 in a container.
//
// Part A, RLS vs no RLS: identical tables (same rows, same indexes) with and without a policy. The RLS table is
// queried WITHOUT a tenant predicate (the policy adds it), the plain table WITH an explicit "WHERE tenant_id = $1",
// which is what an application without RLS must send. The tenant variable is set once per connection, so the numbers
// isolate the cost of evaluating the policy. Two layouts of the same row count: 1 tenant and 1,000 tenants.
// A third table carries a naive policy (tenant_id::text = current_setting(...)) to show what a cast does to the index.
//
// Part B, the per-transaction cost of setting the variable, on the 1,000-tenant RLS table.
//
// Usage: dotnet run -c Release --project benchmarks/SaasRls.Benchmarks -- [rows] [runs] [outputDirectory]
var rows = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 500_000;
var runs = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 3;
var output = args.Length > 2 ? args[2] : "benchmark-results";
Directory.CreateDirectory(output);

await using var container = new PostgreSqlBuilder("postgres:17-alpine")
    .WithDatabase("bench")
    .WithCommand("-c", "shared_buffers=512MB", "-c", "max_parallel_workers_per_gather=0", "-c", "jit=off")
    .Build();
await container.StartAsync();

var admin = new NpgsqlConnectionStringBuilder(container.GetConnectionString()) { Timeout = 60, CommandTimeout = 600 }.ConnectionString;
var app = new NpgsqlConnectionStringBuilder(admin) { Username = "bench_app", Password = "bench" }.ConnectionString;

Console.WriteLine($"Loading {rows:N0} rows per table ...");
var load = Stopwatch.StartNew();
await using (var connection = new NpgsqlConnection(admin))
{
    await connection.OpenAsync();
    await Exec(connection, Setup(rows));
    // Separate command: a multi-statement command is one pipeline (implicit transaction), where VACUUM is not allowed.
    await Exec(connection, "VACUUM ANALYZE");
}

Console.WriteLine($"Loaded in {load.Elapsed.TotalSeconds:F1} s");

await using var dataSource = NpgsqlDataSource.Create(app);
var tenantOne = Guid.Parse("00000000-0000-4000-8000-000000000001");
// A tenant in the 1,000-tenant layout (tenant 42), and ids that belong to it for point lookups.
var tenant42 = await Scalar<Guid>(dataSource, "SELECT tenant_id FROM bench.plain_t1000 WHERE n % 1000 = 42 LIMIT 1");
var ids1 = await Ids(dataSource, "bench.plain_t1", tenantOne);
var ids1000 = await Ids(dataSource, "bench.plain_t1000", tenant42);

var scenarios = new List<Scenario>();
foreach (var (layout, tenant, ids) in new[] { ("1 tenant", tenantOne, ids1), ("1,000 tenants", tenant42, ids1000) })
{
    var suffix = layout == "1 tenant" ? "t1" : "t1000";
    var heavy = layout == "1 tenant";
    // "rls" = policy calling the function directly; "sub" = the same function wrapped in (SELECT ...), an InitPlan.
    foreach (var (table, label) in new[] { ("rls", ""), ("sub", " [policy: (SELECT fn())]") })
    {
        scenarios.Add(new("A", layout, "point lookup by id" + label, $"SELECT total_cents FROM bench.plain_{suffix} WHERE tenant_id = $1 AND id = $2",
            $"SELECT total_cents FROM bench.{table}_{suffix} WHERE id = $1", tenant, ids, 3000));
        scenarios.Add(new("A", layout, "latest 50 orders" + label, $"SELECT id, total_cents FROM bench.plain_{suffix} WHERE tenant_id = $1 ORDER BY created_at DESC LIMIT 50",
            $"SELECT id, total_cents FROM bench.{table}_{suffix} ORDER BY created_at DESC LIMIT 50", tenant, null, 3000));
        scenarios.Add(new("A", layout, "count + sum of tenant" + label, $"SELECT count(*), sum(total_cents) FROM bench.plain_{suffix} WHERE tenant_id = $1",
            $"SELECT count(*), sum(total_cents) FROM bench.{table}_{suffix}", tenant, null, heavy ? 150 : 3000));
    }
}

scenarios.Add(new("A", "1,000 tenants", "latest 50 orders, NAIVE policy (tenant_id::text = setting)",
    "SELECT id, total_cents FROM bench.plain_t1000 WHERE tenant_id = $1 ORDER BY created_at DESC LIMIT 50",
    "SELECT id, total_cents FROM bench.naive_t1000 ORDER BY created_at DESC LIMIT 50", tenant42, null, 100));

var results = new List<object>();
for (var run = 1; run <= runs; run++)
{
    Console.WriteLine($"\n=== run {run}/{runs}");
    foreach (var s in scenarios)
    {
        var (plain, rls) = await PairedAsync(dataSource, s);
        Console.WriteLine($"  [{s.Layout,-13}] {s.Query,-58} plain p50 {plain.P50,8:F3} ms  rls p50 {rls.P50,8:F3} ms  ({Overhead(plain.P50, rls.P50)})");
        results.Add(new { run, part = "A", layout = s.Layout, query = s.Query, iterations = s.Iterations, plain, rls });
    }

    foreach (var (name, p50) in await TransactionCostAsync(dataSource, tenant42))
    {
        Console.WriteLine($"  [B] {name,-74} p50 {p50.P50,8:F3} ms  p95 {p50.P95,8:F3} ms");
        results.Add(new { run, part = "B", layout = "1,000 tenants", query = name, iterations = 3000, stats = p50 });
    }
}

var plans = new Dictionary<string, string>();
await using (var connection = await dataSource.OpenConnectionAsync())
{
    // Each plan is taken with the tenant of its own layout set, so the row counts are the real ones.
    foreach (var (name, tenant, sql) in new[]
    {
        ("rls_t1000 latest 50", tenant42, "SELECT id, total_cents FROM bench.rls_t1000 ORDER BY created_at DESC LIMIT 50"),
        ("naive_t1000 latest 50", tenant42, "SELECT id, total_cents FROM bench.naive_t1000 ORDER BY created_at DESC LIMIT 50"),
        ("rls_t1000 point lookup", tenant42, $"SELECT total_cents FROM bench.rls_t1000 WHERE id = '{ids1000[0]}'"),
        ("sub_t1000 latest 50", tenant42, "SELECT id, total_cents FROM bench.sub_t1000 ORDER BY created_at DESC LIMIT 50"),
        ("plain_t1 count + sum", tenantOne, $"SELECT count(*), sum(total_cents) FROM bench.plain_t1 WHERE tenant_id = '{tenantOne}'"),
        ("rls_t1 count + sum", tenantOne, "SELECT count(*), sum(total_cents) FROM bench.rls_t1"),
        ("sub_t1 count + sum", tenantOne, "SELECT count(*), sum(total_cents) FROM bench.sub_t1"),
    })
    {
        await SetSession(connection, tenant);
        plans[name] = string.Join('\n', await Lines(connection, "EXPLAIN (ANALYZE, COSTS OFF, TIMING OFF, SUMMARY OFF) " + sql));
        Console.WriteLine($"\n-- plan: {name}\n{plans[name]}");
    }
}

var serverVersion = await Scalar<string>(dataSource, "SELECT version()");
var file = Path.Combine(output, $"rls-overhead-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new
{
    rows,
    runs,
    server = serverVersion,
    dotnet = Environment.Version.ToString(),
    os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
    processors = Environment.ProcessorCount,
    results,
    plans,
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"\nwrote {file}");
return;

static string Overhead(double plain, double rls) => $"{(rls - plain) / plain * 100:+0.0;-0.0}%";

static async Task<(Stats Plain, Stats Rls)> PairedAsync(NpgsqlDataSource dataSource, Scenario s)
{
    await using var connection = await dataSource.OpenConnectionAsync();
    await SetSession(connection, s.Tenant);
    Guid[] plainArgs = s.Ids is null ? [s.Tenant] : [s.Tenant, s.Ids[0]];
    Guid[] rlsArgs = s.Ids is null ? [] : [s.Ids[0]];
    await using var plain = Prepared(connection, s.PlainSql, plainArgs);
    await using var rls = Prepared(connection, s.RlsSql, rlsArgs);
    await plain.PrepareAsync();
    await rls.PrepareAsync();

    var warmup = Math.Max(20, s.Iterations / 6);
    var plainTimes = new double[s.Iterations];
    var rlsTimes = new double[s.Iterations];
    for (var i = -warmup; i < s.Iterations; i++)
    {
        // Interleaved, alternating which goes first: drift on a shared host hits both variants equally.
        var id = s.Ids?[(i + warmup) % s.Ids.Length];
        if (id is not null)
        {
            plain.Parameters[1].Value = id.Value;
            rls.Parameters[0].Value = id.Value;
        }

        var first = (i & 1) == 0 ? plain : rls;
        var second = (i & 1) == 0 ? rls : plain;
        var t1 = await TimeAsync(first);
        var t2 = await TimeAsync(second);
        if (i >= 0)
        {
            (first == plain ? plainTimes : rlsTimes)[i] = t1;
            (second == plain ? plainTimes : rlsTimes)[i] = t2;
        }
    }

    return (Stats.Of(plainTimes), Stats.Of(rlsTimes));
}

static NpgsqlCommand Prepared(NpgsqlConnection connection, string sql, params Guid[] values)
{
    var command = new NpgsqlCommand(sql, connection);
    foreach (var value in values)
    {
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = value });
    }

    return command;
}

static async Task<double> TimeAsync(NpgsqlCommand command)
{
    var start = Stopwatch.GetTimestamp();
    await using (var reader = await command.ExecuteReaderAsync())
    {
        while (await reader.ReadAsync())
        {
        }
    }

    return Stopwatch.GetElapsedTime(start).TotalMilliseconds;
}

static async Task<List<(string, Stats)>> TransactionCostAsync(NpgsqlDataSource dataSource, Guid tenant)
{
    const int iterations = 3000;
    const string query = "SELECT id, total_cents FROM bench.rls_t1000 ORDER BY created_at DESC LIMIT 50";
    const string plainQuery = "SELECT id, total_cents FROM bench.plain_t1000 WHERE tenant_id = $1 ORDER BY created_at DESC LIMIT 50";
    var results = new List<(string, Stats)>();
    await using var connection = await dataSource.OpenConnectionAsync();

    async Task<Stats> Measure(Func<Task> once)
    {
        for (var i = 0; i < 300; i++)
        {
            await once();
        }

        var times = new double[iterations];
        for (var i = 0; i < iterations; i++)
        {
            var start = Stopwatch.GetTimestamp();
            await once();
            times[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        return Stats.Of(times);
    }

    async Task Drain(NpgsqlCommand command)
    {
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
        }
    }

    // 1. No RLS, autocommit, explicit predicate: one round trip.
    await using (var command = new NpgsqlCommand(plainQuery, connection))
    {
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = tenant });
        results.Add(("no RLS, autocommit, WHERE tenant_id = $1 (1 round trip)", await Measure(() => Drain(command))));
    }

    // 2. No RLS, explicit transaction: BEGIN rides on the query, then COMMIT (2 round trips).
    await using (var command = new NpgsqlCommand(plainQuery, connection))
    {
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = tenant });
        results.Add(("no RLS, BEGIN + query, COMMIT (2 round trips)", await Measure(async () =>
        {
            await using var tx = await connection.BeginTransactionAsync();
            command.Transaction = tx;
            await Drain(command);
            await tx.CommitAsync();
        })));
    }

    // 3. RLS, the TenantSession path: BEGIN + set_config, query, COMMIT (3 round trips).
    await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', $1, true)", connection))
    await using (var command = new NpgsqlCommand(query, connection))
    {
        set.Parameters.Add(new NpgsqlParameter<string> { TypedValue = tenant.ToString() });
        results.Add(("RLS, BEGIN + set_config(..., true), query, COMMIT (3 round trips)", await Measure(async () =>
        {
            await using var tx = await connection.BeginTransactionAsync();
            set.Transaction = tx;
            command.Transaction = tx;
            await set.ExecuteNonQueryAsync();
            await Drain(command);
            await tx.CommitAsync();
        })));
    }

    // 4. RLS, one round trip: SET LOCAL + query as one batch = one implicit transaction.
    await using (var command = new NpgsqlCommand($"SET LOCAL app.tenant_id = '{tenant:D}'; {query}", connection))
    {
        results.Add(("RLS, SET LOCAL + query in one batch (1 round trip)", await Measure(() => Drain(command))));
    }

    // 5. RLS, variable already set for the session (not safe with pooling; the lower bound).
    await SetSession(connection, tenant);
    await using (var command = new NpgsqlCommand(query, connection))
    {
        results.Add(("RLS, session variable preset, autocommit (1 round trip, lower bound)", await Measure(() => Drain(command))));
    }

    return results;
}

static async Task SetSession(NpgsqlConnection connection, Guid tenant)
{
    await using var command = new NpgsqlCommand("SELECT set_config('app.tenant_id', $1, false)", connection);
    command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = tenant.ToString() });
    await command.ExecuteNonQueryAsync();
}

static async Task Exec(NpgsqlConnection connection, string sql)
{
    await using var command = new NpgsqlCommand(sql, connection);
    await command.ExecuteNonQueryAsync();
}

static async Task<T> Scalar<T>(NpgsqlDataSource dataSource, string sql)
{
    await using var command = dataSource.CreateCommand(sql);
    return (T)(await command.ExecuteScalarAsync())!;
}

static async Task<Guid[]> Ids(NpgsqlDataSource dataSource, string table, Guid tenant)
{
    await using var command = dataSource.CreateCommand($"SELECT id FROM {table} WHERE tenant_id = $1 ORDER BY random() LIMIT 500");
    command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = tenant });
    var ids = new List<Guid>();
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        ids.Add(reader.GetGuid(0));
    }

    return [.. ids];
}

static async Task<List<string>> Lines(NpgsqlConnection connection, string sql)
{
    await using var command = new NpgsqlCommand(sql, connection);
    var lines = new List<string>();
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        lines.Add(reader.GetString(0));
    }

    return lines;
}

static string Setup(int rows) => $"""
    CREATE ROLE bench_app LOGIN PASSWORD 'bench' NOSUPERUSER NOBYPASSRLS;
    CREATE SCHEMA bench;
    GRANT USAGE ON SCHEMA bench TO bench_app;
    CREATE FUNCTION bench.current_tenant_id() RETURNS uuid LANGUAGE sql STABLE PARALLEL SAFE
        AS $$ SELECT nullif(pg_catalog.current_setting('app.tenant_id', true), '')::uuid $$;
    GRANT EXECUTE ON FUNCTION bench.current_tenant_id() TO bench_app;

    CREATE TABLE bench.plain_t1000 AS
        SELECT gen_random_uuid() AS id, g AS n,
               md5('tenant-' || (g % 1000))::uuid AS tenant_id,
               now() - g * interval '1 second' AS created_at,
               (random() * 100000)::bigint AS total_cents
        FROM generate_series(1, {rows}) g;
    CREATE TABLE bench.plain_t1 AS
        SELECT id, n, '00000000-0000-4000-8000-000000000001'::uuid AS tenant_id, created_at, total_cents FROM bench.plain_t1000;
    CREATE TABLE bench.rls_t1000 AS SELECT * FROM bench.plain_t1000;
    CREATE TABLE bench.rls_t1 AS SELECT * FROM bench.plain_t1;
    CREATE TABLE bench.naive_t1000 AS SELECT * FROM bench.plain_t1000;
    CREATE TABLE bench.sub_t1000 AS SELECT * FROM bench.plain_t1000;
    CREATE TABLE bench.sub_t1 AS SELECT * FROM bench.plain_t1;

    DO $$
    DECLARE t text;
    BEGIN
        FOREACH t IN ARRAY ARRAY['plain_t1000', 'plain_t1', 'rls_t1000', 'rls_t1', 'naive_t1000', 'sub_t1000', 'sub_t1'] LOOP
            EXECUTE format('ALTER TABLE bench.%I ADD PRIMARY KEY (id)', t);
            EXECUTE format('CREATE INDEX ON bench.%I (tenant_id, created_at DESC)', t);
            EXECUTE format('GRANT SELECT ON bench.%I TO bench_app', t);
        END LOOP;
    END $$;

    ALTER TABLE bench.rls_t1000 ENABLE ROW LEVEL SECURITY;
    ALTER TABLE bench.rls_t1000 FORCE ROW LEVEL SECURITY;
    CREATE POLICY p ON bench.rls_t1000 USING (tenant_id = bench.current_tenant_id());
    ALTER TABLE bench.rls_t1 ENABLE ROW LEVEL SECURITY;
    ALTER TABLE bench.rls_t1 FORCE ROW LEVEL SECURITY;
    CREATE POLICY p ON bench.rls_t1 USING (tenant_id = bench.current_tenant_id());
    ALTER TABLE bench.naive_t1000 ENABLE ROW LEVEL SECURITY;
    ALTER TABLE bench.naive_t1000 FORCE ROW LEVEL SECURITY;
    CREATE POLICY p ON bench.naive_t1000 USING (tenant_id::text = current_setting('app.tenant_id', true));
    -- The same policy with the function call wrapped in a scalar subquery: evaluated once per query as an InitPlan.
    ALTER TABLE bench.sub_t1000 ENABLE ROW LEVEL SECURITY;
    ALTER TABLE bench.sub_t1000 FORCE ROW LEVEL SECURITY;
    CREATE POLICY p ON bench.sub_t1000 USING (tenant_id = (SELECT bench.current_tenant_id()));
    ALTER TABLE bench.sub_t1 ENABLE ROW LEVEL SECURITY;
    ALTER TABLE bench.sub_t1 FORCE ROW LEVEL SECURITY;
    CREATE POLICY p ON bench.sub_t1 USING (tenant_id = (SELECT bench.current_tenant_id()));

    """;

internal sealed record Scenario(string Part, string Layout, string Query, string PlainSql, string RlsSql, Guid Tenant, Guid[]? Ids, int Iterations);

internal sealed record Stats(double P50, double P95, double P99, double Mean)
{
    public static Stats Of(double[] samples)
    {
        var sorted = samples.Order().ToArray();
        double At(double q) => sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(q * sorted.Length) - 1)];
        return new Stats(Math.Round(At(0.50), 4), Math.Round(At(0.95), 4), Math.Round(At(0.99), 4), Math.Round(sorted.Average(), 4));
    }
}
