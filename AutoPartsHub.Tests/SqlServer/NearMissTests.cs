using AutoPartsHub.Api.Catalogue;
using AutoPartsHub.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AutoPartsHub.Tests.SqlServer;

/// <summary>
/// "Nothing matched — did you mean?", after pg_trgm (T-067).
/// </summary>
/// <remarks>
/// Two things have to hold, and they pull against each other. The search still
/// has to FIND the product somebody was reaching for, which is what most of
/// this file asserts by reading rows back. And it has to find it without
/// reading the catalogue, which is the whole reason the trigram version had to
/// go — that one ran a similarity score over every product on every search
/// that came up empty, which is exactly the search a customer repeats.
///
/// The second half is asserted from the execution plan rather than from the
/// SQL, because the SQL cannot tell you: LIKE 'ABC%' on an unindexed column
/// reads identically to the version that seeks. See <see cref="PlanCapture"/>.
/// </remarks>
[Collection(CatalogueCollection.Name)]
public class NearMissTests(NearMissRows rows) : IClassFixture<NearMissRows>
{
    private SearchQueries Search() => new(rows.Db, FullText());

    /// <remarks>
    /// A real scope factory, because that is what FullTextSearch asks for — it
    /// opens its own context to ask the server one question and lets it go
    /// again, rather than holding one for the life of the process.
    /// </remarks>
    private static FullTextSearch FullText()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => new AutoPartsContext(
            new DbContextOptionsBuilder<AutoPartsContext>()
                .UseSqlServer(SqlServer.ConnectionString).Options));

        return new FullTextSearch(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
    }

    // --------------------------------------------------------- what it finds

    /// <remarks>
    /// The shape the old scoring was best at and this one is still good at:
    /// somebody stopped typing a part number. Separators are ignored on both
    /// sides, which is the reason the stored column exists.
    /// </remarks>
    [SqlServerTheory]
    [InlineData("0 986 42")]
    [InlineData("098642")]
    [InlineData("0986424815")]
    [InlineData("0-986-424")]
    public async Task ATruncatedPartNumberReachesItsProduct(string typed)
    {
        var found = await Search().IdsByFuzzyMatchAsync(typed);

        Assert.Contains(Catalogue.BrakePad, found);
    }

    /// <remarks>
    /// The name lane with no full-text index — which is what LocalDB gives,
    /// and what a deployment without the component would give. It reaches a
    /// name from its start.
    /// </remarks>
    [SqlServerTheory]
    [InlineData("Brake pa")]
    [InlineData("brake")]
    [InlineData("BRAKE PAD SET")]
    public async Task ATruncatedNameReachesItsProduct(string typed)
    {
        var found = await Search().IdsByFuzzyMatchAsync(typed);

        Assert.Contains(Catalogue.BrakePad, found);
    }

    /// <remarks>
    /// A manufacturer is reached the same way in both lanes, so this holds
    /// whether or not the deployment has full text.
    /// </remarks>
    [SqlServerFact]
    public async Task AManufacturerNameReachesItsProducts()
    {
        var found = await Search().IdsByFuzzyMatchAsync("MANN");

        Assert.Contains(Catalogue.OilFilter, found);
        Assert.DoesNotContain(Catalogue.BrakePad, found);
    }

    /// <remarks>
    /// Two characters of a part number reach most of a catalogue, and the
    /// answer to "did you mean" being three thousand products is no answer.
    /// </remarks>
    [SqlServerTheory]
    [InlineData("")]
    [InlineData("br")]
    [InlineData("09")]
    public async Task TooLittleToGoOnFindsNothing(string typed) =>
        Assert.Empty(await Search().IdsByFuzzyMatchAsync(typed));

    /// <remarks>
    /// The limit is the contract, not an accident of how many rows happened to
    /// match: three thousand filler parts all begin "Filler part", and each
    /// lane is capped before the two are joined.
    /// </remarks>
    [SqlServerFact]
    public async Task ASearchThatMatchesEverythingStillReturnsAPageOfIt()
    {
        var found = await Search().IdsByFuzzyMatchAsync("Filler part");

        Assert.Equal(25, found.Count);
    }

    /// <summary>A product reachable twice spends one place, not two.</summary>
    /// <remarks>
    /// The brake pad is reachable by its own part number and by the
    /// cross-reference added here. The caller reads this list as a ranking and
    /// presents it, so a duplicate is a product listed twice under "did you
    /// mean".
    /// </remarks>
    [SqlServerFact]
    public async Task AProductReachableTwiceIsListedOnce()
    {
        await rows.Db.Database.ExecuteSqlAsync($"""
            INSERT INTO "Interchange" ("id", "sourceId", "targetPartNo",
                                       "targetManufacturer", "exactMatch", "isOEM")
            VALUES ('ich-probe', {Catalogue.BrakePad}, '0986424815', 'BOSCH', 1, 0)
            """);
        try
        {
            var found = await Search().IdsByFuzzyMatchAsync("0986424815");

            Assert.Equal([Catalogue.BrakePad], found);
        }
        finally
        {
            await rows.Db.Database.ExecuteSqlRawAsync(
                "DELETE FROM \"Interchange\" WHERE \"id\" = 'ich-probe'");
        }
    }

    /// <summary>A cross-reference reaches the product that carries it.</summary>
    [SqlServerFact]
    public async Task ACrossReferenceReachesItsSourceProduct()
    {
        await rows.Db.Database.ExecuteSqlAsync($"""
            INSERT INTO "Interchange" ("id", "sourceId", "targetPartNo",
                                       "targetManufacturer", "exactMatch", "isOEM")
            VALUES ('ich-probe-2', {Catalogue.OilFilter}, 'ZZ-99/10', 'MANN-FILTER', 1, 1)
            """);
        try
        {
            Assert.Contains(Catalogue.OilFilter, await Search().IdsByFuzzyMatchAsync("zz9910"));
        }
        finally
        {
            await rows.Db.Database.ExecuteSqlRawAsync(
                "DELETE FROM \"Interchange\" WHERE \"id\" = 'ich-probe-2'");
        }
    }

    // ---------------------------------------------------- how it gets there

    /// <summary>
    /// T-067's acceptance, asked of the optimizer: no wide scan.
    /// </summary>
    /// <remarks>
    /// Every statement the near-miss search sends, compiled by the server with
    /// the parameters it was sent with, against tables large enough that a
    /// scan is a choice rather than the only option. A single scan here is the
    /// pg_trgm behaviour returning under another name.
    ///
    /// Four statements, not two: each call asks both lanes. A part number is
    /// also a string somebody might have meant as a name, and the search does
    /// not decide which it was — it asks both and puts the stronger answer
    /// first.
    /// </remarks>
    [SqlServerFact]
    public async Task NoLaneReadsMoreOfTheCatalogueThanItWasAskedFor()
    {
        rows.Plans.Clear();
        await Search().IdsByFuzzyMatchAsync("0 986 42");
        await Search().IdsByFuzzyMatchAsync("Brake pa");

        var statements = rows.Plans.Statements.ToArray();
        Assert.Equal(4, statements.Length);

        foreach (var statement in statements)
        {
            var plan = await statement.PlanAsync();

            Assert.Empty(plan.Scans);
            Assert.Contains(plan.Operations, op => op.EndsWith(" Seek", StringComparison.Ordinal));
        }
    }

    /// <summary>And it seeks the indexes that were added for it.</summary>
    /// <remarks>
    /// Named rather than counted, because "some index was used" would still
    /// pass if a lane quietly fell back to the primary key and filtered.
    /// </remarks>
    [SqlServerFact]
    public async Task EachLaneSeeksTheIndexItWasGiven()
    {
        rows.Plans.Clear();
        await Search().IdsByFuzzyMatchAsync("0 986 42");

        // In the order they are asked: numbers, then names.
        var numbers = await rows.Plans.Statements[0].PlanAsync();
        var names = await rows.Plans.Statements[1].PlanAsync();

        Assert.Contains("Product.Product_partNumberNormalised_idx", numbers.Reads);
        Assert.Contains("Interchange.Interchange_targetPartNoNormalised_idx", numbers.Reads);
        Assert.Contains("Product.Product_name_idx", names.Reads);
        Assert.Contains("Manufacturer.Manufacturer_name_key", names.Reads);
    }

    /// <summary>
    /// And the test above can tell: the one query that still scans, scanning.
    /// </summary>
    /// <remarks>
    /// Without this, "no scans" could be passing because nothing it looks at
    /// is ever reported — a plan shape it failed to recognise, an attribute
    /// renamed between versions, a query that never ran. So the detector is
    /// pointed at a statement known to scan and has to say so.
    ///
    /// The statement is the contains-anywhere part-number lookup, which is a
    /// scan by contract: its pattern has a wildcard at both ends and no index
    /// answers that. It is not part of the near-miss search and is not covered
    /// by T-067 — it is here because it is the honest control.
    /// </remarks>
    [SqlServerFact]
    public async Task TheOneQueryThatStillScansIsSeenToScan()
    {
        rows.Plans.Clear();
        await Search().IdsMatchingNormalisedPartNumberAsync("0986424815");

        var plan = await rows.Plans.Statements.Single().PlanAsync();

        Assert.NotEmpty(plan.Scans);
    }

    /// <summary>
    /// No leading wildcard — the other half of the acceptance, read off the
    /// statement rather than the plan.
    /// </summary>
    /// <remarks>
    /// The plan test above is the one that matters, but it is indirect: a
    /// leading wildcard is refused by it only because it forces a scan. This
    /// says the thing itself, so that the reason a future edit fails is the
    /// reason it is wrong.
    ///
    /// Read from the PARAMETER, not from the SQL — the pattern never appears
    /// in the statement text, which is the point of it being a parameter.
    /// </remarks>
    [SqlServerFact]
    public async Task NoPatternStartsWithAWildcard()
    {
        rows.Plans.Clear();
        await Search().IdsByFuzzyMatchAsync("0 986 42");
        await Search().IdsByFuzzyMatchAsync("Brake pa");

        var patterns = rows.Plans.Statements
            .SelectMany(statement => statement.Parameters)
            .Select(parameter => parameter.Value as string)
            .Where(value => value is not null && value.Contains('%'))
            .ToArray();

        Assert.NotEmpty(patterns);
        Assert.All(patterns, pattern => Assert.False(pattern!.StartsWith('%')));
    }

    // -------------------------------------------------------- the two halves

    /// <summary>
    /// The stored column and the C# that normalises the needle agree.
    /// </summary>
    /// <remarks>
    /// They are two implementations of one rule, in two languages, and the
    /// prefix seek only works while they match. Disagreeing is silent: the
    /// query runs, seeks, and returns nothing.
    /// </remarks>
    [SqlServerFact]
    public async Task TheDatabaseNormalisesAPartNumberTheSameWayTheCodeDoes()
    {
        var stored = await rows.Db.Database.SqlQuery<Normalised>($"""
            SELECT "partNumber" AS "Raw", "partNumberNormalised" AS "Norm"
            FROM "Product"
            WHERE "id" IN ({Catalogue.BrakePad}, {Catalogue.OilFilter})
            """).ToListAsync();

        Assert.Equal(2, stored.Count);
        Assert.All(stored, row => Assert.Equal(PartNumbers.Normalise(row.Raw), row.Norm));
    }

    private record Normalised(string Raw, string Norm);

    /// <summary>
    /// Which name lane this instance is on, said out loud.
    /// </summary>
    /// <remarks>
    /// LocalDB cannot host Full-Text Search, so every assertion above about
    /// names is about the lane that does without it. This test is here so that
    /// the file states which lane it covered rather than leaving a reader to
    /// infer it — and so that it FAILS, loudly and with the other name tests
    /// still passing, on the first machine where the component is present.
    /// That is the machine on which the CONTAINSTABLE statement gets its first
    /// execution, and the point at which this test should be replaced by
    /// assertions about it.
    /// </remarks>
    [SqlServerFact]
    public async Task FullTextIsNotAvailableHereAndTheSearchKnowsIt() =>
        Assert.False(await FullText().IsIndexedAsync());
}
