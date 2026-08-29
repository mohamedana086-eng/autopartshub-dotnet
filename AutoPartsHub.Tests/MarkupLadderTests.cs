using System.Text.Json;
using AutoPartsHub.Api.Pricing;

namespace AutoPartsHub.Tests;

/// <summary>
/// A ladder of standard margins by purchase price.
/// </summary>
/// <remarks>
/// The engine has been able to express this since the price band arrived —
/// each band is an ordinary rule. What could not be expressed is the SHAPE of
/// the whole ladder, and every case here is about a shape that looks fine one
/// rule at a time and is wrong taken together.
///
/// The refusals are asserted for their content rather than merely for failing,
/// because the sentence IS the feature: "nothing covers €50 to €80" is what
/// makes the mistake fixable, where "invalid ladder" would leave somebody
/// hunting for which band was wrong.
/// </remarks>
public class MarkupLadderTests
{
    private static JsonElement Body(string json) => JsonDocument.Parse(json).RootElement;

    private const string Good = """
        {
          "label": "Standard margin",
          "type": "PERCENT",
          "rungs": [
            { "from": 0,   "to": 50,   "value": 40 },
            { "from": 50,  "to": 200,  "value": 30 },
            { "from": 200, "to": null, "value": 22 }
          ]
        }
        """;

    /* ------------------------------------------- a ladder that is one --- */

    [Fact]
    public void IsAcceptedAndKeepsEveryBand()
    {
        var read = MarkupLadders.Read(Body(Good));

        Assert.True(read.Ok);
        Assert.Equal(3, read.Value!.Rungs.Count);
        Assert.Equal([40, 30, 22], read.Value.Rungs.Select(r => r.Value));
    }

    [Fact]
    public void SortsTheBandsRatherThanInsistingTheyArriveInOrder()
    {
        // The order somebody typed rows in is not a mistake worth refusing. The
        // shape of the ladder is.
        var read = MarkupLadders.Read(Body("""
            {
              "label": "Standard margin",
              "rungs": [
                { "from": 200, "to": null, "value": 22 },
                { "from": 0,   "to": 50,   "value": 40 },
                { "from": 50,  "to": 200,  "value": 30 }
              ]
            }
            """));

        Assert.True(read.Ok);
        Assert.Equal([0, 50, 200], read.Value!.Rungs.Select(r => r.From));
    }

    [Fact]
    public void TakesASingleBandCoveringEverything()
    {
        var read = MarkupLadders.Read(Body("""
            { "label": "Flat", "rungs": [ { "from": 0, "to": null, "value": 35 } ] }
            """));

        Assert.True(read.Ok);
    }

    /* ------------------------------------------ the four shapes it refuses --- */

    [Fact]
    public void RefusesAGapAndSaysWhatFallsIntoIt()
    {
        // A part costing €60 would price from the tier default — a number
        // nobody chose for it, which looks like a working answer.
        var read = MarkupLadders.Read(Body("""
            {
              "label": "Standard margin",
              "rungs": [
                { "from": 0,  "to": 50,   "value": 40 },
                { "from": 80, "to": null, "value": 22 }
              ]
            }
            """));

        Assert.False(read.Ok);
        Assert.Contains("€50", read.Error);
        Assert.Contains("€80", read.Error);
        Assert.Contains("tier default", read.Error);
    }

    [Fact]
    public void RefusesAnOverlapAndSaysWhichTwoBands()
    {
        // Two rules match. Which one wins is decided by specificity and then
        // priority — neither of which the person writing a ladder is thinking
        // about.
        var read = MarkupLadders.Read(Body("""
            {
              "label": "Standard margin",
              "rungs": [
                { "from": 0,  "to": 100,  "value": 40 },
                { "from": 50, "to": null, "value": 22 }
              ]
            }
            """));

        Assert.False(read.Ok);
        Assert.Contains("overlap", read.Error);
    }

    [Fact]
    public void RefusesALadderThatDoesNotStartAtZero()
    {
        // The cheapest parts are the ones a margin matters most on.
        var read = MarkupLadders.Read(Body("""
            {
              "label": "Standard margin",
              "rungs": [
                { "from": 10,  "to": 200,  "value": 30 },
                { "from": 200, "to": null, "value": 22 }
              ]
            }
            """));

        Assert.False(read.Ok);
        Assert.Contains("start at 0", read.Error);
    }

    [Fact]
    public void RefusesAClosedTop()
    {
        // The most expensive part in the catalogue today is not the most
        // expensive one next quarter.
        var read = MarkupLadders.Read(Body("""
            {
              "label": "Standard margin",
              "rungs": [
                { "from": 0,  "to": 50,  "value": 40 },
                { "from": 50, "to": 200, "value": 30 }
              ]
            }
            """));

        Assert.False(read.Ok);
        Assert.Contains("open-ended", read.Error);
    }

    [Fact]
    public void RefusesAnOpenBandThatIsNotTheLastOne()
    {
        var read = MarkupLadders.Read(Body("""
            {
              "label": "Standard margin",
              "rungs": [
                { "from": 0,  "to": null, "value": 40 },
                { "from": 50, "to": null, "value": 30 }
              ]
            }
            """));

        Assert.False(read.Ok);
        Assert.Contains("Only the top band", read.Error);
    }

    /* --------------------------- the ordinary refusals a rule already has --- */

    [Fact]
    public void NeedsAName()
    {
        var read = MarkupLadders.Read(Body("""{ "rungs": [] }"""));

        Assert.False(read.Ok);
        Assert.Equal("Give the ladder a name.", read.Error);
    }

    [Fact]
    public void NeedsAtLeastOneBand()
    {
        var read = MarkupLadders.Read(Body("""{ "label": "X", "rungs": [] }"""));

        Assert.False(read.Ok);
        Assert.Equal("A ladder needs at least one band.", read.Error);
    }

    [Fact]
    public void StopsAtTwelveBands()
    {
        var rungs = string.Join(",", Enumerable.Range(0, MarkupLadders.MaxRungs + 1)
            .Select(i => $$"""{ "from": {{i * 10}}, "to": {{(i + 1) * 10}}, "value": 20 }"""));

        var read = MarkupLadders.Read(Body($$"""{ "label": "X", "rungs": [{{rungs}}] }"""));

        Assert.False(read.Ok);
        Assert.Contains("12", read.Error);
    }

    [Fact]
    public void KeepsTheFloorRuleTheSingleRuleFormHas()
    {
        // Word for word, because the two forms write the same column and a
        // person meeting the refusal in one place has already met it in the
        // other.
        var noFloor = MarkupLadders.Read(Body("""
            { "label": "X", "type": "PERCENT_MIN",
              "rungs": [ { "from": 0, "to": null, "value": 40 } ] }
            """));

        Assert.False(noFloor.Ok);
        Assert.Equal(
            "A percentage with a floor needs the floor. Set a minimum amount.", noFloor.Error);

        var strayFloor = MarkupLadders.Read(Body("""
            { "label": "X", "minAmount": 5,
              "rungs": [ { "from": 0, "to": null, "value": 40 } ] }
            """));

        Assert.False(strayFloor.Ok);
        Assert.Equal(
            "A minimum amount only applies to a percentage with a floor.", strayFloor.Error);
    }

    [Fact]
    public void AcceptsAFlooredPercentageNowThatTheRuleFormDoes()
    {
        // The single-rule parser refused PERCENT_MIN outright while the code
        // for its floor sat below, unreachable — so this port could not write
        // a rule the other port could. Fixed with the ladder, and pinned here.
        var read = MarkupLadders.Read(Body("""
            { "label": "X", "type": "PERCENT_MIN", "minAmount": 3,
              "rungs": [ { "from": 0, "to": null, "value": 40 } ] }
            """));

        Assert.True(read.Ok);
        Assert.Equal(3, read.Value!.MinAmount);
    }

    [Fact]
    public void RefusesABandThatEndsWhereItStarts()
    {
        var read = MarkupLadders.Read(Body("""
            { "label": "X", "rungs": [ { "from": 50, "to": 50, "value": 30 } ] }
            """));

        Assert.False(read.Ok);
        Assert.Contains("Band 1", read.Error);
    }

    [Fact]
    public void RefusesABandWithNoMarginOnIt()
    {
        var read = MarkupLadders.Read(Body("""
            { "label": "X", "rungs": [ { "from": 0, "to": null, "value": "" } ] }
            """));

        Assert.False(read.Ok);
        Assert.Contains("needs a margin", read.Error);
    }

    /* -------------------------------------------- what each band is called --- */

    [Fact]
    public void CarriesTheBandInTheName()
    {
        // These arrive as a dozen rules in a list sorted by neither price nor
        // creation, and "Standard margin" twelve times over is a list nobody
        // can read.
        Assert.Equal(
            "Standard margin · €0–€50",
            MarkupLadders.RungLabel("Standard margin", new LadderRung(0, 50, 40)));

        Assert.Equal(
            "Standard margin · €200 and up",
            MarkupLadders.RungLabel("Standard margin", new LadderRung(200, null, 22)));
    }

    [Fact]
    public void WritesTheCentsOnlyWhereThereAreCents()
    {
        Assert.Equal("X · €0–€49.50", MarkupLadders.RungLabel("X", new LadderRung(0, 49.5, 40)));
    }
}
