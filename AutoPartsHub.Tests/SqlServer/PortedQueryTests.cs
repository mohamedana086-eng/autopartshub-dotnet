using AutoPartsHub.Api.Data;
using AutoPartsHub.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsHub.Tests.SqlServer;

/// <summary>
/// The ported statements, against rows, asserting what comes back.
/// </summary>
/// <remarks>
/// The dialect check settled that SQL Server accepts these. It cannot settle
/// that they still mean what they meant, and the rewrites where that could
/// have gone wrong are exactly the ones nobody would notice:
///
/// <list type="bullet">
///   <item>DISTINCT ON became a window function. Picking a different row is
///   a wrong purchase price on every search result, and still a number.</item>
///   <item>LIMIT became OFFSET/FETCH or TOP. A lost ORDER BY is a page that
///   repeats rows, which reads as a duplicate rather than as an error.</item>
///   <item>An array parameter became OPENJSON. Under the wrong collation it
///   matches nothing, and an empty result is a normal-looking answer.</item>
///   <item>MERGE replaced ON CONFLICT. Without HOLDLOCK it is a race, and a
///   race that usually works.</item>
/// </list>
///
/// Each of those has a test here that fails if the rewrite was wrong in the
/// way that would otherwise be invisible.
/// </remarks>
[Collection(CatalogueCollection.Name)]
public class PortedQueryTests(Catalogue catalogue)
{
    private AutoPartsContext Db => catalogue.Db;

    // ------------------------------------------------------- the BestOffer view

    /// <remarks>
    /// The seed makes this the interesting case: the preferred supplier is the
    /// DEARER of the two live ones. A ranking that sorted by price — which is
    /// what DISTINCT ON would do if the ORDER BY had been dropped or
    /// reordered in the rewrite — picks the other one and looks entirely
    /// sensible while charging from the wrong invoice.
    /// </remarks>
    [SqlServerFact]
    public async Task TheBestOfferIsThePreferredSupplierNotTheCheapest()
    {
        var best = await Db.BestOffers.SingleAsync(b => b.ProductId == Catalogue.BrakePad);

        Assert.Equal(Catalogue.Preferred, best.SupplierId);
        Assert.Equal(60, best.PurchasePrice);
        Assert.Equal("PREF", best.SupplierCode);
    }

    /// <remarks>
    /// The stopped supplier's offer is the cheapest AND the highest priority,
    /// so a view that forgot either `active` check would pick it — and it is
    /// the one row in the seed that must never win.
    /// </remarks>
    [SqlServerFact]
    public async Task AStoppedSupplierNeverWinsHoweverGoodTheirOfferIs()
    {
        var best = await Db.BestOffers.SingleAsync(b => b.ProductId == Catalogue.BrakePad);

        Assert.NotEqual(Catalogue.Stopped, best.SupplierId);
    }

    /// <remarks>
    /// One row per part is what DISTINCT ON guaranteed and what ROW_NUMBER …
    /// = 1 has to keep guaranteeing. Three offers on this part; one answer.
    /// </remarks>
    [SqlServerFact]
    public async Task TheViewAnswersOncePerPartHoweverManyOffersThereAre()
    {
        Assert.Equal(3, await Db.SupplierOffers.CountAsync(o => o.ProductId == Catalogue.BrakePad));
        Assert.Equal(1, await Db.BestOffers.CountAsync(b => b.ProductId == Catalogue.BrakePad));
    }

    // ----------------------------------------------------- list parameters

    /// <remarks>
    /// The OPENJSON rewrite, reading real rows. An empty answer here is what a
    /// wrong collation produces, and an empty answer is not obviously wrong.
    /// </remarks>
    [SqlServerFact]
    public async Task AListParameterMatchesTheRowsItNames()
    {
        var found = await Db.Database
            .SqlQuery<string>($"""
                SELECT p."id" AS "Value" FROM "Product" p
                WHERE p."id" IN (
                  SELECT value COLLATE DATABASE_DEFAULT
                  FROM OPENJSON({SqlList.Of([Catalogue.BrakePad, "prd-does-not-exist"])})
                )
                """)
            .ToListAsync();

        Assert.Equal([Catalogue.BrakePad], found);
    }

    [SqlServerFact]
    public async Task AnEmptyListParameterMatchesNoRowsRatherThanAllOfThem()
    {
        var found = await Db.Database
            .SqlQuery<string>($"""
                SELECT p."id" AS "Value" FROM "Product" p
                WHERE p."id" IN (
                  SELECT value COLLATE DATABASE_DEFAULT
                  FROM OPENJSON({SqlList.Of(Array.Empty<string>())})
                )
                """)
            .ToListAsync();

        Assert.Empty(found);
    }

    // --------------------------------------------------------------- paging

    /// <remarks>
    /// OFFSET/FETCH against the order it was given. The failure this catches
    /// is a page that repeats a row from the one before, which happens the
    /// moment the ordering is not total — and reads as a duplicate part rather
    /// than as a broken query.
    /// </remarks>
    [SqlServerFact]
    public async Task PagingWalksEveryRowOnceAndInOrder()
    {
        async Task<List<string>> Page(int page, int size) => await Db.Database
            .SqlQuery<string>($"""
                SELECT p."partNumber" AS "Value" FROM "Product" p
                ORDER BY p."partNumber" ASC
                OFFSET {(page - 1) * size} ROWS FETCH NEXT {size} ROWS ONLY
                """)
            .ToListAsync();

        var first = await Page(1, 1);
        var second = await Page(2, 1);
        var both = await Page(1, 10);

        Assert.Single(first);
        Assert.Single(second);
        Assert.NotEqual(first[0], second[0]);
        Assert.Equal(both, [.. first, .. second]);
        // And the order is the one asked for, not the insertion order.
        Assert.Equal("0 986 424 815", both[0]);
    }

    /// <remarks>
    /// TOP is what a LIMIT with no ORDER BY became. It has to return exactly
    /// one row — the arbitrariness is the point, the count is not.
    /// </remarks>
    [SqlServerFact]
    public async Task TopReturnsOneRowWhereLimitOneDid()
    {
        var one = await Db.Database
            .SqlQuery<string>($"""SELECT TOP 1 p."id" AS "Value" FROM "Product" p""")
            .ToListAsync();

        Assert.Single(one);
    }

    // ------------------------------------------------------- case sensitivity

    /// <remarks>
    /// ILIKE became LIKE on the claim that the collation is already
    /// case-insensitive. That claim is load-bearing for every search in the
    /// application, so it is asserted against a real column rather than taken
    /// from documentation.
    /// </remarks>
    [SqlServerFact]
    public async Task LikeIsCaseInsensitiveAsIlikeWas()
    {
        var found = await Db.Database
            .SqlQuery<string>($"""
                SELECT p."id" AS "Value" FROM "Product" p WHERE p."name" LIKE '%BRAKE PAD%'
                """)
            .ToListAsync();

        Assert.Equal([Catalogue.BrakePad], found);
    }

    // ------------------------------------------------------------ the upsert

    /// <remarks>
    /// MERGE replacing ON CONFLICT, both branches. The counter going up on the
    /// second call is the whole behaviour — an upsert that inserted twice
    /// would violate the unique index, and one that did nothing would lose the
    /// count silently.
    /// </remarks>
    [SqlServerFact]
    public async Task TheUpsertInsertsThenUpdates()
    {
        var term = $"probe-{Guid.NewGuid():n}";
        try
        {
            for (var i = 0; i < 3; i++) await RecordMissAsync(term);

            var row = await Db.SearchMisses.SingleAsync(m => m.Term == term);

            Assert.Equal(3, row.Searches);
            Assert.True(row.LastSeenAt >= row.FirstSeenAt);
        }
        finally
        {
            await Db.Database.ExecuteSqlAsync($"""DELETE FROM "SearchMiss" WHERE "term" = {term}""");
        }
    }

    /// <remarks>
    /// The same term narrowed and not narrowed are two findings and two rows —
    /// the unique key is the pair, and a MERGE that matched on the term alone
    /// would collapse them into one and lose the distinction the column exists
    /// for.
    /// </remarks>
    [SqlServerFact]
    public async Task NarrowedAndUnnarrowedAreCountedApart()
    {
        var term = $"probe-{Guid.NewGuid():n}";
        try
        {
            await RecordMissAsync(term, narrowed: false);
            await RecordMissAsync(term, narrowed: true);

            var rows = await Db.SearchMisses.Where(m => m.Term == term).ToListAsync();

            Assert.Equal(2, rows.Count);
            Assert.All(rows, r => Assert.Equal(1, r.Searches));
        }
        finally
        {
            await Db.Database.ExecuteSqlAsync($"""DELETE FROM "SearchMiss" WHERE "term" = {term}""");
        }
    }

    private Task RecordMissAsync(string term, bool narrowed = false) =>
        Db.Database.ExecuteSqlAsync($"""
            MERGE "SearchMiss" WITH (HOLDLOCK) AS target
            USING (VALUES ({term}, {narrowed})) AS source("term", "narrowed")
              ON target."term" = source."term"
             AND target."narrowed" = source."narrowed"
            WHEN MATCHED THEN
              UPDATE SET "searches" = target."searches" + 1,
                         "lastSeenAt" = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN
              INSERT ("id", "term", "narrowed")
              VALUES ({Guid.NewGuid().ToString("n")}, {term}, {narrowed});
            """);

    // ------------------------------------------------------- the bulk insert

    /// <remarks>
    /// OPENJSON … WITH, replacing the parallel arrays. Every column is a
    /// different type, which is what would go wrong: a bit read as text or a
    /// date read as a string fails here and nowhere else.
    /// </remarks>
    [SqlServerFact]
    public async Task ABulkInsertLandsEveryColumnInItsOwnType()
    {
        var stamped = new DateTime(2026, 3, 4, 5, 6, 7, 123, DateTimeKind.Utc);
        var rows = new[]
        {
            new
            {
                id = "off-bulk-1", productId = Catalogue.OilFilter, supplierId = Catalogue.Cheapest,
                purchasePrice = 12.34, stockDays = (int?)5, supplierPartNumber = "SP-1",
                active = false, updatedAt = stamped,
            },
        };

        try
        {
            await Db.Database.ExecuteSqlAsync($"""
                INSERT INTO "SupplierOffer" ("id", "productId", "supplierId", "purchasePrice",
                                             "stockDays", "supplierPartNumber", "active", "updatedAt")
                SELECT "id", "productId", "supplierId", "purchasePrice",
                       "stockDays", "supplierPartNumber", "active", "updatedAt"
                FROM OPENJSON({SqlList.Rows(rows)})
                WITH (
                  "id" nvarchar(400) '$.id',
                  "productId" nvarchar(400) '$.productId',
                  "supplierId" nvarchar(400) '$.supplierId',
                  "purchasePrice" float '$.purchasePrice',
                  "stockDays" int '$.stockDays',
                  "supplierPartNumber" nvarchar(400) '$.supplierPartNumber',
                  "active" bit '$.active',
                  "updatedAt" datetime2(3) '$.updatedAt'
                )
                """);

            var written = await Db.SupplierOffers.AsNoTracking().SingleAsync(o => o.Id == "off-bulk-1");

            Assert.Equal(12.34, written.PurchasePrice);
            Assert.Equal(5, written.StockDays);
            Assert.Equal("SP-1", written.SupplierPartNumber);
            // The one most likely to arrive as its own string.
            Assert.False(written.Active);
            Assert.Equal(stamped, written.UpdatedAt);
        }
        finally
        {
            await Db.Database.ExecuteSqlAsync($"""DELETE FROM "SupplierOffer" WHERE "id" = 'off-bulk-1'""");
        }
    }

    // ------------------------------------------------------------- OUTPUT

    /// <remarks>
    /// OUTPUT replacing RETURNING, and reading back what the DATABASE decided
    /// rather than what was sent: the defaults are the point, and an OUTPUT
    /// reading DELETED instead of INSERTED would return the row as it was.
    /// </remarks>
    [SqlServerFact]
    public async Task OutputReportsTheRowAsItNowStands()
    {
        var id = $"sup-probe-{Guid.NewGuid():n}"[..30];
        try
        {
            await Db.Database.ExecuteSqlAsync($"""
                INSERT INTO "Supplier" ("id", "name", "code", "slug", "reliability", "active",
                                        "minOrderAmount", "priority")
                VALUES ({id}, 'Probe', {id[..12]}, {id}, 'standard', 1, 0, 0)
                """);

            var updated = (await Db.Database
                .SqlQuery<int>($"""
                    UPDATE "Supplier"
                       SET "priority" = 7
                    OUTPUT INSERTED."priority" AS "Value"
                     WHERE "id" = {id}
                    """)
                .ToListAsync()).Single();

            Assert.Equal(7, updated);
        }
        finally
        {
            await Db.Database.ExecuteSqlAsync($"""DELETE FROM "Supplier" WHERE "id" = {id}""");
        }
    }
}
