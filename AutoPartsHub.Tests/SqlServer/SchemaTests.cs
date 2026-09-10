using AutoPartsHub.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsHub.Tests.SqlServer;

/// <summary>
/// The SQL Server schema, against a SQL Server.
/// </summary>
/// <remarks>
/// Every difference between the two engines found during this move was found
/// by the engine refusing something — a self-referencing foreign key it would
/// not cascade, a filtered index predicate it would not parse, forty-three
/// columns it would not let anybody index. None of the three was visible by
/// reading the model, and none would have been caught by a test that did not
/// connect.
/// </remarks>
public class SchemaTests : IAsyncLifetime
{
    private AutoPartsContext _db = null!;

    public Task InitializeAsync()
    {
        if (SqlServer.Unavailable is not null) return Task.CompletedTask;

        _db = new AutoPartsContext(new DbContextOptionsBuilder<AutoPartsContext>()
            .UseSqlServer(SqlServer.ConnectionString)
            .Options);

        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _db?.DisposeAsync().AsTask() ?? Task.CompletedTask;

    [SqlServerFact]
    public async Task TheMigrationsAreAllApplied()
    {
        var pending = await _db.Database.GetPendingMigrationsAsync();

        Assert.True(
            !pending.Any(),
            $"not applied: {string.Join(", ", pending)}. Run: dotnet ef database update --project AutoPartsHub.Api");
    }

    /// <summary>
    /// The whole of the port rests on this.
    /// </summary>
    /// <remarks>
    /// There are around a hundred and seventy raw statements in this
    /// application and they all quote identifiers the PostgreSQL way —
    /// <c>SELECT p."partNumber" FROM "Product" p</c>. SQL Server reads that
    /// identically, but only with QUOTED_IDENTIFIER ON, and with it OFF the
    /// same text is a string literal and a syntax error.
    ///
    /// SqlClient turns it on for every connection it opens, which is why the
    /// identifiers in all nineteen hundred lines of SQL did not have to be
    /// touched — only the dialect around them. It is asserted rather than
    /// trusted because it is the assumption the size of this whole piece of
    /// work was estimated from, and because a connection string or a server
    /// default could in principle change it.
    /// </remarks>
    [SqlServerFact]
    public async Task PostgresStyleQuotingReadsTheSameHere()
    {
        var setting = (await _db.Database
            .SqlQuery<int>($"SELECT CAST(SESSIONPROPERTY('QUOTED_IDENTIFIER') AS int) AS \"Value\"")
            .ToListAsync()).Single();

        Assert.Equal(1, setting);

        // And in practice, not only in principle: a real table, quoted the way
        // every query in this application quotes it.
        var counted = (await _db.Database
            .SqlQuery<int>($"""SELECT COUNT(*) AS "Value" FROM "Product" p WHERE p."partNumber" IS NOT NULL""")
            .ToListAsync()).Single();

        Assert.True(counted >= 0);
    }

    [SqlServerFact]
    public async Task EveryTableTheModelDeclaresIsThere()
    {
        var tables = await _db.Database
            .SqlQuery<string>($"""SELECT name AS "Value" FROM sys.tables WHERE name <> '__EFMigrationsHistory'""")
            .ToListAsync();

        Assert.Equal(27, tables.Count);
        foreach (var expected in new[] { "Product", "Supplier", "Client", "Order", "MarkupRule", "PriceList" })
        {
            Assert.Contains(expected, tables);
        }
    }

    /// <remarks>
    /// The reason the text convention exists. An <c>nvarchar(max)</c> cannot be
    /// an index key, and the columns that arrived as one are exactly the ones
    /// the catalogue looks parts up by. One is left: the WMI codes are a list,
    /// mapped to JSON here because SQL Server has no array type, and a JSON
    /// document is not something anybody indexes by key.
    /// </remarks>
    [SqlServerFact]
    public async Task NothingIsUnboundedExceptTheOneThingThatHasToBe()
    {
        var unbounded = await _db.Database.SqlQuery<string>($"""
            SELECT t.name + '.' + c.name AS "Value"
            FROM sys.columns c
            JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            JOIN sys.tables t ON t.object_id = c.object_id
            WHERE ty.name = 'nvarchar' AND c.max_length = -1
            """).ToListAsync();

        Assert.Equal(["VehicleMake.wmiCodes"], unbounded);
    }

    /// <remarks>
    /// Not decoration: the storefront and this application both write these
    /// timestamps, and the other one writes JavaScript milliseconds. A column
    /// that rounded to the second would make two rows written together compare
    /// as different times.
    /// </remarks>
    [SqlServerFact]
    public async Task TimestampsKeepTheirMilliseconds()
    {
        var wrong = await _db.Database.SqlQuery<string>($"""
            SELECT t.name + '.' + c.name AS "Value"
            FROM sys.columns c
            JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            JOIN sys.tables t ON t.object_id = c.object_id
            WHERE ty.name = 'datetime2' AND c.scale <> 3
            """).ToListAsync();

        Assert.Empty(wrong);
    }
}
