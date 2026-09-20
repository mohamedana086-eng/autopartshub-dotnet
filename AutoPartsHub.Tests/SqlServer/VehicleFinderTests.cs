using AutoPartsHub.Api.Vehicles;

namespace AutoPartsHub.Tests.SqlServer;

/// <summary>
/// The vehicle finder, run rather than parsed.
/// </summary>
/// <remarks>
/// Its first query carried nine per-row filter flags, and eight of them were
/// rewritten into <c>CASE WHEN … THEN 1 ELSE 0 END</c> while the ninth — the
/// year — was left as the bare boolean PostgreSQL allows in a select list.
/// SQL Server does not allow it, so the statement did not compile and the
/// finder answered nothing at all. Every consumer of that column inside the
/// same statement compares it to 1, so there was never a version of this that
/// half worked.
///
/// It survived a dialect check that only parses because nothing parsed it: the
/// checker substitutes each interpolation with NULL, and <c>(NULL IS NULL OR
/// …) AS "fYear"</c> is the same syntax error as the real one — it was being
/// reported and read as one of the statements still to port. Nothing ran it,
/// which is what these tests are for.
/// </remarks>
[Collection(CatalogueCollection.Name)]
public class VehicleFinderTests(Catalogue catalogue)
{
    private VehicleFinder Finder => new(catalogue.Db);

    /// <summary>Every option, with nothing filtered.</summary>
    /// <remarks>
    /// The first assertion is simply that the statement runs. The rest say
    /// what it should have said: three variants of one model, counted per
    /// value of each field.
    /// </remarks>
    [SqlServerFact]
    public async Task AnUnfilteredSearchOffersEveryValueTheCatalogueHas()
    {
        var options = await Finder.OptionsAsync(new FinderFilters());

        Assert.Equal(3, Count(options, "make", "Renault"));
        Assert.Equal(3, Count(options, "model", "Megane"));
        Assert.Equal(1, Count(options, "bodyType", "hatchback"));
        Assert.Equal(1, Count(options, "bodyType", "estate"));
    }

    /// <summary>
    /// The rule the finder exists for: a list is computed without its own
    /// filter.
    /// </summary>
    /// <remarks>
    /// Picking a body type must not collapse the body-type list to the one
    /// picked, or a customer who chose wrong can no longer see that the other
    /// was ever there. Asserted from the other side too — the transmission
    /// list DOES narrow, because the body filter is not its own.
    /// </remarks>
    [SqlServerFact]
    public async Task ChoosingABodyTypeLeavesTheBodyTypeListWhole()
    {
        var options = await Finder.OptionsAsync(new FinderFilters(BodyType: "estate"));

        Assert.Equal(1, Count(options, "bodyType", "hatchback"));
        Assert.Equal(1, Count(options, "bodyType", "estate"));

        // ... while a list that is not its own filter narrows to what fits.
        Assert.Equal(0, Count(options, "transmission", "manual"));
        Assert.Equal(1, Count(options, "transmission", "automatic"));
    }

    /// <summary>
    /// The year filter — the one that was broken — narrowing.
    /// </summary>
    /// <remarks>
    /// 2016 is inside the estate's range and past the end of the hatchback's,
    /// so a year filter that silently matched everything (or nothing) reads
    /// differently from one that works.
    /// </remarks>
    [SqlServerFact]
    public async Task AYearNarrowsToTheVariantsBuiltThen()
    {
        var options = await Finder.OptionsAsync(new FinderFilters(Year: 2016));

        Assert.Equal(1, Count(options, "make", "Renault"));
        Assert.Equal(1, Count(options, "bodyType", "estate"));
        Assert.Equal(0, Count(options, "bodyType", "hatchback"));
    }

    /// <remarks>
    /// A variant nobody has classified must not disappear from a search that
    /// filters on a field it does not carry — hiding the customer's actual car
    /// is worse than showing one extra. It is the third variant here, and it
    /// has neither a body type nor a region.
    /// </remarks>
    [SqlServerFact]
    public async Task AnUnclassifiedVariantSurvivesAFilterOnWhatItLacks()
    {
        var page = await Finder.VehiclesAsync(new FinderFilters(BodyType: "estate"), 25);

        Assert.Contains(page.Vehicles, v => v.VariantId == "vv-megane-unknown");
        Assert.Contains(page.Vehicles, v => v.VariantId == "vv-megane-estate");
        Assert.DoesNotContain(page.Vehicles, v => v.VariantId == "vv-megane-hatch");
    }

    /// <remarks>
    /// The total comes off a window function on the same pass, and a page with
    /// no rows has nothing to read it from — which is not the same as zero.
    /// </remarks>
    [SqlServerFact]
    public async Task ThePageCarriesItsOwnTotal()
    {
        var all = await Finder.VehiclesAsync(new FinderFilters(), 2);

        Assert.Equal(2, all.Vehicles.Count);
        Assert.Equal(3, all.Total);

        var none = await Finder.VehiclesAsync(new FinderFilters(Make: "Nothing"), 25);

        Assert.Empty(none.Vehicles);
        Assert.Equal(0, none.Total);
    }

    private static int Count(List<FinderOption> options, string field, string value) =>
        options.FirstOrDefault(o => o.Field == field && o.Value == value)?.Vehicles ?? 0;
}
