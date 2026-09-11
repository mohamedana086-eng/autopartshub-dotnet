using AutoPartsHub.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsHub.Tests.SqlServer;

/// <summary>
/// What SQL Server actually throws, provoked rather than looked up.
/// </summary>
/// <remarks>
/// Three places in this application catch a failed write and do something
/// other than let it become a 500: two retry a reference collision, and one
/// turns a CHECK violation on the shelves into a sentence telling an admin to
/// run the reconciliation. All three were written against PostgreSQL's
/// SQLSTATE and would simply stop firing against SQL Server — which is the
/// worst shape of bug available here, because those paths only run when
/// something has already gone wrong and are the least likely to be tried by
/// hand before a cutover.
///
/// So the numbers are asserted by making the database refuse something, not
/// by citing a table of error codes. Every one of these tests fails if
/// Microsoft changes a number, which is the point: the alternative is finding
/// out from a stack trace an admin sent in.
/// </remarks>
[Collection(CatalogueCollection.Name)]
public class DatabaseRefusalTests(Catalogue catalogue)
{
    private AutoPartsContext Db => catalogue.Db;

    /// <summary>Runs something that will fail, and hands back what was thrown.</summary>
    private async Task<Exception> Refused(Func<Task> write)
    {
        try
        {
            await write();
        }
        catch (Exception thrown)
        {
            return thrown;
        }

        throw new Xunit.Sdk.XunitException("the database accepted a write that should have been refused");
    }

    /// <remarks>
    /// A unique index, which is what both reference collisions hit. SQL Server
    /// answers 2601 for an index and 2627 for a constraint; this schema
    /// declares uniqueness both ways, so both have to classify the same.
    /// </remarks>
    [SqlServerFact]
    public async Task ADuplicateOnAUniqueIndexIsAUniqueRefusal()
    {
        var term = $"probe-{Guid.NewGuid():n}";
        try
        {
            await Db.Database.ExecuteSqlAsync($"""
                INSERT INTO "SearchMiss" ("id", "term", "narrowed")
                VALUES ({Guid.NewGuid().ToString("n")}, {term}, 0)
                """);

            var thrown = await Refused(() => Db.Database.ExecuteSqlAsync($"""
                INSERT INTO "SearchMiss" ("id", "term", "narrowed")
                VALUES ({Guid.NewGuid().ToString("n")}, {term}, 0)
                """));

            Assert.Equal(DatabaseRefusal.Unique, DatabaseRefusals.Of(thrown));
            Assert.True(thrown.Is(DatabaseRefusal.Unique));
        }
        finally
        {
            await Db.Database.ExecuteSqlAsync($"""DELETE FROM "SearchMiss" WHERE "term" = {term}""");
        }
    }

    /// <remarks>
    /// The GoodsCategory floor constraint: a markup floor is only allowed
    /// alongside PERCENT_MIN. This is the same KIND of refusal the stock
    /// CHECK produces, and the one the order endpoint words for an admin.
    /// </remarks>
    [SqlServerFact]
    public async Task ACheckConstraintIsACheckRefusal()
    {
        var thrown = await Refused(() => Db.Database.ExecuteSqlAsync($"""
            INSERT INTO "GoodsCategory" ("id", "name", "slug", "markupType", "markupValue",
                                         "markupMinAmount", "sortOrder", "active")
            VALUES ({Guid.NewGuid().ToString("n")}, 'Probe', {Guid.NewGuid().ToString("n")},
                    'PERCENT', 10, 5, 0, 1)
            """));

        Assert.Equal(DatabaseRefusal.Check, DatabaseRefusals.Of(thrown));
    }

    /// <summary>
    /// The one that shares its number with CHECK.
    /// </summary>
    /// <remarks>
    /// SQL Server answers 547 for both, and only the message says which. Told
    /// apart because the consequence differs: a CHECK on the shelves tells an
    /// admin to run the stock reconciliation, and running it for a row that
    /// merely named a supplier who no longer exists would send them looking
    /// for a discrepancy that is not there.
    /// </remarks>
    [SqlServerFact]
    public async Task AForeignKeyIsNotMistakenForACheck()
    {
        var thrown = await Refused(() => Db.Database.ExecuteSqlAsync($"""
            INSERT INTO "SupplierOffer" ("id", "productId", "supplierId", "purchasePrice",
                                         "active", "createdAt", "updatedAt")
            VALUES ({Guid.NewGuid().ToString("n")}, 'prd-does-not-exist', {Catalogue.Cheapest},
                    1, 1, SYSUTCDATETIME(), SYSUTCDATETIME())
            """));

        Assert.Equal(DatabaseRefusal.ForeignKey, DatabaseRefusals.Of(thrown));
        Assert.False(thrown.Is(DatabaseRefusal.Check));
    }

    /// <remarks>
    /// EF wraps whatever the provider threw, so a write made through
    /// SaveChanges arrives in a different shape from one made through raw SQL.
    /// Both reach these catches in the application, so both have to classify.
    /// </remarks>
    [SqlServerFact]
    public async Task AnExceptionEntityFrameworkWrappedIsClassifiedToo()
    {
        var term = $"probe-{Guid.NewGuid():n}";
        try
        {
            await Db.Database.ExecuteSqlAsync($"""
                INSERT INTO "SearchMiss" ("id", "term", "narrowed")
                VALUES ({Guid.NewGuid().ToString("n")}, {term}, 0)
                """);

            using var second = new AutoPartsContext(new DbContextOptionsBuilder<AutoPartsContext>()
                .UseSqlServer(SqlServer.ConnectionString).Options);

            second.SearchMisses.Add(new Api.Data.Entities.SearchMiss
            {
                Id = Guid.NewGuid().ToString("n"), Term = term, Narrowed = false,
                Searches = 1, FirstSeenAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow,
            });

            var thrown = await Refused(() => second.SaveChangesAsync());

            Assert.IsType<DbUpdateException>(thrown);
            Assert.Equal(DatabaseRefusal.Unique, DatabaseRefusals.Of(thrown));
        }
        finally
        {
            await Db.Database.ExecuteSqlAsync($"""DELETE FROM "SearchMiss" WHERE "term" = {term}""");
        }
    }

    /// <remarks>
    /// Anything else is not a refusal this application words. A timeout or a
    /// dropped connection has to keep being a 500 — retrying a reference on a
    /// dead connection would spin four times and then report the wrong thing.
    /// </remarks>
    [Fact]
    public void SomethingElseIsNotARefusal()
    {
        Assert.Equal(DatabaseRefusal.None, DatabaseRefusals.Of(new InvalidOperationException("nope")));
        Assert.Equal(DatabaseRefusal.None, DatabaseRefusals.Of(new TimeoutException()));
        Assert.Equal(DatabaseRefusal.None, DatabaseRefusals.Of(null));
    }
}
