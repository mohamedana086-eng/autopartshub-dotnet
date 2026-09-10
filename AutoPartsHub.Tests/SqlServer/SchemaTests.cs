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

    /// <remarks>
    /// Counted from the model rather than against a number written here. A
    /// hard-coded count is a test that has to be edited every time an entity is
    /// added, and one that gets edited to whatever the failure said.
    /// </remarks>
    [SqlServerFact]
    public async Task EveryTableTheModelDeclaresIsThere()
    {
        var declared = _db.Model.GetEntityTypes()
            .Select(e => e.GetTableName())
            .Where(name => name is not null)
            .Distinct()
            .ToList();

        var inDatabase = (await _db.Database
            .SqlQuery<string>($"""SELECT name AS "Value" FROM sys.tables WHERE name <> '__EFMigrationsHistory'""")
            .ToListAsync()).ToHashSet();

        var absent = declared.Where(name => !inDatabase.Contains(name!)).ToList();

        Assert.True(absent.Count == 0, $"declared and not created: {string.Join(", ", absent)}");
        Assert.True(declared.Count > 30, $"only {declared.Count} tables in the model");
    }

    /// <remarks>
    /// The scaffolded model was missing eleven tables the application queries
    /// every day, and nothing said so — raw SQL does not consult the model, and
    /// against PostgreSQL the columns really were there. This is the assertion
    /// that would have said so, and it is named after what it is for.
    /// </remarks>
    [SqlServerFact]
    public async Task TheTablesTheScaffoldMissedAreThereToo()
    {
        var inDatabase = (await _db.Database
            .SqlQuery<string>($"""SELECT name AS "Value" FROM sys.tables""")
            .ToListAsync()).ToHashSet();

        foreach (var late in new[]
        {
            "ProductSpec", "ProductBarcode", "GoodsCategory", "VinLookup", "SearchMiss",
            "SupplierOffer", "Ticket", "TicketMessage", "PriceListImport", "PriceListImportRow",
        })
        {
            Assert.Contains(late, inDatabase);
        }
    }

    /// <remarks>
    /// The only view, and the only place PostgreSQL's <c>DISTINCT ON</c> had to
    /// become something else. Asserted through the mapped entity rather than
    /// through <c>sys.views</c>, so that the rewrite is checked for producing
    /// the columns the application reads and not merely for existing.
    /// </remarks>
    [SqlServerFact]
    public async Task TheBestOfferViewAnswers()
    {
        var offers = await _db.BestOffers.ToListAsync();

        // Nothing is seeded, so the answer is empty — the point is that the
        // view parses, binds and projects the six columns the entity declares.
        Assert.Empty(offers);
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
