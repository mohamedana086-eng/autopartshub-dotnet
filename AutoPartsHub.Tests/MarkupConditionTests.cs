using AutoPartsHub.Api.Pricing;
using AutoPartsHub.Domain.Pricing;

namespace AutoPartsHub.Tests;

/// <summary>
/// Rules that filter on lists. The mirror of test/markup-conditions.test.ts.
/// </summary>
/// <remarks>
/// "These three suppliers" is one condition with three acceptable answers, not
/// three conditions. Everything below is a consequence of that sentence, and
/// the consequence that matters most is the ranking: a rule naming three
/// suppliers must rank exactly where a rule naming one ranks, because both say
/// the same KIND of thing about the part.
/// </remarks>
public class MarkupConditionTests
{
    private static PricingContext Ctx(
        string supplierId = "sup-1",
        string manufacturerName = "BOSCH",
        string partName = "Brake pad set, front",
        string partType = "aftermarket",
        string? clientId = null,
        string? clientRole = null,
        string? city = null,
        string? priceListId = null,
        long? nowMs = null,
        double basePrice = 100) =>
        new(BasePrice: basePrice,
            SupplierId: supplierId,
            ManufacturerName: manufacturerName,
            VehicleSystemSlug: "brakes",
            PartNumber: "BP-1234",
            ClientCategoryId: "cat-retail",
            ClientCategoryMarkupPercent: 50,
            PartName: partName,
            PartType: partType,
            ClientId: clientId,
            ClientRole: clientRole,
            City: city,
            PriceListId: priceListId,
            NowMs: nowMs);

    /// <summary>One dimension, one or more acceptable values.</summary>
    private static List<RuleCondition> On(string dimension, params string[] values) =>
        [.. values.Select(v => new RuleCondition(dimension, v))];

    private static MarkupRule Rule(
        List<RuleCondition> conditions,
        string label = "Rule",
        string id = "r1",
        int priority = 0,
        double value = 10,
        MarkupType type = MarkupType.Percent,
        double? minAmount = null,
        long? startsAtMs = null,
        long? endsAtMs = null) =>
        new(id, label, priority, conditions,
            MarkupDimensions.SpecificityOf(conditions, false),
            null, null, type, value, true, minAmount, startsAtMs, endsAtMs);

    private const string Fallback = "Client category default markup";

    private static string Applied(PricingContext ctx, params MarkupRule[] rules) =>
        PricingEngine.Resolve(ctx, [.. rules]).AppliedRule;

    /* -------------------------- a list is one condition, several answers --- */

    [Fact]
    public void MatchesAnyValueInTheList()
    {
        var rule = Rule(On("supplier", "sup-1", "sup-2", "sup-3"), "Three suppliers", value: 80);

        foreach (var supplierId in new[] { "sup-1", "sup-2", "sup-3" })
        {
            Assert.Equal("Three suppliers", Applied(Ctx(supplierId: supplierId), rule));
        }
    }

    [Fact]
    public void AndNoneOutsideIt()
    {
        var rule = Rule(On("supplier", "sup-1", "sup-2"), value: 80);

        Assert.Equal(Fallback, Applied(Ctx(supplierId: "sup-9"), rule));
    }

    [Fact]
    public void RequiresEveryDimensionNotEveryValue()
    {
        // "One of these two suppliers, AND one of these two brands." Four
        // conditions, two things to satisfy.
        var rule = Rule(
            [.. On("supplier", "sup-1", "sup-2"), .. On("manufacturer", "BOSCH", "ATE")],
            "Two of each", value: 80);

        Assert.Equal("Two of each",
            Applied(Ctx(supplierId: "sup-2", manufacturerName: "ATE"), rule));
        // Right supplier, wrong brand — the second dimension is unsatisfied.
        Assert.Equal(Fallback,
            Applied(Ctx(supplierId: "sup-2", manufacturerName: "TRW"), rule));
    }

    /* ------------------------------- a list counts once, however long it is --- */

    [Fact]
    public void ScoresTheSameAsNamingASingleValue()
    {
        // The heart of it. Three suppliers is not three times as specific as
        // one; it is the same statement with a wider answer, and a wider
        // answer is if anything LESS specific.
        Assert.Equal(1, MarkupDimensions.SpecificityOf(On("supplier", "sup-1"), false));
        Assert.Equal(1,
            MarkupDimensions.SpecificityOf(On("supplier", "sup-1", "sup-2", "sup-3"), false));
    }

    [Fact]
    public void SoALongListDoesNotOutRankAShortOne()
    {
        var many = Rule(On("supplier", "sup-1", "sup-2", "sup-3", "sup-4"),
            "Many suppliers", id: "many", value: 10);
        var one = Rule(On("supplier", "sup-1"), "One supplier", id: "one", priority: 1, value: 80);

        // Equal specificity, so priority decides — which is the point: they
        // are comparable at all only because the list did not inflate itself.
        Assert.Equal("One supplier", Applied(Ctx(), many, one));
        Assert.Equal("One supplier", Applied(Ctx(), one, many));
    }

    [Fact]
    public void ButTwoDimensionsDoOutRankOneHoweverShort()
    {
        var wide = Rule(On("supplier", "a", "b", "c", "d", "e", "sup-1"),
            "Six suppliers", id: "wide", priority: 9, value: 10);
        var narrow = Rule([.. On("supplier", "sup-1"), .. On("manufacturer", "BOSCH")],
            "Supplier and brand", id: "narrow", value: 80);

        // Specificity beats priority, and two dimensions beat one no matter
        // how many values the one holds.
        Assert.Equal("Supplier and brand", Applied(Ctx(), wide, narrow));
    }

    [Fact]
    public void CountsARepeatedValueOnce()
    {
        // Which the unique index on the table also refuses to store twice.
        Assert.Equal(1, MarkupDimensions.SpecificityOf(On("supplier", "sup-1", "sup-1"), false));
    }

    [Fact]
    public void AddsOneForAPriceBandAndOnlyOne()
    {
        Assert.Equal(1, MarkupDimensions.SpecificityOf([], true));
        Assert.Equal(2, MarkupDimensions.SpecificityOf(On("supplier", "sup-1"), true));
    }

    [Fact]
    public void IsZeroForARuleThatNarrowsOnNothing()
    {
        // A legitimate rule — the house markup — and the least specific there is.
        Assert.Equal(0, MarkupDimensions.SpecificityOf([], false));
    }

    /* ------------------------------------ the ways a value can be compared --- */

    [Fact]
    public void MatchesAnIdExactly()
    {
        Assert.True(MarkupDimensions.ValueMatches(MatchKind.Exact, "sup-1", "sup-1"));
        Assert.False(MarkupDimensions.ValueMatches(MatchKind.Exact, "sup-1", "SUP-1"));
    }

    [Fact]
    public void MatchesANameWithoutRegardToCase()
    {
        Assert.True(MarkupDimensions.ValueMatches(MatchKind.Insensitive, "bosch", "BOSCH"));
        Assert.False(MarkupDimensions.ValueMatches(MatchKind.Insensitive, "bosch", "BOSCHX"));
    }

    [Fact]
    public void MatchesAPrefixWithoutRegardToCase()
    {
        Assert.True(MarkupDimensions.ValueMatches(MatchKind.Prefix, "bp-", "BP-1234"));
        Assert.False(MarkupDimensions.ValueMatches(MatchKind.Prefix, "bp-", "XBP-1234"));
    }

    [Fact]
    public void MatchesAFragmentAnywhereInTheSubject()
    {
        Assert.True(MarkupDimensions.ValueMatches(MatchKind.Contains, "brake", "Brake pad set, front"));
        Assert.True(MarkupDimensions.ValueMatches(MatchKind.Contains, "pad set", "Brake pad set, front"));
        Assert.False(MarkupDimensions.ValueMatches(MatchKind.Contains, "clutch", "Brake pad set, front"));
    }

    [Fact]
    public void NeverMatchesWhenTheRequestCannotAnswer()
    {
        // A rule saying "customers in Cairo" is not satisfied by somebody
        // whose city nobody knows. Every kind, so no dimension has its own
        // opinion about it.
        foreach (var kind in Enum.GetValues<MatchKind>())
        {
            Assert.False(MarkupDimensions.ValueMatches(kind, "anything", null));
        }
    }

    /* ------------------------------------- the new dimensions reach the engine --- */

    [Fact]
    public void NarrowsOnWhatAPartIsCalled()
    {
        var rule = Rule(On("nameContains", "brake"), "Brake parts", value: 80);

        Assert.Equal("Brake parts", Applied(Ctx(), rule));
        Assert.Equal(Fallback, Applied(Ctx(partName: "Oil filter"), rule));
    }

    [Fact]
    public void NarrowsOnWhatKindOfPartItIs()
    {
        var rule = Rule(On("partType", "oem"), "Genuine only", value: 80);

        Assert.Equal("Genuine only", Applied(Ctx(partType: "oem"), rule));
        Assert.Equal(Fallback, Applied(Ctx(), rule));
    }

    [Fact]
    public void NarrowsOnANamedCustomer()
    {
        var rule = Rule(On("client", "cli-7"), "Just them", value: 80);

        Assert.Equal("Just them", Applied(Ctx(clientId: "cli-7"), rule));
        Assert.Equal(Fallback, Applied(Ctx(clientId: "cli-8"), rule));
        // And nobody at all — an anonymous visitor is not a named customer.
        Assert.Equal(Fallback, Applied(Ctx(), rule));
    }

    [Fact]
    public void NarrowsOnACityWithoutRegardToCase()
    {
        var rule = Rule(On("city", "cairo"), "Cairo", value: 80);

        Assert.Equal("Cairo", Applied(Ctx(city: "Cairo"), rule));
        Assert.Equal(Fallback, Applied(Ctx(city: "Alexandria"), rule));
    }

    [Fact]
    public void NarrowsOnSeveralCitiesAtOnce()
    {
        var rule = Rule(On("city", "Cairo", "Alexandria", "Tanta"), "The delta", value: 80);

        foreach (var city in new[] { "Cairo", "ALEXANDRIA", "tanta" })
        {
            Assert.Equal("The delta", Applied(Ctx(city: city), rule));
        }
        Assert.Equal(Fallback, Applied(Ctx(city: "Aswan"), rule));
    }

    [Fact]
    public void NarrowsOnTheAccountType()
    {
        var rule = Rule(On("clientRole", "B2B"), "Trade", value: 80);

        Assert.Equal("Trade", Applied(Ctx(clientRole: "B2B"), rule));
        Assert.Equal(Fallback, Applied(Ctx(clientRole: "RETAIL"), rule));
    }

    [Fact]
    public void NarrowsOnWhichPurchasePriceListIsInForce()
    {
        var rule = Rule(On("priceList", "pl-a"), "While list A", value: 80);

        Assert.Equal("While list A", Applied(Ctx(priceListId: "pl-a"), rule));
        Assert.Equal(Fallback, Applied(Ctx(priceListId: "pl-b"), rule));
    }

    /* ------------------------------ a dimension this build does not know --- */

    [Fact]
    public void MakesTheRuleNotApplyRatherThanApplyWrongly()
    {
        // A rule written by a newer version of the software. Refusing to match
        // is the safe direction: the account default catches it and the
        // customer is quoted a price somebody meant, rather than one nobody
        // checked.
        var rule = Rule([new RuleCondition("deliveryTerms", "ex-works")],
            "From the future", value: 80);

        Assert.Equal(Fallback, Applied(Ctx(), rule));
    }

    /* ------------------------------------------------ the vocabulary itself --- */

    /* ------------------------ an exclusion turns a condition inside out --- */

    private static List<RuleCondition> NotBrand(params string[] brands) =>
        [.. brands.Select(b => new RuleCondition("manufacturer", b, true))];

    [Fact]
    public void MatchesEverythingExceptWhatItNames()
    {
        // The case that asked for it: a tier priced on every brand but one.
        // Writing that as a list would mean naming every other brand in the
        // catalogue, and editing them all the day a new brand arrives.
        var rule = Rule(NotBrand("BMW"), "All but BMW", value: 80);

        Assert.Equal("All but BMW", Applied(Ctx(manufacturerName: "BOSCH"), rule));
        Assert.Equal(Fallback, Applied(Ctx(manufacturerName: "BMW"), rule));
    }

    [Fact]
    public void ExcludesEveryValueInTheListNotJustTheFirst()
    {
        var rule = Rule(NotBrand("BMW", "ATE"), "Neither", value: 80);

        Assert.Equal("Neither", Applied(Ctx(manufacturerName: "TRW"), rule));
        foreach (var manufacturerName in new[] { "BMW", "ATE" })
        {
            Assert.Equal(Fallback, Applied(Ctx(manufacturerName: manufacturerName), rule));
        }
    }

    [Fact]
    public void ExcludesTheWayItsDimensionCompares()
    {
        // Brand is case-insensitive, so excluding "bmw" excludes "BMW".
        var rule = Rule(NotBrand("bmw"), "All but BMW", value: 80);

        Assert.Equal(Fallback, Applied(Ctx(manufacturerName: "BMW"), rule));
    }

    [Fact]
    public void AnExclusionDoesNotApplyWhenTheRequestCannotAnswer()
    {
        // "Customers outside Cairo" is not satisfied by somebody whose city
        // nobody knows. We cannot say they are in Cairo, and we cannot say
        // they are not — so the rule falls through, as an inclusion would.
        var rule = Rule([new RuleCondition("city", "Cairo", true)], "Outside Cairo", value: 80);

        Assert.Equal("Outside Cairo", Applied(Ctx(city: "Alexandria"), rule));
        Assert.Equal(Fallback, Applied(Ctx(), rule));
    }

    [Fact]
    public void AnExclusionScoresTheSameAsAnInclusion()
    {
        // The direction of a statement is not its size. Both narrow on brand.
        Assert.Equal(1, MarkupDimensions.SpecificityOf(NotBrand("BMW", "ATE"), false));
        Assert.Equal(1, MarkupDimensions.SpecificityOf(On("manufacturer", "BMW"), false));
    }

    [Fact]
    public void CombinesAnExclusionWithAnInclusionOnAnotherDimension()
    {
        var rule = Rule(
            [.. On("supplier", "sup-1", "sup-2"), .. NotBrand("BMW")],
            "These suppliers, not BMW", value: 80);

        Assert.Equal("These suppliers, not BMW",
            Applied(Ctx(supplierId: "sup-2", manufacturerName: "TRW"), rule));
        Assert.Equal(Fallback,
            Applied(Ctx(supplierId: "sup-2", manufacturerName: "BMW"), rule));
        Assert.Equal(Fallback,
            Applied(Ctx(supplierId: "sup-9", manufacturerName: "TRW"), rule));
    }

    [Fact]
    public void IsAnsweredDefinitelyEvenIfADimensionPointsBothWays()
    {
        // The write path refuses to store this. The engine does not get to
        // assume the write path ran, so it reads them as two statements that
        // must both hold — which nothing can satisfy for an exact match —
        // rather than an answer that depends on which row came back first.
        var rule = Rule(
            [
                new RuleCondition("supplier", "sup-1"),
                new RuleCondition("supplier", "sup-1", true),
            ],
            "Contradiction", value: 80);

        Assert.Equal(Fallback, Applied(Ctx(supplierId: "sup-1"), rule));
        Assert.Equal(Fallback, Applied(Ctx(supplierId: "sup-2"), rule));
    }

    /* ----------------------------- a percentage with a floor under it --- */

    private static MarkupRule Floored(double value, double? minAmount) =>
        Rule([], "Floored", value: value, type: MarkupType.PercentMin, minAmount: minAmount);

    private static double Price(PricingContext ctx, params MarkupRule[] rules) =>
        PricingEngine.Resolve(ctx, [.. rules]).FinalPrice;

    [Fact]
    public void TakesThePercentageWhenThePercentageIsTheBiggerOfTheTwo()
    {
        // 100 at +20% is 120; the floor of €2 never comes into it.
        Assert.Equal(120, Price(Ctx(), Floored(20, 2)));
    }

    [Fact]
    public void TakesTheFloorWhenThePercentageComesToLess()
    {
        // The case that asked for it: 1% of a one-euro part is a cent, which
        // does not pay for picking it off a shelf.
        Assert.Equal(3, Price(Ctx(basePrice: 1), Floored(1, 2)));
    }

    [Fact]
    public void FloorsTheMarkupNotThePrice()
    {
        // A minimum profit: a part that cost more still sells for more. If the
        // floor were on the price, both of these would come out at 2.
        Assert.Equal(3, Price(Ctx(basePrice: 1), Floored(0, 2)));
        Assert.Equal(12, Price(Ctx(basePrice: 10), Floored(0, 2)));
    }

    [Fact]
    public void IsExactlyThePercentageWhereTheTwoMeet()
    {
        // 100 at +2% is €2 of markup, which is the floor exactly.
        Assert.Equal(102, Price(Ctx(), Floored(2, 2)));
    }

    [Fact]
    public void BehavesAsAPlainPercentageWithNoFloorSet()
    {
        // The database refuses to store the pair that way, so this is about
        // the engine being answerable rather than a state anybody can reach.
        Assert.Equal(120, Price(Ctx(), Floored(20, null)));
    }

    /* --------------------------- a rule only in force for a while --- */

    private static long Utc(int year, int month, int day) =>
        new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private static readonly long June = Utc(2026, 6, 1);
    private static readonly long July = Utc(2026, 7, 1);
    private static readonly long August = Utc(2026, 8, 1);

    [Fact]
    public void AppliesInsideItsWindow()
    {
        var rule = Rule([], "Summer", value: 80, startsAtMs: June, endsAtMs: August);
        Assert.Equal("Summer", Applied(Ctx(nowMs: July), rule));
    }

    [Fact]
    public void DoesNotApplyBeforeItStartsOrAfterItEnds()
    {
        var rule = Rule([], "Summer", value: 80, startsAtMs: July, endsAtMs: August);
        Assert.Equal(Fallback, Applied(Ctx(nowMs: June), rule));

        var past = Rule([], "Summer", value: 80, startsAtMs: June, endsAtMs: July);
        Assert.Equal(Fallback, Applied(Ctx(nowMs: August), past));
    }

    [Fact]
    public void IncludesBothOfItsBounds()
    {
        var rule = Rule([], "Summer", value: 80, startsAtMs: June, endsAtMs: August);
        Assert.Equal("Summer", Applied(Ctx(nowMs: June), rule));
        Assert.Equal("Summer", Applied(Ctx(nowMs: August), rule));
    }

    [Fact]
    public void ReadsAnOpenEndAsUntilFurtherNoticeNotAsExpired()
    {
        var rule = Rule([], "Summer", value: 80, startsAtMs: June);
        Assert.Equal("Summer", Applied(Ctx(nowMs: August), rule));
    }

    [Fact]
    public void ReadsAnOpenStartTheSameWayBackwards()
    {
        var rule = Rule([], "Summer", value: 80, endsAtMs: August);
        Assert.Equal("Summer", Applied(Ctx(nowMs: June), rule));
    }

    [Fact]
    public void LetsALessSpecificRuleWinWhileTheSpecificOneIsOutOfSeason()
    {
        // The behaviour that makes a window worth having: the price falls back
        // to the standing rule rather than to nothing at all.
        var summer = Rule(
            [.. On("supplier", "sup-1"), .. On("manufacturer", "BOSCH")],
            "Summer", id: "summer", value: 80, startsAtMs: July, endsAtMs: August);
        var standing = Rule(On("supplier", "sup-1"), "Standing", id: "standing", value: 10);

        Assert.Equal("Summer", Applied(Ctx(nowMs: July), summer, standing));
        Assert.Equal("Standing", Applied(Ctx(nowMs: June), summer, standing));
    }

    [Fact]
    public void AWindowDoesNotMakeARuleMoreSpecific()
    {
        // A window says whether the rule exists today, not what it applies to.
        var windowed = Rule(On("supplier", "sup-1"), "Windowed", id: "windowed",
            priority: 0, value: 10, startsAtMs: June, endsAtMs: August);
        var plain = Rule(On("supplier", "sup-1"), "Plain", id: "plain", priority: 1, value: 80);

        // Equal specificity, so priority decides — which it could not do if
        // the window had quietly added a point.
        Assert.Equal("Plain", Applied(Ctx(nowMs: July), windowed, plain));
    }

    /* ------------------------------------------------ the vocabulary --- */

    [Fact]
    public void HasNoDuplicateNames()
    {
        var names = MarkupDimensions.All.Select(d => d.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void GivesEveryDimensionALabelAndAHint()
    {
        // A dimension nobody understands is a dimension nobody uses.
        foreach (var dimension in MarkupDimensions.All)
        {
            Assert.NotEmpty(dimension.Label);
            Assert.NotEmpty(dimension.Hint);
        }
    }

    [Fact]
    public void SaysTheSameThingAsTheOtherPort()
    {
        // The two ports price the same shop from the same table. A dimension
        // one of them has never heard of is a rule that applies on one API and
        // not on the other — which is the failure the comparison harness
        // exists to catch, caught here instead.
        Assert.Equal(14, MarkupDimensions.All.Count);
    }
}
