using AutoPartsHub.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsHub.Tests.SqlServer;

/// <summary>
/// Passing a list of values as one parameter, against a real engine.
/// </summary>
/// <remarks>
/// PostgreSQL bound a C# array straight to a <c>text[]</c>. SQL Server has no
/// array parameter, so the list travels as JSON and comes back through
/// <c>OPENJSON</c> — see <see cref="SqlList"/> for why JSON and not a
/// delimiter, a splice, or a parameter per value.
///
/// The dialect check cannot cover any of this. It parses statements and binds
/// their names; it never runs one, so it would pass a rewrite that returns the
/// wrong rows just as happily as one that returns the right ones. Everything
/// below is about what comes back.
/// </remarks>
[Collection(CatalogueCollection.Name)]
public class SqlListTests(Catalogue catalogue)
{
    private AutoPartsContext _db => catalogue.Db;

    /// <summary>The values a list parameter actually delivers.</summary>
    private Task<List<string>> ReadBack(IEnumerable<string>? values) =>
        _db.Database
            .SqlQuery<string>($"""
                SELECT value COLLATE DATABASE_DEFAULT AS "Value"
                FROM OPENJSON({SqlList.Of(values)})
                """)
            .ToListAsync();

    [SqlServerFact]
    public async Task EveryValueArrivesInOrder()
    {
        Assert.Equal(["a", "b", "c"], await ReadBack(["a", "b", "c"]));
    }

    /// <remarks>
    /// The reason it is JSON. Part numbers are typed by customers and contain
    /// whatever they contain: a comma-delimited string would split
    /// <c>0 986, 424</c> into two part numbers nobody searched for, and a
    /// semicolon or a pipe only moves which input breaks it.
    /// </remarks>
    [SqlServerFact]
    public async Task ValuesContainingDelimitersSurvive()
    {
        string[] awkward = ["a,b", "c;d", "e|f", "g'h", "i\"j", "k\\l", "[m]", "{n}"];

        Assert.Equal(awkward, await ReadBack(awkward));
    }

    [SqlServerFact]
    public async Task AnEmptyListMatchesNothingRatherThanEverything()
    {
        Assert.Empty(await ReadBack([]));
    }

    /// <remarks>
    /// Null in, null out — and a null parameter is what the
    /// <c>({list} IS NULL OR …)</c> guards in the search read to switch a
    /// filter off. The JSON literal "null" that a plain serialiser produces is
    /// a four-character string and would switch them on.
    /// </remarks>
    [SqlServerFact]
    public async Task ANullListIsNullAndNotTheWordNull()
    {
        Assert.Null(SqlList.Of((IEnumerable<string>?)null));

        var switchedOff = await _db.Database
            .SqlQuery<int>($"""
                SELECT CASE WHEN {SqlList.Of((IEnumerable<string>?)null)} IS NULL THEN 1 ELSE 0 END AS "Value"
                """)
            .ToListAsync();

        Assert.Equal(1, switchedOff.Single());
    }

    /// <summary>
    /// The collation, which is the half that would have gone wrong silently.
    /// </summary>
    /// <remarks>
    /// OPENJSON reading a parameter returns its <c>value</c> column in
    /// Latin1_General_BIN2. Comparing that to a column in the database's own
    /// collation is an error, which is loud and was fixed the moment it
    /// appeared. What is not loud is that Latin1_General_BIN2 is
    /// case-sensitive: collating the other side instead would have compiled,
    /// run, and quietly made every list lookup in the application
    /// case-sensitive while every other string comparison stayed
    /// case-insensitive.
    /// </remarks>
    [SqlServerFact]
    public async Task AListLookupMatchesTheWayEveryOtherComparisonDoes()
    {
        var matched = await _db.Database
            .SqlQuery<string>($"""
                SELECT v."Value" AS "Value"
                FROM (VALUES ('ABC123'), ('def456')) AS v("Value")
                WHERE v."Value" IN (
                  SELECT value COLLATE DATABASE_DEFAULT
                  FROM OPENJSON({SqlList.Of(["abc123", "DEF456"])})
                )
                """)
            .ToListAsync();

        // Both, because the database collation is case-insensitive and these
        // lookups now follow it. Under OPENJSON's own collation this would be
        // empty.
        Assert.Equal(2, matched.Count);
    }

    /// <remarks>
    /// The negated form is NOT EXISTS rather than NOT IN, and this is why:
    /// `x NOT IN (…)` is unknown — and so not true — the moment the list holds
    /// a null, which would silently match nothing. Both sites that use it are
    /// deletions, where matching nothing means deleting nothing.
    /// </remarks>
    [SqlServerFact]
    public async Task TheNegatedFormIsNotDefeatedByANullInTheList()
    {
        var kept = await _db.Database
            .SqlQuery<string>($"""
                SELECT v."Value" AS "Value"
                FROM (VALUES ('keep'), ('drop')) AS v("Value")
                WHERE NOT EXISTS (
                  SELECT 1 FROM OPENJSON({"[\"drop\", null]"})
                  WHERE value COLLATE DATABASE_DEFAULT = v."Value"
                )
                """)
            .ToListAsync();

        Assert.Equal(["keep"], kept);
    }

    /// <remarks>
    /// Two thousand is the bulk lookup's row limit, and the reason a parameter
    /// per value was not an option: the statement's shape would depend on how
    /// many there were, and the plan cache would fill with a plan per length.
    /// </remarks>
    [SqlServerFact]
    public async Task ATwoThousandItemListIsStillOneParameter()
    {
        var many = Enumerable.Range(0, 2000).Select(i => $"PART{i:D5}").ToArray();

        Assert.Equal(2000, (await ReadBack(many)).Count);
    }
}
