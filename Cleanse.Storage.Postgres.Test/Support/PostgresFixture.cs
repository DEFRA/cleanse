using Cleanse.Storage.Postgres.Schema;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Cleanse.Storage.Postgres.Test.Support;

/// <summary>
/// A throwaway PostgreSQL 16 instance, matching Aurora's 16.6. Shared across every test class in
/// the collection because container startup dominates the run time; isolation comes from
/// truncating between tests rather than from a container each.
/// </summary>
/// <remarks>
/// Laid out the way CDP lays out Aurora, and the way compose.yml does: <c>cleanse_ddl</c> owns
/// the schema and applies the changelog, grants included, while the stores connect as
/// <c>cleanse</c> with only the DML rights those grants give it. A store that needs more than the
/// deployed service has therefore fails here first.
/// </remarks>
public sealed class PostgresFixture : IAsyncLifetime
{
    private const string OwnerRole = "cleanse_ddl";
    private const string ServiceRole = "cleanse";

    private PostgreSqlContainer? _container;
    private NpgsqlDataSource? _owner;

    /// <summary>Connects as the DML-only service role, as the deployed service does.</summary>
    public NpgsqlDataSource DataSource { get; private set; } = null!;

    /// <summary>False when Docker is not available, which skips the suite rather than failing it.</summary>
    public bool IsAvailable { get; private set; }

    public string? SkipReason { get; private set; }

    /// <summary>
    /// Where the suite has to prove it ran, a missing Docker fails it instead of skipping it.
    /// GitHub Actions sets CI=true; CLEANSE_REQUIRE_DOCKER=true asks for the same anywhere else.
    /// </summary>
    private static bool DockerRequired => IsSet("CLEANSE_REQUIRE_DOCKER") || IsSet("CI");

    public async ValueTask InitializeAsync()
    {
        // Testcontainers probes the Docker endpoint while building, not just while starting, so
        // the whole sequence has to be guarded for the suite to skip rather than fail.
        try
        {
            _container = new PostgreSqlBuilder()
                // Kept in step with the postgres service in compose.yml.
                .WithImage("postgres:16.10-alpine")
                .WithDatabase("cleanse")
                .WithUsername(OwnerRole)
                .WithPassword(OwnerRole)
                .Build();

            await _container.StartAsync();
        }
        catch (Exception ex) when (!DockerRequired)
        {
            SkipReason = $"Docker is not available: {ex.GetBaseException().Message}";
            _container = null;
            IsAvailable = false;
            return;
        }

        // From here on a failure is a defect in the schema scripts or the grants, so it fails the
        // suite. It is never a reason to skip.
        _owner = NpgsqlDataSource.Create(_container.GetConnectionString());
        await ApplyAsync(Scripts("roles"));
        await ApplyAsync(Scripts("changelog"));

        DataSource = NpgsqlDataSource.Create(
            new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
            {
                Username = ServiceRole,
                Password = ServiceRole
            }.ConnectionString);

        IsAvailable = true;
    }

    /// <summary>
    /// The service role comes from compose's init script; the schema from the committed migration
    /// scripts rather than EF's EnsureCreated. Those scripts are the exact artefacts Liquibase runs
    /// on CodeBuild, so these tests fail if one is wrong, stale, out of step with the model the
    /// stores query through, or grants the service too little.
    /// </summary>
    private static string[] Scripts(string folder)
    {
        var path = Path.Combine(AppContext.BaseDirectory, folder);
        var scripts = Directory.Exists(path)
            ? Directory.GetFiles(path, "*.sql").OrderBy(Path.GetFileName, StringComparer.Ordinal).ToArray()
            : [];

        if (scripts.Length == 0)
        {
            throw new FileNotFoundException(
                $"No SQL scripts were copied to '{folder}'. The changelog scripts are generated with " +
                "'dotnet dotnet-ef migrations script --idempotent --project Cleanse.Storage.Postgres " +
                "--output changelog/sql/<nnnn>_<name>.sql'; the role script lives in compose/postgres/init.",
                path);
        }

        return scripts;
    }

    private async Task ApplyAsync(IEnumerable<string> scripts)
    {
        foreach (var script in scripts)
        {
            await using var command = _owner!.CreateCommand(await File.ReadAllTextAsync(script));
            await command.ExecuteNonQueryAsync();
        }
    }

    public CleanseDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<CleanseDbContext>().UseNpgsql(DataSource).Options);

    /// <summary>
    /// Wipes every table between tests. Cheaper and safer than recreating the schema. Runs as the
    /// owner, because the service role is deliberately not granted TRUNCATE.
    /// </summary>
    public async Task ResetAsync()
    {
        if (!IsAvailable)
        {
            return;
        }

        await using var command = _owner!.CreateCommand(
            "TRUNCATE TABLE issue_history, audit_entries, issues, rule_runs, " +
            "import_job_rejections, import_jobs, data_resets RESTART IDENTITY CASCADE");
        await command.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (DataSource is not null)
        {
            await DataSource.DisposeAsync();
        }

        if (_owner is not null)
        {
            await _owner.DisposeAsync();
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    private static bool IsSet(string variable) =>
        Environment.GetEnvironmentVariable(variable) is { } value &&
        (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1");
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
