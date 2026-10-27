using System.Text.RegularExpressions;
using Cleanse.Core.Abstractions;
using Cleanse.Core.Domain;
using Cleanse.Storage.Sqlite.Schema;
using Cleanse.Storage.Sqlite.Stores;
using Microsoft.Data.Sqlite;

namespace Cleanse.Storage.Sqlite.Test;

public sealed class SqliteUserPersistenceTests
{
    [Fact]
    public async Task Existing_local_database_is_upgraded_idempotently_and_users_survive_reopening()
    {
        Directory.CreateDirectory("artifacts");
        var path = Path.GetFullPath(Path.Combine("artifacts", $"users-{Guid.NewGuid():N}.sqlite"));
        var options = new CleanseSqliteOptions { DataSource = path };
        try
        {
            await using (var connection = new SqliteConnection(options.BuildConnectionString()))
            {
                await connection.OpenAsync();
                await using var stream = typeof(CleanseSqliteDatabase).Assembly.GetManifestResourceStream(
                    "Cleanse.Storage.Sqlite.Schema.schema.sql")!;
                using var reader = new StreamReader(stream);
                var schema = await reader.ReadToEndAsync();
                schema = schema[..schema.IndexOf("CREATE TABLE IF NOT EXISTS users (", StringComparison.Ordinal)];
                schema = Regex.Replace(schema, @"^\s*assigned_user_(id|full_name).*\r?\n", "", RegexOptions.Multiline);
                schema = Regex.Replace(schema, @"^\s*(tenant_id|client_id).*\r?\n", "", RegexOptions.Multiline);
                await using var create = connection.CreateCommand();
                create.CommandText = schema;
                await create.ExecuteNonQueryAsync();
                create.CommandText = """
                    INSERT INTO issues
                        (id, group_id, rule_id, run_id, status, workflow_status, assigned_to,
                         created_at_utc, updated_at_utc, activated_at_utc, version)
                    VALUES ('rule:legacy', 'group', 'rule', 'run', 'active', 'todo', 'legacy-name',
                            $now, $now, $now, 1)
                    """;
                create.Parameters.AddWithValue("$now", DateTimeOffset.UnixEpoch.ToText());
                await create.ExecuteNonQueryAsync();
            }

            var login = new KnownUser(Guid.NewGuid(), "https://issuer.example.test", "subject", "tenant", "object",
                "client", "Alex Example", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
            await using (var database = new CleanseSqliteDatabase(options))
            {
                await database.InitialiseAsync();
                await database.InitialiseAsync();
                var issues = new SqliteIssueStore(database, options);
                var legacy = await issues.GetAsync(IssueId.From("rule:legacy"));
                Assert.Equal("legacy-name", legacy!.AssignedTo);
                Assert.Null(legacy.AssignedUserId);
                await new SqliteUserStore(database).UpsertLoginAsync(login);
                legacy.AssignKnownUser(login, DateTimeOffset.UnixEpoch);
                await issues.SaveAsync(legacy, 1);
            }

            await using (var reopened = new CleanseSqliteDatabase(options))
            {
                await reopened.InitialiseAsync();
                var users = new SqliteUserStore(reopened);
                Assert.Equal(login, await users.GetAsync(login.Id, "tenant", "client"));
                var issue = await new SqliteIssueStore(reopened, options).GetAsync(IssueId.From("rule:legacy"));
                Assert.Equal(login.Id, issue!.AssignedUserId);
                Assert.Equal(login.FullName, issue.AssignedUserFullName);
                var relogin = await users.UpsertLoginAsync(login with
                {
                    Id = Guid.NewGuid(), LastLoggedInAtUtc = DateTimeOffset.UnixEpoch.AddDays(1)
                });
                Assert.Equal(login.Id, relogin.Id);
                Assert.Equal(login.FirstLoggedInAtUtc, relogin.FirstLoggedInAtUtc);
                await new SqliteDataResetStore(reopened).ResetAsync(null, "test", DateTimeOffset.UnixEpoch);
                Assert.Equal(1, (await users.SearchAsync("tenant", "client", PageRequest.Default)).TotalCount);
            }
        }
        finally
        {
            using var connection = new SqliteConnection(options.BuildConnectionString());
            SqliteConnection.ClearPool(connection);
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                File.Delete(path + suffix);
            }
        }
    }
}
