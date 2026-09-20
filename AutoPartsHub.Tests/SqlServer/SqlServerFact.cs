using AutoPartsHub.Api.Data;
using Microsoft.Data.SqlClient;

namespace AutoPartsHub.Tests.SqlServer;

/// <summary>
/// A test that needs a real SQL Server, and says so rather than failing when
/// there is not one.
/// </summary>
/// <remarks>
/// The rest of this project runs in half a second with nothing installed, and
/// that is worth keeping: a suite that needs a database is a suite people stop
/// running. But porting nineteen hundred lines of PostgreSQL to SQL Server
/// cannot be done by reading — the differences that matter are the ones nobody
/// predicts, and every one found so far was found by an engine refusing
/// something.
///
/// So these tests exist, and they skip when the engine is not there. Skipping
/// is deliberately visible in the runner output: a test that silently passed
/// without connecting would be worse than no test, because the port would look
/// verified.
///
/// The connection is decided once, at discovery, and cached — a probe per test
/// would add a connection attempt to every one of them.
/// </remarks>
public sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (SqlServer.Unavailable is { } why) Skip = why;
    }
}

/// <inheritdoc cref="SqlServerFactAttribute"/>
public sealed class SqlServerTheoryAttribute : TheoryAttribute
{
    public SqlServerTheoryAttribute()
    {
        if (SqlServer.Unavailable is { } why) Skip = why;
    }
}

/// <summary>Where the tests find a SQL Server, and whether there is one.</summary>
public static class SqlServer
{
    /// <summary>
    /// The instance to test against.
    /// </summary>
    /// <remarks>
    /// <c>TEST_SQLSERVER</c> overrides, so that CI can point at a container.
    /// The default is the LocalDB instance a Windows development machine has
    /// once the SQL Server tooling is installed.
    ///
    /// A DIFFERENT DATABASE from the one <see cref="DesignTimeContextFactory"/>
    /// migrates, and deliberately. The seeded tests below empty every table
    /// before they run; pointed at a developer's own database that would throw
    /// away whatever they were in the middle of looking at.
    /// </remarks>
    public static string ConnectionString =>
        Environment.GetEnvironmentVariable("TEST_SQLSERVER") is { Length: > 0 } configured
            ? configured
            : DesignTimeContextFactory.LocalDb.Replace(
                "Database=AutoPartsHub;", "Database=AutoPartsHub_Tests;", StringComparison.Ordinal);

    private static readonly Lazy<string?> Probe = new(() =>
    {
        try
        {
            // Asks whether the SERVER is there, not whether the test database
            // is: the fixture creates that one by running the migrations, so
            // its absence is the normal state on a machine that has never run
            // these. Probing it directly made every test skip on the first run
            // and pass on the second, which is worse than either.
            var master = new SqlConnectionStringBuilder(ConnectionString)
            {
                InitialCatalog = "master",
                ConnectTimeout = 5,
            };

            using var connection = new SqlConnection(master.ConnectionString);
            connection.Open();
            return null;
        }
        catch (Exception cause)
        {
            // The reason, not just "unavailable". A machine with SQL Server
            // installed and the instance stopped reads very differently from
            // one without it, and the message is the only place that shows.
            return $"No SQL Server to test against ({cause.GetType().Name}: {cause.Message.Split('\n')[0]}). "
                 + "Set TEST_SQLSERVER, or run: SqlLocalDB start MSSQLLocalDB";
        }
    });

    /// <summary>Null when there is one; the reason when there is not.</summary>
    public static string? Unavailable => Probe.Value;
}
