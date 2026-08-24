using AutoPartsHub.Api.Pricing;

namespace AutoPartsHub.Tests;

/// <summary>
/// Pricing by goods category.
/// </summary>
/// <remarks>
/// The engine picks a markup down three rungs — a matching rule, then the
/// part's goods category, then the account's category default. What these pin
/// down is the ORDER, because every one of them produces a plausible price and
/// only one of them is right.
///
/// Mirrors test/goods-category.test.ts in the other repository, case for case.
/// </remarks>
public class GoodsCategoryTests
{
    private static PricingContext Base(
        string? goodsCategoryId = null,
        GoodsCategoryMarkup? goodsCategoryMarkup = null,
        double? discountPercent = null) =>
        new(
            BasePrice: 100,
            SupplierId: "sup-1",
            ManufacturerName: "BOSCH",
            VehicleSystemSlug: "brake-system",
            PartNumber: "ABC-123",
            ClientCategoryId: "retail",
            ClientCategoryMarkupPercent: 40,
            DiscountPercent: discountPercent,
            Currency: null,
            GoodsCategoryId: goodsCategoryId,
            GoodsCategoryMarkup: goodsCategoryMarkup);

    /// <summary>A rule narrowing on a category, a supplier, or neither.</summary>
    private static MarkupRule Rule(
        string label,
        double value,
        string id = "r1",
        string? goodsCategoryId = null,
        string? supplierId = null,
        int priority = 0,
        MarkupType type = MarkupType.Percent)
    {
        List<RuleCondition> conditions = [];
        if (goodsCategoryId is not null) conditions.Add(new("goodsCategory", goodsCategoryId));
        if (supplierId is not null) conditions.Add(new("supplier", supplierId));

        return new(id, label, priority, conditions,
            MarkupDimensions.SpecificityOf(conditions, false),
            null, null, type, value, true);
    }

    [Fact]
    public void IsIgnoredWhenThePartHasNoCategory()
    {
        // Nothing is back-filled, so most of the catalogue is in this state and
        // has to price exactly as it did before the table existed.
        var result = PricingEngine.Resolve(Base(), []);
        Assert.Equal(140, result.FinalPrice);
        Assert.Equal("Client category default markup", result.AppliedRule);
    }

    [Fact]
    public void BeatsTheAccountDefaultWhenThePartIsClassified()
    {
        var result = PricingEngine.Resolve(
            Base("slow", new GoodsCategoryMarkup("Slow-moving", MarkupType.Percent, 60)), []);
        Assert.Equal(160, result.FinalPrice);
        Assert.Equal("Slow-moving category markup", result.AppliedRule);
    }

    [Theory]
    [InlineData(MarkupType.Amount, 25, 125)]
    [InlineData(MarkupType.Fixed, 89.99, 89.99)]
    public void CarriesTheFlatAndFixedKindsToo(MarkupType type, double value, double expected)
    {
        var result = PricingEngine.Resolve(
            Base(null, new GoodsCategoryMarkup("Heavy", type, value)), []);
        Assert.Equal(expected, result.FinalPrice);
    }

    [Fact]
    public void FallsBackWhenTheCategoryHoldsNoOpinion()
    {
        // A category can be purely organisational. Being in one must not change
        // the price on its own.
        var result = PricingEngine.Resolve(Base("shelf-b"), []);
        Assert.Equal(140, result.FinalPrice);
        Assert.Equal("Client category default markup", result.AppliedRule);
    }

    [Fact]
    public void ARuleBeatsTheCategoryEvenWhenItMatchesNothingInParticular()
    {
        // The category baseline is what a rule exists to override. A rule
        // losing to the baseline would make the override unwritable.
        var result = PricingEngine.Resolve(
            Base(null, new GoodsCategoryMarkup("Slow-moving", MarkupType.Percent, 60)),
            [Rule("Blanket 10%", 10)]);
        Assert.Equal(110, result.FinalPrice);
        Assert.Equal("Blanket 10%", result.AppliedRule);
    }

    [Fact]
    public void IncludingARuleScopedToThatVeryCategory()
    {
        var result = PricingEngine.Resolve(
            Base("consumables", new GoodsCategoryMarkup("Consumables", MarkupType.Percent, 35)),
            [Rule("Trade on consumables", 22, goodsCategoryId: "consumables")]);
        Assert.Equal(122, result.FinalPrice);
        Assert.Equal("Trade on consumables", result.AppliedRule);
    }

    [Fact]
    public void ACategoryScopedRuleSkipsAPartInAnotherCategory()
    {
        var result = PricingEngine.Resolve(
            Base("heavy"),
            [Rule("Consumables only", 5, goodsCategoryId: "consumables")]);
        Assert.Equal("Client category default markup", result.AppliedRule);
    }

    [Fact]
    public void AndSkipsAnUnclassifiedPartEntirely()
    {
        // The case that goes wrong if the check is written against the context
        // instead of the rule: a part with no category must fall through every
        // category-scoped rule, not match the first one.
        var result = PricingEngine.Resolve(Base(), [
            Rule("Consumables only", 5, goodsCategoryId: "consumables"),
            Rule("Heavy only", 7, id: "r2", goodsCategoryId: "heavy"),
        ]);
        Assert.Equal("Client category default markup", result.AppliedRule);
        Assert.Equal(140, result.FinalPrice);
    }

    [Fact]
    public void AnUnscopedRuleStillReachesAClassifiedPart()
    {
        var result = PricingEngine.Resolve(Base("heavy"), [Rule("Everything 10%", 10)]);
        Assert.Equal("Everything 10%", result.AppliedRule);
    }

    [Fact]
    public void ACategoryScopedRuleBeatsAnUnscopedOne()
    {
        var result = PricingEngine.Resolve(Base("consumables"), [
            Rule("Everything", 10, id: "wide"),
            Rule("Consumables", 20, id: "narrow", goodsCategoryId: "consumables"),
        ]);
        Assert.Equal("Consumables", result.AppliedRule);
    }

    [Fact]
    public void TwoFiltersBeatOneWhicheverTheyAre()
    {
        // Specificity counts filters rather than ranking them, which is what
        // the engine already did for the other six. The category joining that
        // count is the whole of its interaction with the ladder.
        var result = PricingEngine.Resolve(Base("consumables"), [
            Rule("Consumables", 20, id: "one", goodsCategoryId: "consumables"),
            Rule("Consumables from this supplier", 30, id: "two",
                goodsCategoryId: "consumables", supplierId: "sup-1"),
        ]);
        Assert.Equal("Consumables from this supplier", result.AppliedRule);
    }

    [Fact]
    public void PriorityStillBreaksATieBetweenEquallySpecificRules()
    {
        var result = PricingEngine.Resolve(Base("consumables"), [
            Rule("By category", 20, id: "a", goodsCategoryId: "consumables"),
            Rule("By supplier", 30, id: "b", supplierId: "sup-1", priority: 5),
        ]);
        Assert.Equal("By supplier", result.AppliedRule);
    }

    [Fact]
    public void TheDiscountStillComesAfterWhicheverRungWon()
    {
        var result = PricingEngine.Resolve(
            Base(null, new GoodsCategoryMarkup("Slow-moving", MarkupType.Percent, 60), 10), []);
        // 100 → 160 → less 10% → 144
        Assert.Equal(144, result.FinalPrice);
        Assert.Equal("Slow-moving category markup · less 10% account discount", result.AppliedRule);
    }
}
