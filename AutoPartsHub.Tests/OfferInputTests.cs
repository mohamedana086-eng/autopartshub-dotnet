using System.Text.Json;
using AutoPartsHub.Api.Admin;

namespace AutoPartsHub.Tests;

/// <summary>
/// What a part's offers are allowed to say.
/// </summary>
/// <remarks>
/// A purchase price is the number the whole markup engine multiplies up, so
/// every refusal here is a wrong price that never reaches a customer. The rest
/// is about the two absences that mean different things — a supplier left off
/// the list, and one whose offer is switched off. Both APIs write to the same
/// table, so both have to answer the same way.
/// </remarks>
public class OfferInputTests
{
    private static Validated<List<OfferInput>> Read(string json) =>
        OfferInputs.Read(JsonDocument.Parse(json).RootElement);

    private static List<OfferInput> Ok(Validated<List<OfferInput>> r)
    {
        Assert.True(r.Ok, $"expected success, got: {r.Error}");
        return r.Value!;
    }

    private static string Err(Validated<List<OfferInput>> r)
    {
        Assert.False(r.Ok, "expected a refusal, got success");
        return r.Error!;
    }

    /* --------------------------------------- the shape of the request --- */

    [Fact]
    public void TakesAnEmptyListWhichIsAPartNobodyOffers()
    {
        Assert.Empty(Ok(Read("""{"offers":[]}""")));
    }

    [Fact]
    public void RefusesSomethingThatIsNotAList()
    {
        Assert.Contains("list of offers", Err(Read("{}")));
        Assert.Contains("list of offers", Err(Read("""{"offers":"none"}""")));
    }

    [Fact]
    public void RefusesASupplierListedTwice()
    {
        // One offer per supplier per part. Two would be two answers to "what
        // do they charge", and nothing could choose between them.
        Assert.Contains("listed twice", Err(Read("""
            {"offers":[{"supplierId":"s-1","purchasePrice":10},
                       {"supplierId":"s-1","purchasePrice":11}]}
            """)));
    }

    [Fact]
    public void NeedsASupplierOnEveryOffer()
    {
        Assert.Contains("needs a supplier", Err(Read("""{"offers":[{"purchasePrice":10}]}""")));
    }

    /* -------------------------------------------------------- the price --- */

    [Fact]
    public void TakesZeroWhichASupplierCanQuote()
    {
        Assert.Equal(0, Ok(Read("""{"offers":[{"supplierId":"s-1","purchasePrice":0}]}"""))[0].PurchasePrice);
    }

    [Fact]
    public void RefusesANegativeOneAndSomethingThatIsNotANumber()
    {
        Assert.Contains("zero or more",
            Err(Read("""{"offers":[{"supplierId":"s-1","purchasePrice":-1}]}""")));
        Assert.Contains("zero or more",
            Err(Read("""{"offers":[{"supplierId":"s-1","purchasePrice":"on request"}]}""")));
    }

    [Fact]
    public void RefusesAFigureNobodyWouldTypeOnPurpose()
    {
        // Not a rule about what parts cost — a rule about what a keyboard
        // produces. Ten million is a decimal point in the wrong place, or a
        // part number pasted into a price box.
        var json = JsonSerializer.Serialize(new
        {
            offers = new[] { new { supplierId = "s-1", purchasePrice = OfferInputs.MaxPurchasePrice + 1 } },
        });

        Assert.Contains("a mistake, not a price", Err(Read(json)));
    }

    [Fact]
    public void RoundsToCentsOnTheWayIn()
    {
        // A price carried to four decimal places would be multiplied by a
        // markup and rounded once at the end, which puts the rounding
        // somewhere nobody chose.
        Assert.Equal(10.12,
            Ok(Read("""{"offers":[{"supplierId":"s-1","purchasePrice":10.12345}]}"""))[0].PurchasePrice);
    }

    /* ---------------------------------------------------- the lead time --- */

    [Fact]
    public void TheLeadTimeIsOptionalAndAbsentMeansTheSuppliers()
    {
        Assert.Null(Ok(Read("""{"offers":[{"supplierId":"s-1","purchasePrice":10}]}"""))[0].StockDays);
        Assert.Null(Ok(Read("""
            {"offers":[{"supplierId":"s-1","purchasePrice":10,"stockDays":null}]}
            """))[0].StockDays);
    }

    [Fact]
    public void TakesAWholeNumberOfDaysIncludingNought()
    {
        Assert.Equal(0, Ok(Read("""
            {"offers":[{"supplierId":"s-1","purchasePrice":10,"stockDays":0}]}
            """))[0].StockDays);
        Assert.Equal(14, Ok(Read("""
            {"offers":[{"supplierId":"s-1","purchasePrice":10,"stockDays":14}]}
            """))[0].StockDays);
    }

    [Fact]
    public void RefusesHalfADayANegativeOneAndAYearAndAHalf()
    {
        foreach (var days in new[] { "1.5", "-1", "400" })
        {
            Assert.Contains("whole number of days", Err(Read(
                $$"""{"offers":[{"supplierId":"s-1","purchasePrice":10,"stockDays":{{days}}}]}""")));
        }
    }

    /* ---------------------------------------- their own part number --- */

    [Fact]
    public void TheirPartNumberIsOptionalAndTrimmed()
    {
        Assert.Equal("BP-77", Ok(Read("""
            {"offers":[{"supplierId":"s-1","purchasePrice":10,"supplierPartNumber":"  BP-77 "}]}
            """))[0].SupplierPartNumber);
        Assert.Null(Ok(Read("""
            {"offers":[{"supplierId":"s-1","purchasePrice":10,"supplierPartNumber":"   "}]}
            """))[0].SupplierPartNumber);
    }

    [Fact]
    public void RefusesSomethingTooLongToBeAPartNumber()
    {
        var json = JsonSerializer.Serialize(new
        {
            offers = new[]
            {
                new
                {
                    supplierId = "s-1",
                    purchasePrice = 10.0,
                    supplierPartNumber = new string('x', OfferInputs.MaxSupplierPartNumber + 1),
                },
            },
        });

        Assert.Contains("under 60", Err(Read(json)));
    }

    /* ------------------- withdrawing an offer, as against removing it --- */

    [Fact]
    public void IsActiveUnlessSomethingExplicitlySaysOtherwise()
    {
        // An offer submitted without the field is one somebody is adding, and
        // adding an offer nobody may buy from would be a strange default.
        Assert.True(Ok(Read("""{"offers":[{"supplierId":"s-1","purchasePrice":10}]}"""))[0].Active);
        Assert.True(Ok(Read("""
            {"offers":[{"supplierId":"s-1","purchasePrice":10,"active":"yes"}]}
            """))[0].Active);
    }

    [Fact]
    public void IsWithdrawnOnlyByAnExplicitFalse()
    {
        Assert.False(Ok(Read("""
            {"offers":[{"supplierId":"s-1","purchasePrice":10,"active":false}]}
            """))[0].Active);
    }

    [Fact]
    public void KeepsAWithdrawnOfferOnTheList()
    {
        // "We do not buy this from them" and "they do not sell it" both have
        // to be sayable, and only the second is an absence.
        var offers = Ok(Read("""
            {"offers":[{"supplierId":"s-1","purchasePrice":10},
                       {"supplierId":"s-2","purchasePrice":11,"active":false}]}
            """));

        Assert.Equal(2, offers.Count);
        Assert.Equal([true, false], offers.Select(o => o.Active).ToArray());
    }
}
