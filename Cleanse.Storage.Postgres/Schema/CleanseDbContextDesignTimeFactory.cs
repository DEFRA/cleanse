using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Cleanse.Storage.Postgres.Schema;

/// <summary>
/// Used only by <c>dotnet ef</c> when generating migrations. The connection string is never
/// opened: migrations are produced as SQL and applied by CDP, never by this process.
/// </summary>
public sealed class CleanseDbContextDesignTimeFactory : IDesignTimeDbContextFactory<CleanseDbContext>
{
    public CleanseDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<CleanseDbContext>()
            .UseNpgsql("Host=localhost;Database=cleanse;Username=cleanse")
            .Options);
}
