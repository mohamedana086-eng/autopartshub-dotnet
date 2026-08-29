using AutoPartsHub.Api.Pricing;

namespace AutoPartsHub.Tests;

/// <summary>
/// What a part cost to buy, now that it can be bought from several suppliers.
/// </summary>
/// <remarks>
/// The chain gained a rung in the middle. Everything the markup engine does
/// multiplies the number these two functions return, so the order of the rungs
/// is not a detail — it decides which of three possible costs the whole
/// catalogue is priced from. The other API asserts the same answers.
/// </remarks>
public class PurchaseCostTests
{
    /// <summary>A priceable row with nothing on it but what these two read.</summary>
    private record Row(
        double BasePrice = 100,
        double? ListPrice = null,
        double? OfferPrice = null,
        string? SupplierId = null,
        string? OfferSupplierId = null) : IPriceable
    {
        public string PartNumber => "BP-1";
        public string ManufacturerName => "BOSCH";
        public string SystemSlug => "brakes";
        public string? GoodsCategoryId => null;
        public string Name => "Brake pad set";
        public string PartType => "aftermarket";
        public double? ListRowMarkupPercent => null;
    }

    /* ------------------------------ which of the three costs applies --- */

    [Fact]
    public void TakesTheActivePriceListAboveEverything()
    {
        // Uploading a list is the act of saying "these are the prices now". A
        // list that could be silently outranked by an offer nobody looked at
        // would make that act meaningless.
        Assert.Equal(70, RequestPricing.PurchasePrice(
            new Row(BasePrice: 100, ListPrice: 70, OfferPrice: 80)));
    }

    [Fact]
    public void TakesTheBestOfferWhenNoListCoversThePart()
    {
        Assert.Equal(80, RequestPricing.PurchasePrice(
            new Row(BasePrice: 100, ListPrice: null, OfferPrice: 80)));
    }

    [Fact]
    public void FallsBackToThePartsOwnPriceWhenThereIsNeither()
    {
        // The offers migration was lossless, so a part in this state prices
        // exactly as it did the day before it ran.
        Assert.Equal(100, RequestPricing.PurchasePrice(new Row()));
    }

    [Fact]
    public void TreatsAFreeOfferAsAPriceNotAsNothing()
    {
        // Zero is a figure a supplier can quote. `??` rather than a truthiness
        // test is what makes it one — the wrong operator here would silently
        // reprice every free part at its basePrice and nothing would look
        // broken.
        Assert.Equal(0, RequestPricing.PurchasePrice(new Row(BasePrice: 100, OfferPrice: 0)));
        Assert.Equal(0, RequestPricing.PurchasePrice(new Row(ListPrice: 0, OfferPrice: 80)));
    }

    /* --------------------------- which supplier the markup rules see --- */

    [Fact]
    public void IsTheOneWhoseOfferWon()
    {
        Assert.Equal("s-winner", RequestPricing.SupplierIdFor(
            new Row(SupplierId: "s-original", OfferSupplierId: "s-winner")));
    }

    [Fact]
    public void FallsBackToTheColumnThePartWasFirstSourcedFrom()
    {
        // The only answer available when nobody has offered it since.
        Assert.Equal("s-original", RequestPricing.SupplierIdFor(new Row(SupplierId: "s-original")));
    }

    [Fact]
    public void IsTheEmptyStringWhenThereIsNoSupplierAtAll()
    {
        // Not null: the dimension compares strings, and a rule naming a
        // supplier must not match a part that has none.
        Assert.Equal("", RequestPricing.SupplierIdFor(new Row()));
    }

    [Fact]
    public void NeverLetsTheOriginalOutrankTheWinner()
    {
        // The failure this prevents is a supplier markup rule firing for a
        // supplier we are no longer buying that part from — a wrong price with
        // a correct-looking rule behind it.
        var row = new Row(SupplierId: "s-original", OfferSupplierId: "s-winner");

        Assert.NotEqual(row.SupplierId, RequestPricing.SupplierIdFor(row));
    }
}
