using AutoPartsHub.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsHub.Tests.SqlServer;

/// <summary>
/// Crossing a failed search off the buying report.
/// </summary>
/// <remarks>
/// <c>SearchMiss</c> records what customers looked for and did not find. It
/// was a list with no way to cross anything off, so a term stocked last month
/// stayed at the top of it — the ranking is by how often it was ever searched,
/// and the only way to know it had been handled was to remember.
///
/// The rules worth asserting are the three that are easy to get subtly wrong:
/// resolving is whole or not at all, resolving twice keeps the first decision,
/// and nothing reopens itself.
/// </remarks>
[Collection(CatalogueCollection.Name)]
public class FailedSearchTests(Catalogue catalogue)
{
    private AutoPartsContext Db => catalogue.Db;

    /// <summary>A failed search and an admin, both of this test's own.</summary>
    private async Task WithMiss(Func<string, string, Task> body)
    {
        var id = $"miss-{Guid.NewGuid():n}";
        var admin = $"cli-adm-{Guid.NewGuid():n}";

        await Db.Database.ExecuteSqlAsync($"""
            INSERT INTO "Client" ("id", "name", "email", "role", "discountPercent")
            VALUES ({admin}, 'Probe Admin', {admin + "@example.test"}, 'ADMIN', 0)
            """);
        await Db.Database.ExecuteSqlAsync($"""
            INSERT INTO "SearchMiss" ("id", "term", "narrowed", "searches")
            VALUES ({id}, {"probe-" + id}, 0, 7)
            """);
        try
        {
            await body(id, admin);
        }
        finally
        {
            await Db.Database.ExecuteSqlAsync($"""DELETE FROM "SearchMiss" WHERE "id" = {id}""");
            await Db.Database.ExecuteSqlAsync($"""DELETE FROM "Client" WHERE "id" = {admin}""");
        }
    }

    private record Resolution(DateTime? ResolvedAt, string? ResolvedById, DateTime LastSeenAt);

    private async Task<Resolution> Read(string id) =>
        (await Db.Database.SqlQuery<Resolution>($"""
            SELECT "resolvedAt" AS "ResolvedAt", "resolvedById" AS "ResolvedById",
                   "lastSeenAt" AS "LastSeenAt"
            FROM "SearchMiss" WHERE "id" = {id}
            """).ToListAsync()).Single();

    /// <summary>The same statement the endpoint sends.</summary>
    private Task ResolveAsync(string id, string? by) =>
        Db.Database.ExecuteSqlAsync($"""
            UPDATE "SearchMiss"
               SET "resolvedAt" = CASE WHEN {by is null} = 1 THEN NULL
                                       ELSE COALESCE("resolvedAt", SYSUTCDATETIME()) END,
                   "resolvedById" = CASE WHEN {by is null} = 1 THEN NULL
                                         ELSE COALESCE("resolvedById", {by}) END
             WHERE "id" = {id}
            """);

    // ------------------------------------------------------------ the basics

    [SqlServerFact]
    public Task EveryTermStartsOpen() => WithMiss(async (id, _) =>
    {
        var row = await Read(id);

        Assert.Null(row.ResolvedAt);
        Assert.Null(row.ResolvedById);
    });

    [SqlServerFact]
    public Task ResolvingRecordsWhenAndByWhom() => WithMiss(async (id, admin) =>
    {
        await ResolveAsync(id, admin);

        var row = await Read(id);

        Assert.NotNull(row.ResolvedAt);
        Assert.Equal(admin, row.ResolvedById);
    });

    [SqlServerFact]
    public Task ReopeningClearsBoth() => WithMiss(async (id, admin) =>
    {
        await ResolveAsync(id, admin);
        await ResolveAsync(id, null);

        var row = await Read(id);

        Assert.Null(row.ResolvedAt);
        Assert.Null(row.ResolvedById);
    });

    /// <summary>
    /// Resolving twice keeps the first decision.
    /// </summary>
    /// <remarks>
    /// The date on the row should be when the term was dealt with, not when
    /// somebody last pressed the button. Two admins looking at the same report
    /// is the ordinary case, and restamping would quietly rewrite who decided
    /// and when.
    /// </remarks>
    [SqlServerFact]
    public Task ResolvingAgainDoesNotRestampIt() => WithMiss(async (id, admin) =>
    {
        await ResolveAsync(id, admin);
        var first = await Read(id);

        var second = $"cli-adm-{Guid.NewGuid():n}";
        await Db.Database.ExecuteSqlAsync($"""
            INSERT INTO "Client" ("id", "name", "email", "role", "discountPercent")
            VALUES ({second}, 'Second Admin', {second + "@example.test"}, 'ADMIN', 0)
            """);
        try
        {
            await ResolveAsync(id, second);

            var after = await Read(id);

            Assert.Equal(first.ResolvedAt, after.ResolvedAt);
            Assert.Equal(admin, after.ResolvedById);
        }
        finally
        {
            await Db.Database.ExecuteSqlAsync($"""DELETE FROM "SearchMiss" WHERE "resolvedById" = {second}""");
            await Db.Database.ExecuteSqlAsync($"""DELETE FROM "Client" WHERE "id" = {second}""");
        }
    });

    // ----------------------------------------------------- nothing reopens itself

    /// <summary>
    /// A resolved term searched again keeps climbing and stays resolved.
    /// </summary>
    /// <remarks>
    /// The judgement stands; the screen shows that it may want revisiting.
    /// Reopening automatically would make "we are not going to sell this"
    /// impossible to say once — it would come back every time somebody asked.
    /// </remarks>
    [SqlServerFact]
    public Task ASearchAfterResolutionKeepsClimbingAndStaysResolved() => WithMiss(async (id, admin) =>
    {
        await ResolveAsync(id, admin);
        var resolved = await Read(id);

        // The same upsert the search runs when a term misses again.
        await Db.Database.ExecuteSqlAsync($"""
            UPDATE "SearchMiss"
               SET "searches" = "searches" + 1, "lastSeenAt" = DATEADD(day, 1, SYSUTCDATETIME())
             WHERE "id" = {id}
            """);

        var after = await Read(id);

        Assert.Equal(resolved.ResolvedAt, after.ResolvedAt);
        Assert.Equal(admin, after.ResolvedById);
        // And this is what the endpoint reports as seenSinceResolved.
        Assert.True(after.LastSeenAt > after.ResolvedAt);
    });

    // ------------------------------------------------------- the constraints

    /// <summary>
    /// A resolution whose author has left is a whole record, not half of one.
    /// </summary>
    /// <remarks>
    /// This file first carried a CHECK pairing the two columns — "a date with
    /// no decider is half a record of a decision" — and a test asserting the
    /// database refused it. Both were wrong, and the engine said so: SET NULL
    /// on the foreign key produces exactly that state, deliberately, the
    /// moment an admin leaves, so the constraint and the key contradicted each
    /// other and the DELETE below was refused.
    ///
    /// The constraint went. What is asserted instead is the state it forbade,
    /// because that state is the point.
    /// </remarks>
    [SqlServerFact]
    public Task ADateWithNobodyBehindItIsAllowed() => WithMiss(async (id, _) =>
    {
        await Db.Database.ExecuteSqlAsync($"""
            UPDATE "SearchMiss" SET "resolvedAt" = SYSUTCDATETIME() WHERE "id" = {id}
            """);

        Assert.NotNull((await Read(id)).ResolvedAt);
        Assert.Null((await Read(id)).ResolvedById);
    });

    /// <summary>
    /// Deleting the admin who resolved it keeps the resolution.
    /// </summary>
    /// <remarks>
    /// SET NULL rather than CASCADE: losing the resolution because somebody
    /// left would quietly refill the buying report with things already handled.
    /// It is the only relation to Client on this table, so SET NULL is
    /// available here where the grant tables had to take NO ACTION.
    /// </remarks>
    [SqlServerFact]
    public Task DeletingTheAdminKeepsTheDecisionAndForgetsTheName() => WithMiss(async (id, admin) =>
    {
        await ResolveAsync(id, admin);

        await Db.Database.ExecuteSqlAsync($"""DELETE FROM "Client" WHERE "id" = {admin}""");

        var row = await Read(id);

        Assert.NotNull(row.ResolvedAt);
        Assert.Null(row.ResolvedById);
    });

    /// <remarks>
    /// The filtered index the open report reads. Asserted because a filter
    /// written the other way round — or dropped — is invisible until the
    /// resolved pile is large enough to matter, which is exactly when nobody
    /// is looking for it.
    /// </remarks>
    [SqlServerFact]
    public async Task TheOpenReportHasAnIndexOfItsOwn()
    {
        var filter = (await Db.Database.SqlQuery<string>($"""
            SELECT i."filter_definition" AS "Value"
            FROM sys.indexes i
            WHERE i."object_id" = OBJECT_ID('SearchMiss') AND i."name" = 'SearchMiss_open_idx'
            """).ToListAsync()).SingleOrDefault();

        Assert.NotNull(filter);
        Assert.Contains("resolvedAt", filter);
        Assert.Contains("IS NULL", filter);
    }
}
