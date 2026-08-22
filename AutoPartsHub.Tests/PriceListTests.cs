using System.Text.Json;
using AutoPartsHub.Api.Admin;

namespace AutoPartsHub.Tests;

/// <summary>
/// Reading a supplier's price list.
/// </summary>
/// <remarks>
/// The conversion is the part worth pinning down. <c>rate</c> means units of
/// that currency per one unit of the base, so turning a quoted price into the
/// base divides where the markup engine multiplies. Upside down it throws
/// nothing — it just misprices the catalogue by the square of the rate.
/// </remarks>
public class PriceListTests
{
    private static readonly Dictionary<string, string> Products = new()
    {
        ["0986424815"] = "p-bosch",
        ["W71275"] = "p-mann",
        ["17138616418"] = "p-bmw",
    };

    private static readonly Dictionary<string, ConversionRate> Rates = new()
    {
        ["EUR"] = new ConversionRate("EUR", 1),
        // 1 EUR buys 1.1 USD, and 50 EGP.
        ["USD"] = new ConversionRate("USD", 1.1),
        ["EGP"] = new ConversionRate("EGP", 50),
    };

    /// <summary>The rows as they arrive: parsed JSON, not typed objects.</summary>
    private static Validated<ReadResult> Read(string json) =>
        PriceLists.ReadPriceRows(JsonDocument.Parse(json).RootElement, Products, Rates);

    private static Validated<ReadResult> ReadAbsent() =>
        PriceLists.ReadPriceRows(null, Products, Rates);

    private static ReadResult Ok(Validated<ReadResult> r)
    {
        Assert.True(r.Ok, $"expected success, got: {r.Error}");
        return r.Value!;
    }

    private static string Err(Validated<ReadResult> r)
    {
        Assert.False(r.Ok, "expected a refusal, got success");
        return r.Error!;
    }

    /* --------------------------------------------------- the conversion --- */

    [Fact]
    public void DividesByTheRateBecauseRateIsUnitsPerBase()
    {
        // 110 USD at 1.1 USD to the euro is 100 euro. Multiplying would say
        // 121, which is the same mistake in the same direction for every row.
        Assert.Equal(100, PriceLists.ToBaseCurrency(110, 1.1), 6);
        Assert.Equal(100, PriceLists.ToBaseCurrency(5000, 50), 6);
    }

    [Fact]
    public void LeavesABaseCurrencyPriceAlone()
    {
        Assert.Equal(100, PriceLists.ToBaseCurrency(100, 1));
    }

    /* ---------------------------------------------- matching rows to parts --- */

    [Fact]
    public void IgnoresTheSeparatorsABrandPrintsItsNumbersWith()
    {
        var rows = Ok(Read("""[{"partNumber":"0 986 424 815","price":10}]""")).Rows;

        Assert.Single(rows);
        Assert.Equal("p-bosch", rows[0].ProductId);
    }

    [Fact]
    public void MatchesRegardlessOfCase()
    {
        Assert.Equal("p-mann",
            Ok(Read("""[{"partNumber":"w712/75","price":10}]""")).Rows[0].ProductId);
    }

    [Fact]
    public void ReportsANumberTheCatalogueDoesNotCarryRatherThanDroppingIt()
    {
        var result = Ok(Read("""
            [{"partNumber":"0986424815","price":10},{"partNumber":"NOT-A-PART","price":10}]
            """));

        Assert.Single(result.Rows);
        var rejected = Assert.Single(result.Rejected);
        Assert.Equal("NOT-A-PART", rejected.PartNumber);
        Assert.Equal("No part in the catalogue matches that number.", rejected.Reason);
    }

    [Fact]
    public void SkipsBlankLinesWithoutCallingThemFailures()
    {
        // A spreadsheet's trailing empty rows are not something to report.
        var result = Ok(Read("""
            [{"partNumber":"0986424815","price":10},{"partNumber":"   ","price":0}]
            """));

        Assert.Single(result.Rows);
        Assert.Empty(result.Rejected);
    }

    /* --------------------------------------- converting what the file quoted --- */

    [Fact]
    public void StoresTheBaseCurrencyFigureAndKeepsTheOriginalBesideIt()
    {
        var row = Ok(Read("""[{"partNumber":"0986424815","price":110,"currency":"USD"}]""")).Rows[0];

        Assert.Equal(100, row.Price);
        Assert.Equal(110, row.SourcePrice);
        Assert.Equal("USD", row.SourceCurrency);
    }

    [Fact]
    public void RecordsNoSourceWhenNothingWasConverted()
    {
        // A "source" repeating the stored number would imply a conversion that
        // never happened.
        var row = Ok(Read("""[{"partNumber":"0986424815","price":100,"currency":"EUR"}]""")).Rows[0];

        Assert.Equal(100, row.Price);
        Assert.Null(row.SourcePrice);
        Assert.Null(row.SourceCurrency);
    }

    [Fact]
    public void TreatsAMissingCurrencyColumnAsTheBaseCurrency()
    {
        var row = Ok(Read("""[{"partNumber":"0986424815","price":100}]""")).Rows[0];

        Assert.Equal(100, row.Price);
        Assert.Null(row.SourceCurrency);
    }

    [Fact]
    public void RoundsTheConvertedFigureToCents()
    {
        var row = Ok(Read("""[{"partNumber":"0986424815","price":100,"currency":"USD"}]""")).Rows[0];

        Assert.Equal(90.91, row.Price); // 100 / 1.1
    }

    [Fact]
    public void RejectsACurrencyNobodyHasSetUp()
    {
        var result = Ok(Read("""
            [{"partNumber":"0986424815","price":10,"currency":"GBP"},
             {"partNumber":"W71275","price":10}]
            """));

        Assert.Contains("No currency called GBP", result.Rejected[0].Reason);
    }

    [Fact]
    public void AcceptsALowerCaseCurrencyCode()
    {
        Assert.Equal(100,
            Ok(Read("""[{"partNumber":"0986424815","price":5000,"currency":"egp"}]""")).Rows[0].Price);
    }

    /* ------------------------------------------------------------ refusals --- */

    [Fact]
    public void RejectsAPriceThatIsNotANumberOfZeroOrMore()
    {
        var result = Ok(Read("""
            [{"partNumber":"0986424815","price":"free"},
             {"partNumber":"W71275","price":-1},
             {"partNumber":"17138616418","price":1}]
            """));

        Assert.Equal(2, result.Rejected.Count);
        Assert.Contains("not a number", result.Rejected[0].Reason);
    }

    [Fact]
    public void RejectsAPriceColumnThatIsMissingEntirely()
    {
        // Number(undefined) is NaN, not zero. A missing column must not price
        // a whole file at nothing.
        var result = Ok(Read("""
            [{"partNumber":"0986424815"},{"partNumber":"W71275","price":1}]
            """));

        Assert.Single(result.Rows);
        Assert.Contains("not a number", result.Rejected[0].Reason);
    }

    [Fact]
    public void AcceptsAPriceOfZeroWhichIsWhatAnUnpricedImportLandsOn()
    {
        Assert.Equal(0, Ok(Read("""[{"partNumber":"0986424815","price":0}]""")).Rows[0].Price);
    }

    [Fact]
    public void TakesTheLastPriceWhenAFileNamesTheSamePartTwiceAndSaysSo()
    {
        var result = Ok(Read("""
            [{"partNumber":"0986424815","price":10},{"partNumber":"0 986 424 815","price":20}]
            """));

        Assert.Single(result.Rows);
        Assert.Equal(20, result.Rows[0].Price);
        Assert.Contains("more than once", result.Rejected[0].Reason);
    }

    [Fact]
    public void RefusesAFileWhereNothingCouldBeUsed()
    {
        Assert.Contains("No part in the catalogue",
            Err(Read("""[{"partNumber":"NOPE","price":1}]""")));
    }

    [Fact]
    public void BlamesTheReasonThatActuallyExplainsTheFailure()
    {
        // Every row here matches a part. What killed the file is a currency
        // that is not set up, and saying "no part matched" would send the
        // admin to check part numbers that were never the problem.
        var message = Err(Read("""
            [{"partNumber":"0986424815","price":1,"currency":"GBP"},
             {"partNumber":"W71275","price":1,"currency":"GBP"}]
            """));

        Assert.Contains("No currency called GBP", message);
        Assert.DoesNotContain("No part in the catalogue", message);
    }

    [Fact]
    public void NamesTheCommonestReasonWhenAFileFailedSeveralWays()
    {
        var message = Err(Read("""
            [{"partNumber":"NOPE-1","price":1},
             {"partNumber":"NOPE-2","price":1},
             {"partNumber":"0986424815","price":"x"}]
            """));

        Assert.Contains("Most often", message);
        Assert.Contains("2 of 3", message);
    }

    [Fact]
    public void RefusesSomethingThatIsNotAListOfRows()
    {
        Assert.Contains("list of rows", Err(ReadAbsent()));
        Assert.Contains("list of rows", Err(Read("""{"partNumber":"x"}""")));
        Assert.Contains("list of rows", Err(Read("\"a string\"")));
        Assert.Contains("no rows", Err(Read("[]")));
    }

    [Theory]
    [InlineData("[5]")]
    [InlineData("[null]")]
    [InlineData("[true]")]
    [InlineData("[\"a row\"]")]
    public void RefusesARowThatIsNotAnObject(string json)
    {
        Assert.Contains("Every row must be an object.", Err(Read(json)));
    }

    [Fact]
    public void RefusesAFileOfNothingButBlankLines()
    {
        // Blank lines are skipped rather than rejected, so this ends with no
        // rows and no reasons — and still has to refuse rather than store an
        // empty list.
        var message = Err(Read("""[{"partNumber":"  "},{"partNumber":""}]"""));

        Assert.Contains("Not one row could be used", message);
        Assert.Contains("The file had no usable rows", message);
    }

    /* ------------------------------------------------------- the details --- */

    [Fact]
    public void RequiresAName()
    {
        var r = PriceLists.ReadListDetails(JsonDocument.Parse("{}").RootElement);

        Assert.False(r.Ok);
        Assert.Equal("Give the list a name.", r.Error);
    }

    [Fact]
    public void RefusesAnOverLongName()
    {
        var json = JsonSerializer.Serialize(new { name = new string('x', 121) });
        var r = PriceLists.ReadListDetails(JsonDocument.Parse(json).RootElement);

        Assert.False(r.Ok);
        Assert.Contains("under 120", r.Error);
    }

    [Fact]
    public void ReadsBlankDescriptionAndSourceAsNone()
    {
        var json = JsonSerializer.Serialize(new { name = " March list ", description = "  ", sourceName = "" });
        var r = PriceLists.ReadListDetails(JsonDocument.Parse(json).RootElement);

        Assert.True(r.Ok);
        Assert.Equal("March list", r.Value!.Name);
        Assert.Null(r.Value.Description);
        Assert.Null(r.Value.SourceName);
    }
}
