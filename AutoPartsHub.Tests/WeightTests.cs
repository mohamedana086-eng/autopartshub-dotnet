using AutoPartsHub.Api.Catalogue;

namespace AutoPartsHub.Tests;

/// <summary>
/// The weight of an order.
/// </summary>
/// <remarks>
/// Addition is not the interesting part. What these pin down is what happens
/// when a part has no weight on file — because the tempting answer, counting
/// it as zero, produces a number that looks like the weight of the order and
/// is quietly less than it.
///
/// Mirrors test/weight.test.ts in the other repository.
/// </remarks>
public class WeightTests
{
    [Fact]
    public void MultipliesEachLineByItsQuantity()
    {
        var total = Weight.Sum([new(1200, 2), new(350, 4)]);
        Assert.Equal(new WeightTotal(3800, true, 0), total);
    }

    [Fact]
    public void IsExactOverManyLinesWhichIsWhyGramsAreIntegers()
    {
        // The same sum in kilograms as doubles drifts, and an order of fifty
        // lines is fifty chances to be a gram out.
        var lines = Enumerable.Range(0, 50).Select(_ => new WeighedLine(100, 3));
        Assert.Equal(15000, Weight.Sum(lines).Grams);
    }

    [Fact]
    public void IsZeroAndCompleteForAnEmptyBasket()
    {
        // Nothing is missing from nothing.
        Assert.Equal(new WeightTotal(0, true, 0), Weight.Sum([]));
    }

    [Fact]
    public void CountsAGenuinelyWeightlessLineAsWeightless()
    {
        // Only null means unknown; a stored zero means zero.
        Assert.Equal(new WeightTotal(0, true, 0), Weight.Sum([new(0, 5)]));
    }

    [Fact]
    public void SaysSoRatherThanCountingAnUnweighedPartAsNothing()
    {
        var total = Weight.Sum([new(1000, 1), new(null, 3)]);
        Assert.Equal(1000, total.Grams);
        Assert.False(total.Complete);
        Assert.Equal(1, total.Unweighed);
    }

    [Fact]
    public void StillReturnsTheSubtotalBecauseAFloorBeatsNothing()
    {
        // The caller needs both halves: what is known, and that it is not all
        // of it. Returning null would throw away the part that was measured.
        var total = Weight.Sum([new(null, 1), new(2500, 2), new(null, 1)]);
        Assert.Equal(5000, total.Grams);
        Assert.Equal(2, total.Unweighed);
    }

    [Fact]
    public void CountsLinesNotUnitsAsTheGap()
    {
        // "Two parts have no weight on file" is what an operator can act on.
        Assert.Equal(1, Weight.Sum([new(null, 6)]).Unweighed);
    }

    [Theory]
    [InlineData(1.25, 1250)]
    [InlineData(0.35, 350)]
    [InlineData(0.0004, 0)]
    [InlineData(1.2345, 1235)]
    public void TakesKilogramsAndStoresGrams(double kg, int expected)
    {
        var (value, error) = Weight.ReadGrams(kg);
        Assert.Null(error);
        Assert.Equal(expected, value);
    }

    [Fact]
    public void RoundsHalvesTheSameWayJavaScriptDoes()
    {
        // Math.round in JavaScript rounds a half away from zero; .NET's
        // default rounds it to even, which would put 2.5 g at 2 on one side
        // and 3 on the other. One gram, on every part with a half in it.
        Assert.Equal(3, Weight.ReadGrams(0.0025).Value);
    }

    [Fact]
    public void ReadsBlankAsUnknownNotAsZero()
    {
        var (value, error) = Weight.ReadGrams(null);
        Assert.Null(error);
        Assert.Null(value);
    }

    [Fact]
    public void RefusesZeroAndSaysWhatToDoInstead()
    {
        var (_, error) = Weight.ReadGrams(0);
        Assert.Contains("Leave it blank", error);
    }

    [Fact]
    public void RefusesANegativeWeight()
    {
        Assert.NotNull(Weight.ReadGrams(-2).Error);
    }

    [Fact]
    public void RefusesAWeightThatIsReallyAMisplacedDecimal()
    {
        // 2500 kg is not a car part, it is 2.5 kg typed wrong.
        Assert.Contains("decimal point", Weight.ReadGrams(2500).Error);
    }

    [Fact]
    public void AcceptsExactlyTheMaximum()
    {
        Assert.Equal(Weight.MaxGrams, Weight.ReadGrams(Weight.MaxGrams / 1000.0).Value);
    }
}
