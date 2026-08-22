using AutoPartsHub.Api.Catalogue;

namespace AutoPartsHub.Tests;

/// <summary>
/// Whether a part can be sold from stock.
/// </summary>
/// <remarks>
/// One distinction carries the whole thing: never counted is not the same as
/// counted and gone. The queries keep them apart — SUM over no shelves is
/// null, and null travels all the way out to the catalogue's responses so an
/// admin can tell an unfilled record from an empty shelf.
///
/// Nothing a customer can do turns on which it is, and that collapse happens
/// in exactly one function. Collapsing the two the other way — reading
/// uncounted as "sell on the lead time" — would put the entire catalogue on
/// sale, so it is pinned here rather than left to the shape of an `if` in a
/// template.
/// </remarks>
public class AvailabilityTests
{
    [Fact]
    public void SellsNothingFromAPartNobodyHasCounted()
    {
        Assert.Equal(0, Availability.Sellable(null));
    }

    [Fact]
    public void SellsNothingFromACountedPartThatHasRunOut()
    {
        Assert.Equal(0, Availability.Sellable(0));
    }

    [Fact]
    public void GivesTheTwoTheSameAnswerThoughTheyAreDifferentFacts()
    {
        // The policy in one line: an unfilled record and an empty shelf are
        // the same to a buyer, even though the responses keep reporting them
        // apart.
        Assert.Equal(Availability.Sellable(null), Availability.Sellable(0));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(6)]
    public void SellsWhatTheQueryCounted(int counted)
    {
        Assert.Equal(counted, Availability.Sellable(counted));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(954)]
    public void PassesACountThroughUntouchedRatherThanReinterpretingIt(int counted)
    {
        // What is promised to an order is already netted off by the query.
        // Doing it again here would take the reserved units off twice.
        Assert.Equal(counted, Availability.Sellable(counted));
    }
}
