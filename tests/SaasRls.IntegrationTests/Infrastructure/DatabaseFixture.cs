using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SaasRls.Api.Persistence;
using SaasRls.Tenancy;
using SaasRls.Tenancy.Context;
using SaasRls.Tenancy.Npgsql;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(SaasRls.IntegrationTests.Infrastructure.DatabaseFixture))]

namespace SaasRls.IntegrationTests.Infrastructure;

/// <summary>
/// One PostgreSQL 17 and one PgBouncer 1.25 container per test run, on a private network. PostgreSQL is initialised
/// with the exact role script docker compose uses and migrated as <c>saas_migrator</c>. Tests create their own tenants
/// (fresh UUIDs), so they never depend on each other's rows.
/// </summary>
public sealed class DatabaseFixture : IAsyncLifetime
{
    public const string Database = "saas";
    public const string MigratorPassword = "migrator-test";
    public const string AppPassword = "app-test";
    public const string AdminPassword = "admin-test";
    public const string PgBouncerImage = "pgbouncer/pgbouncer:1.25.1";

    private readonly INetwork _network = new NetworkBuilder().Build();
    private readonly PostgreSqlContainer _postgres;
    private readonly IContainer _pgbouncer;

    public DatabaseFixture()
    {
        _postgres = new PostgreSqlBuilder("postgres:17-alpine")
            .WithDatabase(Database)
            .WithNetwork(_network)
            .WithNetworkAliases("postgres")
            .WithEnvironment("MIGRATOR_PASSWORD", MigratorPassword)
            .WithEnvironment("APP_PASSWORD", AppPassword)
            .WithEnvironment("ADMIN_PASSWORD", AdminPassword)
            .WithResourceMapping(
                File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "postgres-init", "01-roles.sh")),
                "/docker-entrypoint-initdb.d/01-roles.sh")
            .Build();

        // Three pools on one PgBouncer:
        //   saas          transaction mode, normal size: what the API would use;
        //   saas_tiny     transaction mode, ONE server connection: every client shares it, which makes the
        //                 cross-client leak of session state deterministic instead of probabilistic;
        //   saas_session  session mode, one server connection: a client owns it until it disconnects;
        //   saas_lab      transaction mode, for the test that deliberately pollutes server connections.
        _pgbouncer = new ContainerBuilder(PgBouncerImage)
            .WithNetwork(_network)
            .WithEnvironment("DATABASES", string.Join(',',
                "saas = host=postgres port=5432 dbname=saas",
                "saas_tiny = host=postgres port=5432 dbname=saas pool_size=1",
                "saas_session = host=postgres port=5432 dbname=saas pool_size=1 pool_mode=session",
                "saas_lab = host=postgres port=5432 dbname=saas pool_size=10"))
            .WithEnvironment("PGBOUNCER_POOL_MODE", "transaction")
            .WithEnvironment("PGBOUNCER_DEFAULT_POOL_SIZE", "20")
            .WithEnvironment("PGBOUNCER_MAX_CLIENT_CONN", "500")
            .WithEnvironment("PGBOUNCER_AUTH_TYPE", "scram-sha-256")
            .WithEnvironment("PGBOUNCER_AUTH_FILE", "/etc/pgbouncer-auth/userlist.txt")
            .WithResourceMapping(
                Encoding.UTF8.GetBytes($"\"saas_app\" \"{AppPassword}\"\n\"saas_admin\" \"{AdminPassword}\"\n"),
                "/etc/pgbouncer-auth/userlist.txt")
            .WithPortBinding(6432, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("process up"))
            .Build();
    }

    public NpgsqlDataSource Superuser { get; private set; } = null!;

    public NpgsqlDataSource Migrator { get; private set; } = null!;

    public NpgsqlDataSource App { get; private set; } = null!;

    public NpgsqlDataSource Admin { get; private set; } = null!;

    public TenancyOptions Tenancy { get; } = new();

    /// <summary>Tenant sessions for explicit tenants (no request scope needed).</summary>
    public TenantDataSource Tenants { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _network.CreateAsync();
        await _postgres.StartAsync();
        await using (var migrator = DatabaseSetup.CreateDataSource(ConnectionString("saas_migrator", MigratorPassword)))
        {
            await new MigrationRunner(migrator, NullLogger<MigrationRunner>.Instance).MigrateAsync();
        }

        await _pgbouncer.StartAsync();

        Superuser = NpgsqlDataSource.Create(ConnectionString(null, null));
        Migrator = NpgsqlDataSource.Create(ConnectionString("saas_migrator", MigratorPassword));
        App = NpgsqlDataSource.Create(ConnectionString("saas_app", AppPassword));
        Admin = NpgsqlDataSource.Create(ConnectionString("saas_admin", AdminPassword));
        Tenants = new TenantDataSource(App, new TenantContext(), Tenancy);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var dataSource in new[] { Superuser, Migrator, App, Admin })
        {
            if (dataSource is not null)
            {
                await dataSource.DisposeAsync();
            }
        }

        await _pgbouncer.DisposeAsync();
        await _postgres.DisposeAsync();
        await _network.DisposeAsync();
    }

    /// <summary>Direct connection string (host port mapped by Testcontainers); superuser when no user is given.</summary>
    public string ConnectionString(string? username, string? password, Action<NpgsqlConnectionStringBuilder>? configure = null)
    {
        var builder = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString())
        {
            Database = Database,
            MaxPoolSize = 50,
            // Generous connect timeout: the host is shared with other projects' containers.
            Timeout = 60,
        };
        if (username is not null)
        {
            builder.Username = username;
            builder.Password = password;
        }

        configure?.Invoke(builder);
        return builder.ConnectionString;
    }

    /// <summary>A connection string to a PgBouncer pool. Defaults follow Npgsql's guidance for transaction pooling.</summary>
    public string PgBouncerConnectionString(string database, Action<NpgsqlConnectionStringBuilder>? configure = null)
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = _pgbouncer.Hostname,
            Port = _pgbouncer.GetMappedPublicPort(6432),
            Database = database,
            Username = "saas_app",
            Password = AppPassword,
            Timeout = 60,
            // Npgsql's reset (DISCARD ALL) is meaningless behind a transaction pooler: the next transaction may run on
            // a different server connection anyway.
            NoResetOnClose = true,
        };
        configure?.Invoke(builder);
        return builder.ConnectionString;
    }

    /// <summary>Creates a tenant through the audited admin path, exactly as an operator would.</summary>
    public async Task<TenantId> CreateTenantAsync(string name)
    {
        await using var connection = await Admin.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var operatorCommand = new NpgsqlCommand("SELECT set_config('admin.operator', 'fixture@tests', true)", connection, transaction))
        {
            await operatorCommand.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand("SELECT admin.create_tenant($1, 'pro', 'integration test fixture')", connection, transaction);
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = name });
        var id = (Guid)(await command.ExecuteScalarAsync())!;
        await transaction.CommitAsync();
        return new TenantId(id);
    }

    public async Task<T> SuperuserScalarAsync<T>(string sql)
    {
        await using var command = Superuser.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    public async Task SuperuserExecuteAsync(string sql)
    {
        await using var command = Superuser.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<string> PostgresLogsAsync()
    {
        var (stdout, stderr) = await _postgres.GetLogsAsync();
        return stdout + stderr;
    }
}
