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

    /// <summary>
    /// Cross-references, normalised, to the parts they are exactly equivalent to.
    /// </summary>
    /// <remarks>
    /// A list rather than an id because a number can be equivalent to several
    /// of ours — <c>SHARED1</c> is — and that case exists to be refused rather
    /// than guessed at. <c>W71275</c> is deliberately both one of our own
    /// numbers and somebody's cross-reference to a different part, to pin down
    /// which wins.
    /// </remarks>
    private static readonly Dictionary<string, List<string>> Interchanges = new()
    {
        ["BOSCHOEM1"] = ["p-bosch"],
        ["SHARED1"] = ["p-mann", "p-bmw"],
        ["W71275"] = ["p-bmw"],
    };

    /// <summary>The rows as they arrive: parsed JSON, not typed objects.</summary>
    private static ReadOutcome Read(string json) =>
        PriceLists.ReadPriceRows(JsonDocument.Parse(json).RootElement, Products, Interchanges, Rates);

    private static ReadOutcome ReadAbsent() =>
        PriceLists.ReadPriceRows(null, Products, Interchanges, Rates);

    private static ReadResult Ok(ReadOutcome r)
    {
        Assert.True(r.Ok, $"expected success, got: {r.Error}");
        return r.Value!;
    }

    private static string Err(ReadOutcome r)
    {
        Assert.False(r.Ok, "expected a refusal, got success");
        return r.Error!;
    }

    private static ReadOutcome Refused(ReadOutcome r)
    {
        Assert.False(r.Ok, "expected a refusal, got success");
        return r;
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

    /* ---------------------------------------- what a rejection records --- */

    // A count of failures tells an admin their file is wrong. A line number,
    // the part number, and the price exactly as the file wrote it tell them
    // which cell to open. The import log stores these, so what is asserted
    // here is what can still be read back next week — and it has to match the
    // other API line for line, because both write into the same column.

    [Fact]
    public void NumbersTheLineItSatOnBlankRowsIncluded()
    {
        // The blank third row is skipped rather than rejected — and it still
        // counts, because the point of the number is to match the file.
        var result = Ok(Read("""
            [{"partNumber":"0986424815","price":10},
             {"partNumber":"W71275","price":10},
             {"partNumber":"  ","price":0},
             {"partNumber":"NOT-A-PART","price":10}]
            """));

        var rejected = Assert.Single(result.Rejected);
        Assert.Equal(4, rejected.Line);
    }

    [Fact]
    public void KeepsThePriceVerbatimWhichIsTheWholeEvidenceWhenItIsNotANumber()
    {
        var result = Ok(Read("""
            [{"partNumber":"0986424815","price":10},{"partNumber":"W71275","price":"on request"}]
            """));

        Assert.Equal("on request", result.Rejected[0].Price);
        Assert.Contains("not a number", result.Rejected[0].Reason);
    }

    [Fact]
    public void WritesANumericPriceTheWayJavaScriptWouldNotTheWayTheFileSpeltIt()
    {
        // `10.50` reaches the other API as the double 10.5, because JSON.parse
        // threw the trailing zero away before String() ever saw it. Both ports
        // store this text, so both have to spell it the same.
        var result = Ok(Read("""
            [{"partNumber":"0986424815","price":10},{"partNumber":"NOT-A-PART","price":10.50}]
            """));

        Assert.Equal("10.5", result.Rejected[0].Price);
    }

    [Fact]
    public void KeepsTheCurrencyTheFileNamedEvenWhenThatIsWhyItFailed()
    {
        var result = Ok(Read("""
            [{"partNumber":"0986424815","price":10},
             {"partNumber":"W71275","price":10,"currency":"gbp"}]
            """));

        // As written, not as upper-cased for the lookup: the cell said `gbp`.
        Assert.Equal("gbp", result.Rejected[0].Currency);
        Assert.Contains("No currency called GBP", result.Rejected[0].Reason);
    }

    [Fact]
    public void ReportsTheLineARepeatSupersededNotTheOneThatWon()
    {
        var result = Ok(Read("""
            [{"partNumber":"0986424815","price":10},
             {"partNumber":"W71275","price":15},
             {"partNumber":"0 986 424 815","price":20}]
            """));

        Assert.Equal(2, result.Rows.Count);

        // Line 3 is the price now in force, so line 1 is the one that needs
        // explaining — with the ten it quoted, not the twenty that replaced it.
        var rejected = Assert.Single(result.Rejected);
        Assert.Equal(1, rejected.Line);
        Assert.Equal("10", rejected.Price);
        Assert.Contains("line 3 was used instead", rejected.Reason);
    }

    [Fact]
    public void ReportsEachSupersessionSeparatelyWhenAPartAppearsThreeTimes()
    {
        var result = Ok(Read("""
            [{"partNumber":"0986424815","price":10},
             {"partNumber":"0986424815","price":20},
             {"partNumber":"0986424815","price":30}]
            """));

        Assert.Single(result.Rows);
        Assert.Equal(30, result.Rows[0].Price);
        Assert.Equal(new[] { 1, 2 }, result.Rejected.Select(r => r.Line).ToArray());
    }

    /* ------------------------------------ a refusal carries its rejections --- */

    // A file that failed completely is the one somebody most wants to read
    // line by line — and it produces no price list for those lines to hang
    // off. So the refusal carries them itself, or they are lost with the
    // response.

    [Fact]
    public void HandsBackEveryLineOfAWhollyFailedFile()
    {
        var result = Refused(Read("""
            [{"partNumber":"NOPE-1","price":1},{"partNumber":"NOPE-2","price":"x"}]
            """));

        Assert.Equal(2, result.Rejected.Count);
        Assert.Equal(new[] { 1, 2 }, result.Rejected.Select(r => r.Line).ToArray());
        Assert.Equal("x", result.Rejected[1].Price);
    }

    [Fact]
    public void HasNothingToCarryWhenTheFileNeverGotAsFarAsItsRows()
    {
        Assert.Empty(Refused(Read("[]")).Rejected);
        Assert.Empty(Refused(ReadAbsent()).Rejected);
    }

    /* -------------------------------- matching a supplier's own numbers --- */

    // Suppliers quote their own numbering, because it is the only numbering
    // they have. Falling through to the cross-reference is what stops a whole
    // file being rejected line by line for that — and the rules below are what
    // stop the fallback becoming a quiet way to misprice.

    [Fact]
    public void FindsThePartThroughAnExactCrossReference()
    {
        var result = Ok(Read("""[{"partNumber":"BOSCH-OEM-1","price":10}]"""));

        Assert.Empty(result.Rejected);
        Assert.Equal("p-bosch", result.Rows[0].ProductId);
    }

    [Fact]
    public void RecordsTheSuppliersNumberBecauseThatIsWhatADisputeNames()
    {
        var result = Ok(Read("""[{"partNumber":"BOSCH-OEM-1","price":10}]"""));

        Assert.Equal("BOSCH-OEM-1", result.Rows[0].SourcePartNumber);
    }

    [Fact]
    public void RecordsNothingWhenOurOwnNumberMatched()
    {
        // Reads the same way as a null SourcePrice: nothing was translated, so
        // there is nothing to record.
        var result = Ok(Read("""[{"partNumber":"0986424815","price":10}]"""));

        Assert.Null(result.Rows[0].SourcePartNumber);
    }

    [Fact]
    public void PrefersOurOwnNumberToACrossReferencePointingElsewhere()
    {
        // W71275 is our number for the Mann filter AND somebody's
        // cross-reference to the BMW part. Ours wins, or a supplier's
        // paperwork could rename our catalogue.
        var result = Ok(Read("""[{"partNumber":"W71275","price":10}]"""));

        Assert.Equal("p-mann", result.Rows[0].ProductId);
        Assert.Null(result.Rows[0].SourcePartNumber);
    }

    [Fact]
    public void RefusesANumberEquivalentToMoreThanOneOfOurParts()
    {
        var result = Ok(Read("""
            [{"partNumber":"0986424815","price":10},{"partNumber":"SHARED-1","price":10}]
            """));

        Assert.Single(result.Rows);
        Assert.Contains("interchangeable with 2 of our parts", result.Rejected[0].Reason);
    }

    /* ------------------------------ what an upload would change --- */

    // The disaster being guarded against is not one wrong price — it is a
    // whole column read from the wrong place, which is visible in the SHAPE of
    // a file long before anyone reads a line of it.

    private static PricedRow Row(string productId, double price) =>
        new(productId, productId, price, null, null, null);

    /// <summary>`n` parts, each currently costing 10, each becoming `to(i)`.</summary>
    private static (List<PricedRow> Rows, Dictionary<string, double> Cost) File(
        int n, Func<int, double> to)
    {
        var rows = new List<PricedRow>();
        var cost = new Dictionary<string, double>();
        for (var i = 0; i < n; i++)
        {
            rows.Add(Row($"p{i}", to(i)));
            cost[$"p{i}"] = 10;
        }
        return (rows, cost);
    }

    [Fact]
    public void CountsWhichWayPricesWent()
    {
        var (rows, cost) = File(3, i => new double[] { 12, 8, 10 }[i]);
        var m = PriceLists.ReadPriceMovement(rows, cost);

        Assert.Equal(3, m.Compared);
        Assert.Equal(1, m.Rose);
        Assert.Equal(1, m.Fell);
        Assert.Equal(1, m.Unchanged);
    }

    [Fact]
    public void DoesNotCountAPartItHasNoCurrentCostFor()
    {
        // A new part is not moving, it is arriving. Counting arrivals as moves
        // of infinity would make every first upload look like a disaster.
        var m = PriceLists.ReadPriceMovement([Row("p-new", 500)], []);

        Assert.Equal(0, m.Compared);
        Assert.Equal(0, m.Rose);
        Assert.False(PriceLists.MovementIsAlarming(m));
    }

    [Fact]
    public void NamesTheSteepestMoveInEachDirection()
    {
        var (rows, cost) = File(4, i => new double[] { 11, 30, 9, 1 }[i]);
        var m = PriceLists.ReadPriceMovement(rows, cost);

        Assert.Equal(new Move("p1", 10, 30), m.SteepestRise);
        Assert.Equal(new Move("p3", 10, 1), m.SteepestFall);
    }

    [Fact]
    public void LeavesAPartThatCostsNothingTodayOutOfTheComparison()
    {
        // Nothing is a multiple of zero. Counting it would make an unpriced
        // part becoming priced look like an infinite rise every single time.
        var m = PriceLists.ReadPriceMovement(
            [Row("p-free", 25)], new Dictionary<string, double> { ["p-free"] = 0 });

        Assert.Equal(0, m.Compared);
    }

    [Fact]
    public void TreatsAFallToZeroAsWildSinceNoMultipleDescribesIt()
    {
        var m = PriceLists.ReadPriceMovement(
            [Row("p1", 0)], new Dictionary<string, double> { ["p1"] = 10 });

        Assert.Equal(1, m.Fell);
        Assert.Equal(1, m.Wild);
    }

    [Fact]
    public void LetsAnOrdinaryPriceRiseThroughHoweverManyPartsItTouches()
    {
        // Every part up by half. This is what a real quarterly list looks
        // like, and stopping it would make the guard an obstacle rather than a
        // warning.
        var (rows, cost) = File(200, _ => 15);

        Assert.False(PriceLists.MovementIsAlarming(PriceLists.ReadPriceMovement(rows, cost)));
    }

    [Fact]
    public void LetsADoublingThrough()
    {
        var (rows, cost) = File(200, _ => 20);

        Assert.False(PriceLists.MovementIsAlarming(PriceLists.ReadPriceMovement(rows, cost)));
    }

    [Fact]
    public void StopsAFileWhereMostOfThePricesMovedByAnAbsurdMultiple()
    {
        // A list quoted in a currency fifty times the base with the currency
        // column left off — every price fifty times what it should be.
        var (rows, cost) = File(200, _ => 500);
        var m = PriceLists.ReadPriceMovement(rows, cost);

        Assert.Equal(200, m.Wild);
        Assert.True(PriceLists.MovementIsAlarming(m));
    }

    [Fact]
    public void LetsAFewWildMoversThroughWhenTheRestOfTheFileIsSane()
    {
        // One part in ten going up tenfold is a supplier being difficult, not
        // a column read wrong.
        var (rows, cost) = File(200, i => i % 10 == 0 ? 100 : 11);
        var m = PriceLists.ReadPriceMovement(rows, cost);

        Assert.Equal(20, m.Wild);
        Assert.False(PriceLists.MovementIsAlarming(m));
    }

    [Fact]
    public void JudgesNothingWhenThereIsTooLittleToJudge()
    {
        // Deliberate corrections come in small files. The signature being
        // caught is a whole column, and a whole column is never three rows.
        var (rows, cost) = File(PriceLists.EnoughToJudge - 1, _ => 5000);
        var m = PriceLists.ReadPriceMovement(rows, cost);

        Assert.Equal(PriceLists.EnoughToJudge - 1, m.Wild);
        Assert.False(PriceLists.MovementIsAlarming(m));
    }

    [Fact]
    public void StartsJudgingAtExactlyEnoughRows()
    {
        var (rows, cost) = File(PriceLists.EnoughToJudge, _ => 5000);

        Assert.True(PriceLists.MovementIsAlarming(PriceLists.ReadPriceMovement(rows, cost)));
    }

    [Fact]
    public void SaysWhatTrippedItWithTheNumbersThatTrippedIt()
    {
        var (rows, cost) = File(50, _ => 500);
        var message = PriceLists.MovementRefusal(PriceLists.ReadPriceMovement(rows, cost));

        // Spelt out in full rather than matched loosely: both APIs put this
        // exact sentence in front of an admin, and in the import log.
        Assert.Equal(
            "50 of the 50 parts this file already prices move by more than 5 times. "
          + "p0 goes from 10 to 500. That is the shape of a column read from the wrong place. "
          + "Check which column the prices came from, and which currency they are in — or send "
          + "it again with `confirmLargeChange` if the move is real.",
            message);
    }
}
