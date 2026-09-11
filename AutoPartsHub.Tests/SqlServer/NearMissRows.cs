using AutoPartsHub.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsHub.Tests.SqlServer;

/// <summary>
/// Enough of a catalogue that the optimizer has to choose.
/// </summary>
/// <remarks>
/// The seeded fixture holds two products, which is right for asking what a
/// statement RETURNS and useless for asking how it gets there: at two rows
/// every plan is a scan, because reading the whole table costs one page and no
/// index can beat that. A test asserting "it seeks" against two rows would
/// fail while the code was correct — and, far worse, one asserting "it does
/// not scan" would have to be weakened until it passed, which means weakened
/// until it asserted nothing.
///
/// So these rows exist only while the near-miss tests run. All three tables
/// the search touches get volume, because a scan of any of them is a scan:
/// the point is not which table is large today but which one grows.
///
/// A CLASS fixture rather than a collection one, deliberately. Test classes
/// inside a collection run one after another, so these rows are gone before
/// the next class starts — which matters, because the paging test next door
/// asserts that a page of ten holds exactly the two seeded products.
///
/// The row counters are <c>sys.all_objects</c> cross-joined rather than
/// <c>GENERATE_SERIES</c>, which this build does not recognise at
/// compatibility level 170 — the same substitution the schema tests make, and
/// for the same reason.
/// </remarks>
public sealed class NearMissRows : IAsyncLifetime
{
    /// <summary>Every row this fixture owns starts with it.</summary>
    public const string Prefix = "bulk-";

    /// <summary>Past the point where reading the whole table is free.</summary>
    private const int Products = 3000;

    private const int CrossReferences = 3000;

    /// <summary>
    /// Fewer, because brands are — a catalogue holds thousands of parts per
    /// brand. Still enough that reading all of them is a decision.
    /// </summary>
    private const int Brands = 500;

    public AutoPartsContext Db { get; private set; } = null!;

    /// <summary>The statements the near-miss search sent, with their parameters.</summary>
    public PlanCapture Plans { get; } = new();

    public async Task InitializeAsync()
    {
        if (SqlServer.Unavailable is not null) return;

        Db = new AutoPartsContext(new DbContextOptionsBuilder<AutoPartsContext>()
            .UseSqlServer(SqlServer.ConnectionString)
            .AddInterceptors(Plans)
            .Options);

        // The seeded fixture has migrated and seeded by the time a test class
        // is constructed, so the manufacturer and the vehicle system these
        // rows point at are already there.
        await Db.Database.ExecuteSqlAsync($"""
            INSERT INTO "Manufacturer" ("id", "name", "isOEM")
            SELECT CONCAT({Prefix}, 'man-', n), CONCAT('Filler brand ', n), 0
            FROM (SELECT TOP ({Brands}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n
                  FROM sys.all_objects a CROSS JOIN sys.all_objects b) numbered
            """);

        await Db.Database.ExecuteSqlAsync($"""
            INSERT INTO "Product" ("id", "partNumber", "name", "manufacturerId",
                                   "vehicleSystemId", "basePrice")
            SELECT CONCAT({Prefix}, n),
                   -- Numbers and names sharing no prefix with the seeded rows,
                   -- so a lane reaching these instead of the row it was asked
                   -- for is visible rather than merely wrong by one.
                   CONCAT('BX-', n), CONCAT('Filler part ', n),
                   'man-bosch', 'sys-brakes', 10
            FROM (SELECT TOP ({Products}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n
                  FROM sys.all_objects a CROSS JOIN sys.all_objects b) numbered
            """);

        await Db.Database.ExecuteSqlAsync($"""
            INSERT INTO "Interchange" ("id", "sourceId", "targetPartNo",
                                       "targetManufacturer", "exactMatch", "isOEM")
            SELECT CONCAT({Prefix}, 'ich-', n), CONCAT({Prefix}, n),
                   CONCAT('IX-', n), 'Filler brand 1', 0, 0
            FROM (SELECT TOP ({CrossReferences}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n
                  FROM sys.all_objects a CROSS JOIN sys.all_objects b) numbered
            """);

        // Statistics, so the optimizer is choosing from what is there rather
        // than from what was there when these tables held two rows and none.
        // Without this the plan assertions are testing the sampling schedule.
        await ResampleAsync();

        Plans.Clear();
    }

    private async Task ResampleAsync()
    {
        foreach (var table in new[] { "Product", "Interchange", "Manufacturer" })
        {
            var statement = "UPDATE STATISTICS \"" + table + "\"";
            await Db.Database.ExecuteSqlRawAsync(statement);
        }
    }

    public async Task DisposeAsync()
    {
        if (Db is null) return;

        var owned = Prefix + "%";

        // Cross-references first: they point at the products.
        await Db.Database.ExecuteSqlAsync($"""DELETE FROM "Interchange" WHERE "id" LIKE {owned}""");
        await Db.Database.ExecuteSqlAsync($"""DELETE FROM "Product" WHERE "id" LIKE {owned}""");
        await Db.Database.ExecuteSqlAsync($"""DELETE FROM "Manufacturer" WHERE "id" LIKE {owned}""");

        await ResampleAsync();
        await Db.DisposeAsync();
    }
}
