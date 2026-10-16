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
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    /// <summary>False when Docker is not available, which skips the suite rather than failing it.</summary>
    public bool IsAvailable { get; private set; }

    public string? SkipReason { get; private set; }

    public async ValueTask InitializeAsync()
    {
        // Testcontainers probes the Docker endpoint while building, not just while starting, so
        // the whole sequence has to be guarded for the suite to skip rather than fail.
        try
        {
            _container = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("cleanse")
                .WithUsername("cleanse")
                .WithPassword("cleanse")
                .Build();

            await _container.StartAsync();

            DataSource = new NpgsqlDataSourceBuilder(_container.GetConnectionString()).Build();
            await CreateSchemaAsync();
            IsAvailable = true;
        }
        catch (Exception ex)
        {
            SkipReason = $"Docker is not available: {ex.GetBaseException().Message}";
            _container = null;
            IsAvailable = false;
        }
    }

    /// <summary>
    /// Applies the committed migration scripts rather than EF's EnsureCreated. Those scripts are
    /// the exact artefacts Liquibase runs on CodeBuild, so these tests fail if one is wrong,
    /// stale, or out of step with the model the stores query through. Grants are skipped: the
    /// throwaway container has no separate DML role.
    /// </summary>
    private async Task CreateSchemaAsync()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "changelog");
        var scripts = Directory.Exists(folder)
            ? Directory.GetFiles(folder, "*.sql")
                .Where(f => !Path.GetFileName(f).Contains("grants", StringComparison.OrdinalIgnoreCase))
                .OrderBy(Path.GetFileName, StringComparer.Ordinal)
                .ToArray()
            : [];

        if (scripts.Length == 0)
        {
            throw new FileNotFoundException(
                "The generated migration scripts are missing. Run 'dotnet dotnet-ef migrations script " +
                "--idempotent --project Cleanse.Storage.Postgres --output changelog/sql/<nnnn>_<name>.sql'.",
                folder);
        }

        foreach (var script in scripts)
        {
            await using var command = DataSource.CreateCommand(await File.ReadAllTextAsync(script));
            await command.ExecuteNonQueryAsync();
        }
    }

    public CleanseDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<CleanseDbContext>().UseNpgsql(DataSource).Options);

    /// <summary>Wipes every table between tests. Cheaper and safer than recreating the schema.</summary>
    public async Task ResetAsync()
    {
        if (!IsAvailable)
        {
            return;
        }

        await using var command = DataSource.CreateCommand(
            "TRUNCATE TABLE issue_history, audit_entries, issues, rule_runs RESTART IDENTITY CASCADE");
        await command.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (DataSource is not null)
        {
            await DataSource.DisposeAsync();
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
