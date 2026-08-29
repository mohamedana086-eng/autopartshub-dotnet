using System.Text.RegularExpressions;
using AutoPartsHub.Api.Pricing;

namespace AutoPartsHub.Tests;

/// <summary>
/// The margin stated where the part is bought.
/// </summary>
/// <remarks>
/// Two things are held here. The chain itself — line, then list, then supplier
/// — and where that chain sits in the engine's ladder, which is the half that
/// decides whether a rule somebody wrote last year still applies.
///
/// The same cases run against the other API, and the refusals are asserted in
/// full: three rungs of one chain refusing the same number in different words
/// would be three answers to one question.
/// </remarks>
public partial class PurchaseMarkupTests
{
    private static PurchaseMarkupSource Src(
        double? listPrice = 40,
        double? rowMarkupPercent = null,
        double? listMarkupPercent = null,
        string? listName = "March file",
        double? supplierMarkupPercent = null,
        string? supplierName = "Bosch") =>
        new(listPrice, rowMarkupPercent, listMarkupPercent, listName,
            supplierMarkupPercent, supplierName);

    /* ------------------------------------------------------- the chain --- */

    [Fact]
    public void TakesTheLineAboveTheListAndTheSupplier()
    {
        var markup = PurchaseMarkups.Of(Src(
            rowMarkupPercent: 35, listMarkupPercent: 22, supplierMarkupPercent: 18));

        Assert.Equal(new PurchaseMarkup("March file line markup", 35, MarkupRung.Line), markup);
    }

    [Fact]
    public void TakesTheListAboveTheSupplier()
    {
        var markup = PurchaseMarkups.Of(Src(listMarkupPercent: 22, supplierMarkupPercent: 18));

        Assert.Equal(MarkupRung.List, markup!.Rung);
        Assert.Equal(22, markup.Percent);
    }

    [Fact]
    public void FallsToTheSupplierWhenTheFileStatesNothing()
    {
        var markup = PurchaseMarkups.Of(Src(supplierMarkupPercent: 18));

        Assert.Equal(new PurchaseMarkup("Bosch markup", 18, MarkupRung.Supplier), markup);
    }

    [Fact]
    public void HasNoAnswerWhenNobodyHasStatedOne()
    {
        // Which is the state every part is in on the day this ships. Null here
        // is what makes the migration lossless: the engine falls through to
        // exactly the rungs it used before.
        Assert.Null(PurchaseMarkups.Of(Src()));
    }

    [Fact]
    public void TreatsAZeroAsAnInstructionNotAnAbsence()
    {
        // "Sell this line at cost" is a real thing to say. A ?? 0 anywhere in
        // the chain would read it as "say nothing" and quietly charge the
        // supplier's margin instead — the whole reason the columns are
        // nullable.
        var markup = PurchaseMarkups.Of(Src(rowMarkupPercent: 0, supplierMarkupPercent: 18));

        Assert.Equal(0, markup!.Percent);
        Assert.Equal(MarkupRung.Line, markup.Rung);
    }

    [Fact]
    public void IgnoresTheLineAndTheListWhenTheFileDoesNotCoverThePart()
    {
        // The cost then comes from an offer or from the part's own basePrice,
        // and a margin from a file that is not setting the cost would be half
        // of one deal and half of another.
        var markup = PurchaseMarkups.Of(Src(
            listPrice: null, rowMarkupPercent: 35, listMarkupPercent: 22,
            supplierMarkupPercent: 18));

        Assert.Equal(MarkupRung.Supplier, markup!.Rung);
    }

    [Fact]
    public void StillNamesTheListWhenItHasNoName()
    {
        var markup = PurchaseMarkups.Of(Src(listMarkupPercent: 22, listName: null));

        Assert.Equal("Price list list markup", markup!.Label);
    }

    /* ------------------------------------------------------ the ladder --- */

    private static PricingContext Ctx(
        PurchaseMarkup? purchaseMarkup = null,
        GoodsCategoryMarkup? goodsCategoryMarkup = null,
        double? discountPercent = null,
        PricingCurrency? currency = null) =>
        new(BasePrice: 100, SupplierId: "s1", ManufacturerName: "BOSCH",
            VehicleSystemSlug: "brakes", PartNumber: "BP-1",
            ClientCategoryId: "c1", ClientCategoryMarkupPercent: 30,
            DiscountPercent: discountPercent, Currency: currency,
            NowMs: 1787875200000,
            GoodsCategoryMarkup: goodsCategoryMarkup,
            PurchaseMarkup: purchaseMarkup);

    private static MarkupRule Rule() =>
        new("r1", "Trade accounts on brakes", 0, [], 1, null, null,
            MarkupType.Percent, 12, true);

    [Fact]
    public void LosesToAMatchingRule()
    {
        // Load-bearing rather than a preference. A rule is the only markup that
        // can see WHO is asking. If a supplier's margin outranked one, typing a
        // number into a supplier form would silently switch off every
        // customer-specific rule on their parts.
        var result = PricingEngine.Resolve(
            Ctx(purchaseMarkup: new PurchaseMarkup("Bosch markup", 50, MarkupRung.Supplier)),
            [Rule()]);

        Assert.Equal(112, result.FinalPrice);
        Assert.Equal("Trade accounts on brakes", result.AppliedRule);
    }

    [Fact]
    public void BeatsTheGoodsCategory()
    {
        var result = PricingEngine.Resolve(
            Ctx(
                purchaseMarkup: new PurchaseMarkup("March file line markup", 35, MarkupRung.Line),
                goodsCategoryMarkup: new GoodsCategoryMarkup("Consumables", MarkupType.Percent, 20)),
            []);

        Assert.Equal(135, result.FinalPrice);
        Assert.Equal("March file line markup", result.AppliedRule);
    }

    [Fact]
    public void BeatsTheClientCategoryDefault()
    {
        var result = PricingEngine.Resolve(
            Ctx(purchaseMarkup: new PurchaseMarkup("Bosch markup", 18, MarkupRung.Supplier)), []);

        Assert.Equal(118, result.FinalPrice);
    }

    [Fact]
    public void ChangesNothingWhereNobodyHasStatedOne()
    {
        // The whole catalogue is in this state today, so this is the assertion
        // that says the migration repriced nothing.
        var before = PricingEngine.Resolve(Ctx(), []);

        Assert.Equal(130, before.FinalPrice);
        Assert.Equal("Client category default markup", before.AppliedRule);
    }

    [Fact]
    public void StillTakesTheDiscountOffItAndConvertsLast()
    {
        var result = PricingEngine.Resolve(
            Ctx(
                purchaseMarkup: new PurchaseMarkup("Bosch markup", 50, MarkupRung.Supplier),
                discountPercent: 10,
                currency: new PricingCurrency("USD", "$", 2)),
            []);

        // 100 marked up to 150, less 10% is 135, doubled by the rate is 270.
        Assert.Equal(135, result.NetBase);
        Assert.Equal(270, result.FinalPrice);
        Assert.Equal("Bosch markup · less 10% account discount", result.AppliedRule);
    }

    /* ------------------------------------------------------- the input --- */

    [Fact]
    public void TakesAnEmptyBoxAsNoOpinion()
    {
        foreach (var empty in new object?[] { null, "" })
        {
            var read = PurchaseMarkups.Read(empty, "A markup");
            Assert.True(read.Ok);
            Assert.Null(read.Value);
        }
    }

    [Fact]
    public void KeepsADeliberateZero()
    {
        var read = PurchaseMarkups.Read(0d, "A markup");

        Assert.True(read.Ok);
        Assert.Equal(0, read.Value);
    }

    [Fact]
    public void RoundsToTheTwoPlacesTheMoneyUses()
    {
        Assert.Equal(18.33, PurchaseMarkups.Read(18.333333d, "A markup").Value);
    }

    [Fact]
    public void RefusesANegativeOne()
    {
        var read = PurchaseMarkups.Read(-5d, "A markup");

        Assert.False(read.Ok);
        Assert.Equal("A markup cannot be negative.", read.Error);
    }

    [Fact]
    public void RefusesAPriceTypedIntoThePercentBoxAndSaysSo()
    {
        var read = PurchaseMarkups.Read(1450d, "A markup");

        Assert.False(read.Ok);
        // Word for word against the other API. Naming the number back is what
        // makes the mistake obvious, where "out of range" would not.
        Assert.Equal(
            "A markup of 1450% is above the 1000% limit. Is that a price rather than a percentage?",
            read.Error);
        Assert.Equal(1000, PurchaseMarkups.MaxMarkupPercent);
    }

    [Fact]
    public void RefusesSomethingThatIsNotANumberAtAll()
    {
        Assert.False(PurchaseMarkups.Read("eighteen", "A markup").Ok);
    }

    /* ---------------------------------------------------- the SQL shape --- */

    [GeneratedRegex(@"AS ""ListPrice""")]
    private static partial Regex ListPriceColumn();

    [GeneratedRegex(@"AS ""ListRowMarkupPercent""")]
    private static partial Regex ListMarginColumn();

    private static string ApiSource(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AutoPartsHub.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);

        return File.ReadAllText(Path.Combine(dir!.FullName, "AutoPartsHub.Api", relative));
    }

    [Fact]
    public void SelectsTheLineMarginWhereverItSelectsTheLinePrice()
    {
        // Six queries build a priceable row. One that took the list's PRICE and
        // not its MARGIN would price the part off the file and mark it up off
        // the supplier — two halves of two different deals, and a number that
        // looks entirely ordinary. Nothing here runs SQL, so this is what
        // catches it.
        var missing = new List<string>();

        foreach (var file in new[]
                 {
                     "Catalogue/SearchQueries.cs",
                     "Endpoints/BulkLookupEndpoints.cs",
                     "Endpoints/CartEndpoints.cs",
                     "Endpoints/OrderEndpoints.cs",
                     "Endpoints/ProductEndpoints.cs",
                 })
        {
            var text = ApiSource(file);
            var prices = ListPriceColumn().Matches(text).Count;
            var margins = ListMarginColumn().Matches(text).Count;

            if (prices != margins) missing.Add($"{file}: {prices} prices, {margins} margins");
        }

        Assert.Empty(missing);
    }

    [GeneratedRegex(@"""([A-Za-z]+)""")]
    private static partial Regex QuotedName();

    [GeneratedRegex(@"""([A-Za-z]+)"" =")]
    private static partial Regex AssignedName();

    [Fact]
    public void WritesTheSameSupplierColumnsOnCreateAndOnUpdate()
    {
        // This found a real one: priority and minOrderAmount were on the INSERT
        // and absent from the UPDATE, so the editor could set them when a
        // supplier was created and never change them afterwards — saving the
        // form quietly put them back. Nothing in the types can see it, because
        // both statements are written from the same SupplierInput.
        var text = ApiSource("Endpoints/AdminSiteWriteEndpoints.cs");

        var insert = text[text.IndexOf("INSERT INTO \"Supplier\"", StringComparison.Ordinal)..];
        var inserted = QuotedName()
            .Matches(insert[..insert.IndexOf("VALUES", StringComparison.Ordinal)])
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
        inserted.Remove("Supplier");
        // Assigned by the insert itself, so it has no business in an update.
        inserted.Remove("id");

        // The SECOND UPDATE "Supplier" in the file: the first is the quick
        // rating-and-returns write, which sets two columns on purpose.
        var firstUpdate = text.IndexOf("UPDATE \"Supplier\"", StringComparison.Ordinal);
        var fullUpdate = text.IndexOf("UPDATE \"Supplier\"", firstUpdate + 1, StringComparison.Ordinal);
        var update = text[fullUpdate..];
        var updated = AssignedName()
            .Matches(update[..update.IndexOf("WHERE", StringComparison.Ordinal)])
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        var dropped = inserted.Where(c => !updated.Contains(c)).ToArray();

        Assert.Empty(dropped);
    }
}
