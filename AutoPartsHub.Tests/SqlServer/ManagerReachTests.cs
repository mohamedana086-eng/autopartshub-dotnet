using AutoPartsHub.Api.Auth;
using AutoPartsHub.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsHub.Tests.SqlServer;

/// <summary>
/// The two tables <c>ManagerReach</c> was waiting for.
/// </summary>
/// <remarks>
/// Every guard and every scoped query in this application already handled all
/// three degrees; only Own was reachable, because there was nowhere to record
/// the other two. These assert that the storage now reaches them and — the
/// part worth having a database for — that the constraints refuse what the
/// design says they should.
///
/// The rule this file exists to protect is the first one: <b>an absent row
/// reads as Own</b>. It is what makes landing the tables safe, because it
/// means the migration grants nobody anything. A resolver that treated a
/// missing row as an error, or as All, would have quietly changed what every
/// salesperson could see on the day it shipped.
/// </remarks>
[Collection(CatalogueCollection.Name)]
public class ManagerReachTests(Catalogue catalogue)
{
    private AutoPartsContext Db => catalogue.Db;

    private ManagerReachLoader Reaches => new(Db);

    /// <summary>A salesperson and a customer of this test's own.</summary>
    private async Task WithPeople(Func<string, string, Task> body)
    {
        var manager = $"cli-mgr-{Guid.NewGuid():n}";
        var customer = $"cli-cus-{Guid.NewGuid():n}";

        await Db.Database.ExecuteSqlAsync($"""
            INSERT INTO "Client" ("id", "name", "email", "role", "discountPercent")
            VALUES ({manager}, 'Probe Manager', {manager + "@example.test"}, 'SALES', 0),
                   ({customer}, 'Probe Customer', {customer + "@example.test"}, 'RETAIL', 0)
            """);
        try
        {
            await body(manager, customer);
        }
        finally
        {
            await Db.Database.ExecuteSqlAsync($"""
                DELETE FROM "ExtraClient" WHERE "managerId" IN ({manager}, {customer})
                                             OR "clientId" IN ({manager}, {customer})
                """);
            await Db.Database.ExecuteSqlAsync($"""
                DELETE FROM "ManagerAccess" WHERE "managerId" IN ({manager}, {customer})
                """);
            await Db.Database.ExecuteSqlAsync($"""
                DELETE FROM "Client" WHERE "id" IN ({manager}, {customer})
                """);
        }
    }

    private Task GrantAsync(string managerId, string reach) =>
        Db.Database.ExecuteSqlAsync($"""
            INSERT INTO "ManagerAccess" ("id", "managerId", "reach")
            VALUES ({Guid.NewGuid().ToString("n")}, {managerId}, {reach})
            """);

    private Task GrantClientAsync(string managerId, string clientId) =>
        Db.Database.ExecuteSqlAsync($"""
            INSERT INTO "ExtraClient" ("id", "managerId", "clientId")
            VALUES ({Guid.NewGuid().ToString("n")}, {managerId}, {clientId})
            """);

    private static Scope Staff(string id) =>
        Scope.From(new SessionPayload(id, Roles.Sales, null, "Probe Manager", Later));

    private static long Later => DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds();

    // ------------------------------------------------- the rule that matters

    /// <summary>
    /// A salesperson with no row keeps exactly what they had.
    /// </summary>
    /// <remarks>
    /// The migration inserts nothing, so this is every salesperson on the day
    /// it lands. Own means the accounts naming them in <c>salesManagerId</c>,
    /// which is what the scoped queries already narrow by.
    /// </remarks>
    [SqlServerFact]
    public Task NoRowMeansTheAccountsThatNameThem() => WithPeople(async (manager, _) =>
    {
        var scope = await Reaches.ResolveAsync(Staff(manager));

        Assert.Equal(ManagerReach.Own, scope.Reach);
        Assert.Empty(scope.ExtraClientIds);
        // Narrowed to themselves, which is what a WHERE reads.
        Assert.Equal(manager, scope.ManagedBy);
    });

    [SqlServerFact]
    public Task AllReachesEveryCustomer() => WithPeople(async (manager, customer) =>
    {
        await GrantAsync(manager, ManagerReachLoader.All);

        var scope = await Reaches.ResolveAsync(Staff(manager));

        Assert.Equal(ManagerReach.All, scope.Reach);
        // Null is "no narrowing" — the same thing a WHERE clause reads for an
        // admin, and a different thing to a policy route.
        Assert.Null(scope.ManagedBy);
        Assert.True(scope.Reaches(customer));
        Assert.False(scope.IsAdmin);
    });

    [SqlServerFact]
    public Task SelectedReachesTheAccountsGranted() => WithPeople(async (manager, customer) =>
    {
        await GrantAsync(manager, ManagerReachLoader.Selected);
        await GrantClientAsync(manager, customer);

        var scope = await Reaches.ResolveAsync(Staff(manager));

        Assert.Equal(ManagerReach.Selected, scope.Reach);
        Assert.Equal([customer], scope.ExtraClientIds);
        Assert.True(scope.Reaches(customer));
        // Still narrowed: Selected is their own accounts PLUS the list, and
        // the WHERE is what finds their own.
        Assert.Equal(manager, scope.ManagedBy);
        // Somebody not on the list is the database's question, not this one.
        Assert.Null(scope.Reaches("cli-somebody-else"));
    });

    /// <remarks>
    /// The list is only consulted under Selected. Turning somebody down to Own
    /// leaves their grants in the table — which is the point of two tables —
    /// and this is what stops those rows still being read.
    /// </remarks>
    [SqlServerFact]
    public Task GrantsAreNotReadUnderTheOtherTwoReaches() => WithPeople(async (manager, customer) =>
    {
        await GrantClientAsync(manager, customer);

        await GrantAsync(manager, ManagerReachLoader.Own);
        Assert.Empty((await Reaches.ResolveAsync(Staff(manager))).ExtraClientIds);

        await Db.Database.ExecuteSqlAsync($"""
            UPDATE "ManagerAccess" SET "reach" = {ManagerReachLoader.All} WHERE "managerId" = {manager}
            """);
        Assert.Empty((await Reaches.ResolveAsync(Staff(manager))).ExtraClientIds);

        // And the rows are still there to come back to.
        Assert.Equal([customer], await Reaches.ExtraClientIdsFor(manager));
    });

    /// <remarks>
    /// An admin is not narrowed by any of this, and granting one a reach must
    /// not narrow them — a row saying "selected" on an admin would be a
    /// restriction wearing the name of a grant.
    /// </remarks>
    [SqlServerFact]
    public Task AnAdminIsNotNarrowedByARowAboutThem() => WithPeople(async (manager, _) =>
    {
        await GrantAsync(manager, ManagerReachLoader.Selected);

        var admin = Scope.From(new SessionPayload(manager, Roles.Admin, null, "Probe", Later));
        var scope = await Reaches.ResolveAsync(admin);

        Assert.Null(scope.ManagedBy);
        Assert.True(scope.IsAdmin);
    });

    // ------------------------------------------------------- the constraints

    /// <remarks>
    /// Two rows would be two answers to how far somebody's reach goes, and
    /// nothing could choose between them.
    /// </remarks>
    [SqlServerFact]
    public Task OneReachPerPerson() => WithPeople(async (manager, _) =>
    {
        await GrantAsync(manager, ManagerReachLoader.All);

        var refused = await Assert.ThrowsAnyAsync<Exception>(
            () => GrantAsync(manager, ManagerReachLoader.Selected));

        Assert.Equal(DatabaseRefusal.Unique, DatabaseRefusals.Of(refused));
    });

    /// <remarks>
    /// The CHECK, which is what lets the resolver fall back to Own for an
    /// unrecognised word without that fallback ever being the thing keeping
    /// the system safe.
    /// </remarks>
    [SqlServerFact]
    public Task AReachNothingRecognisesIsRefusedByTheDatabase() => WithPeople(async (manager, _) =>
    {
        var refused = await Assert.ThrowsAnyAsync<Exception>(() => GrantAsync(manager, "everything"));

        Assert.Equal(DatabaseRefusal.Check, DatabaseRefusals.Of(refused));
    });

    [SqlServerFact]
    public Task TheSameAccountCannotBeGrantedTwice() => WithPeople(async (manager, customer) =>
    {
        await GrantClientAsync(manager, customer);

        var refused = await Assert.ThrowsAnyAsync<Exception>(() => GrantClientAsync(manager, customer));

        Assert.Equal(DatabaseRefusal.Unique, DatabaseRefusals.Of(refused));
    });

    /// <remarks>
    /// Their own accounts are the ones naming them, which every reach includes
    /// already — a row here saying otherwise grants nothing and reads as
    /// though it did.
    /// </remarks>
    [SqlServerFact]
    public Task NobodyIsGrantedThemselves() => WithPeople(async (manager, _) =>
    {
        var refused = await Assert.ThrowsAnyAsync<Exception>(() => GrantClientAsync(manager, manager));

        Assert.Equal(DatabaseRefusal.Check, DatabaseRefusals.Of(refused));
    });

    [SqlServerFact]
    public Task AGrantCannotNameAnAccountThatDoesNotExist() => WithPeople(async (manager, _) =>
    {
        var refused = await Assert.ThrowsAnyAsync<Exception>(
            () => GrantClientAsync(manager, "cli-never-existed"));

        Assert.Equal(DatabaseRefusal.ForeignKey, DatabaseRefusals.Of(refused));
    });

    /// <summary>
    /// A salesperson leaving takes their reach and their grants with them.
    /// </summary>
    /// <remarks>
    /// The one cascade SQL Server allows per pair of tables, spent on the
    /// relation that matters. The other two refuse the delete instead — see
    /// the allowances in tools/schema-audit.mjs.
    /// </remarks>
    [SqlServerFact]
    public async Task DeletingASalespersonTakesTheirReachAndGrantsWithThem()
    {
        var manager = $"cli-mgr-{Guid.NewGuid():n}";
        var customer = $"cli-cus-{Guid.NewGuid():n}";

        await Db.Database.ExecuteSqlAsync($"""
            INSERT INTO "Client" ("id", "name", "email", "role", "discountPercent")
            VALUES ({manager}, 'Probe Manager', {manager + "@example.test"}, 'SALES', 0),
                   ({customer}, 'Probe Customer', {customer + "@example.test"}, 'RETAIL', 0)
            """);
        try
        {
            await GrantAsync(manager, ManagerReachLoader.Selected);
            await GrantClientAsync(manager, customer);

            await Db.Database.ExecuteSqlAsync($"""DELETE FROM "Client" WHERE "id" = {manager}""");

            Assert.Equal(ManagerReach.Own, await Reaches.ReachFor(manager));
            Assert.Empty(await Reaches.ExtraClientIdsFor(manager));
        }
        finally
        {
            await Db.Database.ExecuteSqlAsync($"""
                DELETE FROM "ExtraClient" WHERE "managerId" = {manager} OR "clientId" = {customer}
                """);
            await Db.Database.ExecuteSqlAsync($"""DELETE FROM "ManagerAccess" WHERE "managerId" = {manager}""");
            await Db.Database.ExecuteSqlAsync($"""
                DELETE FROM "Client" WHERE "id" IN ({manager}, {customer})
                """);
        }
    }

    // -------------------------------------------------------- the vocabulary

    /// <summary>
    /// The three words the column holds are the three the enum has.
    /// </summary>
    /// <remarks>
    /// Two vocabularies that happen to agree, in two schemas and one enum.
    /// Asserted rather than derived from <c>ToString().ToLower()</c>, because
    /// renaming a C# member must not silently rename a database value that a
    /// CHECK constraint and another API also hold.
    /// </remarks>
    [Fact]
    public void TheWordsAndTheEnumAgree()
    {
        Assert.Equal("own", ManagerReachLoader.Write(ManagerReach.Own));
        Assert.Equal("selected", ManagerReachLoader.Write(ManagerReach.Selected));
        Assert.Equal("all", ManagerReachLoader.Write(ManagerReach.All));

        Assert.Equal(ManagerReach.Own, ManagerReachLoader.Read("own"));
        Assert.Equal(ManagerReach.Selected, ManagerReachLoader.Read("selected"));
        Assert.Equal(ManagerReach.All, ManagerReachLoader.Read("all"));
    }

    /// <remarks>
    /// The CHECK constraint should keep these out, and the fallback is still
    /// the narrowest reading rather than the widest. An unrecognised
    /// permission that read as All would be the worst possible default.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ALL")]
    [InlineData("everything")]
    public void AWordNothingRecognisesReadsAsTheNarrowestReach(string? stored) =>
        Assert.Equal(ManagerReach.Own, ManagerReachLoader.Read(stored));
}
