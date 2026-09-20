using AutoPartsHub.Api.Catalogue;
using AutoPartsHub.Domain.Catalogue;

namespace AutoPartsHub.Tests;

/// <summary>
/// What a pack size allows somebody to order.
/// </summary>
/// <remarks>
/// The rule reaches the basket, the order and the quantity control, so it is
/// exercised here rather than three times over HTTP: the arithmetic is the
/// same in all three places and it is the arithmetic that can be wrong.
///
/// Mirrors test/packaging.test.ts in the other repository case for case. The
/// integer division in <c>NearestOrderable</c> is the one to watch — C#
/// truncates toward zero where JavaScript's Math.floor rounds down, which
/// agree for the positive quantities this ever sees and would not for others.
/// </remarks>
public class PackagingTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(99)]
    public void LetsAnythingThroughForAPartPackedSingly(int quantity)
    {
        // The ordinary case, and the one that must cost nothing.
        Assert.True(Packaging.IsOrderableQuantity(quantity, 1));
    }

    [Theory]
    [InlineData(2, 2, true)]
    [InlineData(4, 2, true)]
    [InlineData(3, 2, false)]
    [InlineData(1, 2, false)]
    [InlineData(8, 4, true)]
    [InlineData(6, 4, false)]
    public void AllowsWholePackagesAndRefusesPartOfOne(int quantity, int per, bool expected)
    {
        Assert.Equal(expected, Packaging.IsOrderableQuantity(quantity, per));
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(0, 1)]
    [InlineData(-2, 2)]
    public void RefusesZeroAndNegativesWhateverThePackSize(int quantity, int per)
    {
        // Zero divides by everything, so the multiple test alone would accept
        // it and quietly remove the line.
        Assert.False(Packaging.IsOrderableQuantity(quantity, per));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ConstrainsNothingWhenThePackSizeIsNonsense(int per)
    {
        // Bad data should not take the shop down. The check constraint on the
        // column is what keeps a zero out; this decides what happens if one is
        // there anyway.
        Assert.True(Packaging.IsOrderableQuantity(3, per));
    }

    [Fact]
    public void OffersThePackageEitherSide()
    {
        Assert.Equal((2, 4), Packaging.NearestOrderable(3, 2));
        Assert.Equal((5, 10), Packaging.NearestOrderable(7, 5));
    }

    [Fact]
    public void OffersNothingBelowOnePackage()
    {
        // Rounding 1 down to 0 would be offering to delete the line.
        Assert.Equal((null, 2), Packaging.NearestOrderable(1, 2));
    }

    [Fact]
    public void StepsUpFromAQuantityThatAlreadyFits()
    {
        Assert.Equal((4, 6), Packaging.NearestOrderable(4, 2));
    }

    [Fact]
    public void NamesBothQuantitiesInTheRefusal()
    {
        var message = Packaging.Refusal(3, 2, "pair");
        Assert.Contains("2 or 4", message);
        Assert.Contains("pairs of 2", message);
    }

    [Fact]
    public void NamesOnlyTheOneThatExistsWhenThereIsNoSmallerOrder()
    {
        var message = Packaging.Refusal(1, 10, "box");
        Assert.Contains("Order 10.", message);
        Assert.DoesNotContain("or", message.Split("Order ")[1]);
    }

    [Fact]
    public void DefaultsToTheUnitThatConstrainsNothing()
    {
        Assert.Equal("piece", Packaging.DefaultUnit);
        Assert.Equal("piece", Packaging.Units[0]);
    }

    [Theory]
    [InlineData("piece", true)]
    [InlineData("pair", true)]
    [InlineData("kit", true)]
    [InlineData("crate", false)]
    [InlineData("", false)]
    [InlineData("PIECE", false)]
    public void RecognisesItsOwnListAndNothingElse(string value, bool expected)
    {
        Assert.Equal(expected, Packaging.IsUnit(value));
    }
}
