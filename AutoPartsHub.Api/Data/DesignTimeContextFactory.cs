using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AutoPartsHub.Api.Data;

/// <summary>
/// The context <c>dotnet ef</c> builds against when it writes a migration.
/// </summary>
/// <remarks>
/// Without one of these the tooling starts the application to find a context,
/// which means a migration cannot be written without a reachable database and
/// a full set of environment variables. Neither is true on a machine that is
/// only generating SQL — and the connection here is never opened, because
/// writing a migration is a comparison between the model and the last
/// migration, not between the model and a server.
///
/// SQL SERVER, ALWAYS
/// ------------------
/// The migrations in this repository are SQL Server's, whatever the running
/// deployment is pointed at. PostgreSQL's schema is not ours to migrate — it
/// belongs to the Prisma migrations in the storefront repository, and the two
/// applications share it. So this deliberately does not read
/// <c>DATABASE_PROVIDER</c>: a developer with the old value still exported
/// would otherwise generate PostgreSQL migrations into a folder that means
/// SQL Server, and nothing would say so until they were applied.
///
/// <c>DATABASE_URL</c> is honoured when it names a SQL Server, so that
/// <c>dotnet ef database update</c> reaches the right one; otherwise it falls
/// back to the LocalDB instance the tests use.
/// </remarks>
public sealed class DesignTimeContextFactory : IDesignTimeDbContextFactory<AutoPartsContext>
{
    /// <summary>What a developer machine has when nothing says otherwise.</summary>
    public const string LocalDb =
        "Server=(localdb)\\MSSQLLocalDB;Database=AutoPartsHub;Trusted_Connection=True;TrustServerCertificate=True";

    public AutoPartsContext CreateDbContext(string[] args)
    {
        var configured = Environment.GetEnvironmentVariable("DATABASE_URL");

        // Only a SQL Server string is used. A PostgreSQL one left in the
        // environment is ignored rather than passed to a provider that cannot
        // parse it, so that generating a migration works on a machine still
        // set up for the old engine.
        var connection =
            !string.IsNullOrWhiteSpace(configured)
            && ConnectionString.ProviderFor(null, configured) == DatabaseProvider.SqlServer
                ? configured
                : LocalDb;

        return new AutoPartsContext(
            new DbContextOptionsBuilder<AutoPartsContext>().UseSqlServer(connection).Options);
    }
}
