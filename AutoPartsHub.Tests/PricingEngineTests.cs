using AutoPartsHub.Api.Pricing;

namespace AutoPartsHub.Tests;

/// <summary>
/// The pricing engine decides what every customer pays, and it is pure — no
/// database, no session — so it is the one part of the system that can be
/// pinned down completely.
/// </summary>
/// <remarks>
/// What these lock down is the order of operations: markup, then discount,
/// then currency, each exactly once. The order is not cosmetic. Discounting
/// before markup, or converting before discounting, gives a different answer
/// for the same agreed percentages, and the mistake is invisible until a
/// customer adds up an invoice.
/// </remarks>
public class PricingEngineTests
{
    private static PricingContext Ctx(
        double basePrice = 100,
        string supplierId = "sup-1",
        string manufacturerName = "BOSCH",
        string vehicleSystemSlug = "brakes",
        string partNumber = "BP-1234",
        string clientCategoryId = "cat-retail",
        double markupPercent = 50,
        double? discountPercent = null,
        PricingCurrency? currency = null) =>
        new(basePrice, supplierId, manufacturerName, vehicleSystemSlug, partNumber,
            clientCategoryId, markupPercent, discountPercent, currency);

    /// <summary>
    /// A rule, named the way a rule used to be written.
    /// </summary>
    /// <remarks>
    /// The filters are now conditions, and each of these named arguments makes
    /// a list of one — which is what a single-value filter always was. Kept as
    /// named arguments rather than rewritten at every call site because the
    /// cases below are about what the engine DECIDES, and they should go on
    /// deciding the same thing through the new mechanism. Lists longer than
    /// one are exercised in <see cref="MarkupConditionTests"/>.
    ///
    /// Specificity comes from the same function a save uses, so a test cannot
    /// drift from what ships.
    /// </remarks>
    private static MarkupRule Rule(
        string id = "r1",
        string label = "Rule",
        int priority = 0,
        string? clientCategoryId = null,
        string? supplierId = null,
        string? manufacturerName = null,
        string? vehicleSystemSlug = null,
        string? goodsCategoryId = null,
        string? partNumberPrefix = null,
        double? purchasePriceFrom = null,
        double? purchasePriceTo = null,
        MarkupType type = MarkupType.Percent,
        double value = 10,
        bool active = true)
    {
        List<RuleCondition> conditions = [];
        if (clientCategoryId is not null) conditions.Add(new("clientCategory", clientCategoryId));
        if (supplierId is not null) conditions.Add(new("supplier", supplierId));
        if (manufacturerName is not null) conditions.Add(new("manufacturer", manufacturerName));
        if (vehicleSystemSlug is not null) conditions.Add(new("vehicleSystem", vehicleSystemSlug));
        if (goodsCategoryId is not null) conditions.Add(new("goodsCategory", goodsCategoryId));
        if (partNumberPrefix is not null) conditions.Add(new("partNumberPrefix", partNumberPrefix));

        return new(id, label, priority, conditions,
            MarkupDimensions.SpecificityOf(
                conditions, purchasePriceFrom is not null || purchasePriceTo is not null),
            purchasePriceFrom, purchasePriceTo,
            type, value, active);
    }

    private const string Fallback = "Client category default markup";

    private static PriceResult Resolve(PricingContext ctx, params MarkupRule[] rules) =>
        PricingEngine.Resolve(ctx, [.. rules]);

    /* --------------------------------------------------- falling back --- */

    [Fact]
    public void UsesTheClientCategoryDefaultWhenNoRuleMatches()
    {
        var result = Resolve(Ctx());

        Assert.Equal(150, result.FinalPrice);
        Assert.Equal(Fallback, result.AppliedRule);
    }

    [Fact]
    public void IgnoresARuleThatIsSwitchedOff()
    {
        Assert.Equal(150, Resolve(Ctx(), Rule(active: false, value: 500)).FinalPrice);
    }

    [Fact]
    public void MatchesARuleWithEveryFilterLeftEmpty()
    {
        // No filters means "any", not "nothing" — this is the catalogue-wide rule.
        var result = Resolve(Ctx(), Rule(label: "House markup", value: 20));

        Assert.Equal(120, result.FinalPrice);
        Assert.Equal("House markup", result.AppliedRule);
    }

    /* ---------------------------------------- choosing between rules --- */

    [Fact]
    public void PrefersTheRuleWithMoreFiltersSet()
    {
        var broad = Rule(id: "broad", label: "Broad", value: 10);
        var narrow = Rule(id: "narrow", label: "Narrow", value: 80,
            supplierId: "sup-1", manufacturerName: "BOSCH");

        // Order in the list must not matter; the sort decides.
        Assert.Equal("Narrow", Resolve(Ctx(), broad, narrow).AppliedRule);
        Assert.Equal("Narrow", Resolve(Ctx(), narrow, broad).AppliedRule);
    }

    [Fact]
    public void BreaksATieOnSpecificityWithPriorityHighestFirst()
    {
        var low = Rule(id: "low", label: "Low", supplierId: "sup-1", priority: 1, value: 10);
        var high = Rule(id: "high", label: "High", supplierId: "sup-1", priority: 9, value: 80);

        Assert.Equal("High", Resolve(Ctx(), low, high).AppliedRule);
        Assert.Equal("High", Resolve(Ctx(), high, low).AppliedRule);
    }

    [Fact]
    public void CountsAPriceBandAsOneFilterHoweverManyEndsItHas()
    {
        // from+to is still one concept, so it must not out-specify a rule with
        // a genuinely separate second filter.
        var band = Rule(id: "band", label: "Band", purchasePriceFrom: 1, purchasePriceTo: 500);
        var two = Rule(id: "two", label: "Two filters",
            supplierId: "sup-1", manufacturerName: "BOSCH");

        Assert.Equal("Two filters", Resolve(Ctx(), band, two).AppliedRule);
    }

    [Fact]
    public void LeavesATieOnBothKeysToTheOrderTheRulesArrivedIn()
    {
        // Equal specificity and equal priority. The engine does NOT invent a
        // third key — it sorts stably and lets the first one through, and the
        // caller is what makes that deterministic by reading the rules in id
        // order. Asserting the sort is stable is asserting that contract: if
        // it stopped being stable, the loader's ORDER BY would silently stop
        // deciding anything.
        var a = Rule(id: "aaa", label: "A", supplierId: "sup-1", value: 10);
        var b = Rule(id: "bbb", label: "B", supplierId: "sup-1", value: 80);

        Assert.Equal("A", Resolve(Ctx(), a, b).AppliedRule);
        Assert.Equal("B", Resolve(Ctx(), b, a).AppliedRule);
    }

    /* ------------------------------------------ the filters themselves --- */

    [Fact]
    public void MatchesAClientCategoryAndRejectsAnother()
    {
        var r = Rule(label: "Tier", clientCategoryId: "cat-trade", value: 80);

        Assert.Equal("Tier", Resolve(Ctx(clientCategoryId: "cat-trade"), r).AppliedRule);
        Assert.Equal(Fallback, Resolve(Ctx(clientCategoryId: "cat-retail"), r).AppliedRule);
    }

    [Fact]
    public void ComparesAManufacturerWithoutRegardToCase()
    {
        var r = Rule(label: "Brand", manufacturerName: "bosch", value: 80);

        Assert.Equal("Brand", Resolve(Ctx(manufacturerName: "BOSCH"), r).AppliedRule);
    }

    [Fact]
    public void ComparesAPartNumberPrefixWithoutRegardToCase()
    {
        var r = Rule(label: "Prefix", partNumberPrefix: "bp-", value: 80);

        Assert.Equal("Prefix", Resolve(Ctx(partNumber: "BP-1234"), r).AppliedRule);
        Assert.Equal(Fallback, Resolve(Ctx(partNumber: "XX-1234"), r).AppliedRule);
    }

    [Fact]
    public void MatchesASupplierAndAVehicleSystemExactly()
    {
        var r = Rule(label: "Both", supplierId: "sup-1", vehicleSystemSlug: "brakes", value: 80);

        Assert.Equal("Both", Resolve(Ctx(), r).AppliedRule);
        Assert.Equal(Fallback, Resolve(Ctx(vehicleSystemSlug: "cooling"), r).AppliedRule);
        Assert.Equal(Fallback, Resolve(Ctx(supplierId: "sup-2"), r).AppliedRule);
    }

    [Theory]
    [InlineData(100, "Band")]
    [InlineData(200, "Band")]
    [InlineData(99.99, Fallback)]
    [InlineData(200.01, Fallback)]
    public void TreatsBothEndsOfAPriceBandAsInclusive(double basePrice, string expected)
    {
        var r = Rule(label: "Band", purchasePriceFrom: 100, purchasePriceTo: 200, value: 80);

        Assert.Equal(expected, Resolve(Ctx(basePrice: basePrice), r).AppliedRule);
    }

    [Fact]
    public void AppliesAnOpenEndedBandFromOneSideOnly()
    {
        var floor = Rule(label: "Expensive", purchasePriceFrom: 150, value: 80);

        Assert.Equal("Expensive", Resolve(Ctx(basePrice: 200), floor).AppliedRule);
        Assert.Equal(Fallback, Resolve(Ctx(basePrice: 100), floor).AppliedRule);
    }

    /* --------------------------------------- the three kinds of markup --- */

    [Fact]
    public void AddsAPercentage()
    {
        Assert.Equal(125, Resolve(Ctx(), Rule(type: MarkupType.Percent, value: 25)).FinalPrice);
    }

    [Fact]
    public void AddsAFlatAmount()
    {
        Assert.Equal(125, Resolve(Ctx(), Rule(type: MarkupType.Amount, value: 25)).FinalPrice);
    }

    [Fact]
    public void SetsAFixedPriceIgnoringWhatThePartCost()
    {
        Assert.Equal(25, Resolve(Ctx(), Rule(type: MarkupType.Fixed, value: 25)).FinalPrice);
        Assert.Equal(25, Resolve(Ctx(basePrice: 999), Rule(type: MarkupType.Fixed, value: 25)).FinalPrice);
    }

    /* ------------------------------------------- the account discount --- */

    [Fact]
    public void ComesOffTheMarkedUpPriceNotThePurchasePrice()
    {
        // markup first: (100 + 100) * 0.9 = 180.
        // Discount first would be (100 * 0.9) + 100 = 190. The percentages are
        // chosen so the two answers differ — with PERCENT they would not.
        var result = Resolve(Ctx(discountPercent: 10),
            Rule(label: "Flat", type: MarkupType.Amount, value: 100));

        Assert.Equal(180, result.FinalPrice);
    }

    [Fact]
    public void ReducesTheMarkupRatherThanCancellingIt()
    {
        var result = Resolve(Ctx(discountPercent: 10));

        Assert.Equal(150, result.PriceBeforeDiscount);
        Assert.Equal(135, result.FinalPrice);
        Assert.Equal(10, result.DiscountPercent);
    }

    [Fact]
    public void SaysWhichRuleWonAndThatADiscountCameOffIt()
    {
        var result = Resolve(Ctx(discountPercent: 10), Rule(label: "Trade"));

        Assert.Equal("Trade · less 10% account discount", result.AppliedRule);
    }

    [Fact]
    public void WritesAFractionalDiscountTheWayJavaScriptWould()
    {
        // The sentence a customer reads. 7.5 must not become "7.50", and 10
        // must not become "10.0" — the other API builds this string with
        // template interpolation, which prints the shortest form.
        Assert.Equal("Trade · less 7.5% account discount",
            Resolve(Ctx(discountPercent: 7.5), Rule(label: "Trade")).AppliedRule);
        Assert.Equal("Trade · less 10% account discount",
            Resolve(Ctx(discountPercent: 10), Rule(label: "Trade")).AppliedRule);
    }

    [Fact]
    public void LeavesTheLabelAloneWhenThereIsNoDiscount()
    {
        Assert.Equal("Trade", Resolve(Ctx(), Rule(label: "Trade")).AppliedRule);
    }

    [Fact]
    public void RefusesToTurnANegativeDiscountIntoASurcharge()
    {
        var result = Resolve(Ctx(discountPercent: -25));

        Assert.Equal(0, result.DiscountPercent);
        Assert.Equal(150, result.FinalPrice);
    }

    [Fact]
    public void WillNotPayTheCustomerToTakeThePart()
    {
        var result = Resolve(Ctx(discountPercent: 250));

        Assert.Equal(100, result.DiscountPercent);
        Assert.Equal(0, result.FinalPrice);
    }

    /* ------------------------------------------------------- currency --- */

    private static readonly PricingCurrency Egp = new("EGP", "E£", 50);

    [Fact]
    public void ConvertsLastAndOnlyMultiplies()
    {
        var result = Resolve(Ctx(currency: Egp));

        Assert.Equal(7500, result.FinalPrice); // 150 * 50
        Assert.Equal("EGP", result.CurrencyCode);
        Assert.Equal("E£", result.CurrencySymbol);
    }

    [Fact]
    public void GivesTheSameDiscountedAnswerWhateverTheAccountIsQuotedIn()
    {
        // The point of converting last: 10% off is 10% off in every currency.
        var b = Resolve(Ctx(discountPercent: 10));
        var converted = Resolve(Ctx(discountPercent: 10, currency: Egp));

        Assert.Equal(135, b.NetBase);
        Assert.Equal(135, converted.NetBase);
        Assert.Equal(b.NetBase * 50, converted.FinalPrice);
    }

    [Fact]
    public void KeepsNetBaseInTheBaseCurrencyWhichIsWhatAnOrderStores()
    {
        var result = Resolve(Ctx(currency: Egp));

        Assert.Equal(150, result.NetBase);
        Assert.Equal(7500, result.FinalPrice);
    }

    [Fact]
    public void QuotesInEuroWhenTheAccountHasNoCurrencyOfItsOwn()
    {
        var result = Resolve(Ctx());

        Assert.Equal("EUR", result.CurrencyCode);
        Assert.Equal("€", result.CurrencySymbol);
    }

    [Fact]
    public void ConvertsThePreDiscountPriceTooSoAQuoteAddsUp()
    {
        var result = Resolve(Ctx(discountPercent: 10, currency: Egp));

        Assert.Equal(7500, result.PriceBeforeDiscount);
        Assert.Equal(6750, result.FinalPrice);
    }

    /* --------------------------------------------------------- margin --- */

    [Fact]
    public void ReportsTheMarginOverThePurchasePriceInTheBaseCurrency()
    {
        Assert.Equal(50, Resolve(Ctx()).MarginPercent);
    }

    [Fact]
    public void MarginDoesNotMoveWhenTheAccountIsQuotedInAnotherCurrency()
    {
        Assert.Equal(50, Resolve(Ctx(currency: Egp)).MarginPercent);
    }

    [Fact]
    public void GoesNegativeWhenAFixedPriceSellsBelowCost()
    {
        Assert.Equal(-20, Resolve(Ctx(), Rule(type: MarkupType.Fixed, value: 80)).MarginPercent);
    }

    [Fact]
    public void SaysZeroRatherThanDividingByAPurchasePriceOfZero()
    {
        // Newly imported TecDoc articles land with basePrice 0 until a supplier
        // price list gives them one, so this is a real row, not a hypothetical.
        var result = Resolve(Ctx(basePrice: 0));

        Assert.Equal(0, result.MarginPercent);
        Assert.False(double.IsNaN(result.MarginPercent));
    }

    /* ------------------------------------------------------- rounding --- */

    [Fact]
    public void RoundsMoneyToCents()
    {
        Assert.Equal(6.85, Resolve(Ctx(basePrice: 6.85, markupPercent: 0)).FinalPrice);
    }

    [Fact]
    public void RoundsARepeatingResultRatherThanPassingTheDriftOn()
    {
        Assert.Equal(13.33, Resolve(Ctx(basePrice: 10, markupPercent: 33.333)).FinalPrice);
    }

    [Fact]
    public void RoundsAHalfCentUpTheWayJavaScriptDoes()
    {
        // .NET rounds half to even by default and JavaScript rounds half up,
        // so every half-case would land a cent apart if this were left to the
        // language. 8.125 is exactly representable, which makes it a real
        // half rather than one that floating point has already decided.
        Assert.Equal(8.13, Resolve(Ctx(basePrice: 8.125, markupPercent: 0)).FinalPrice);
        Assert.Equal(2.68, Resolve(Ctx(basePrice: 2.675, markupPercent: 0)).FinalPrice);
    }
}
