using AutoPartsHub.Api.Catalogue;

namespace AutoPartsHub.Tests;

/// <summary>
/// Turning <c>page</c> and <c>pageSize</c> from a query string into a slice.
/// </summary>
/// <remarks>
/// Every value arrives as text and any of it can be absent, negative,
/// fractional or a word — these reach the API from truncated urls, templates
/// that rendered an empty variable, and hand-edited query strings.
///
/// The fractional and out-of-range cases are the ones worth pinning on both
/// sides: JavaScript's Number() accepts "7.9" and Math.floor takes it to 7,
/// where an int parse would fail on it and fall back to the default. That is a
/// different answer to the same request, and only a customer with an odd url
/// would ever find it.
/// </remarks>
public class PagingTests
{
    [Fact]
    public void DefaultsToTheSmallestSizeTheOldSystemOffered()
    {
        Assert.Equal(50, Paging.ReadPageSize(null));
        Assert.Equal(50, Paging.DefaultPageSize);
    }

    [Theory]
    [InlineData("100000")]
    [InlineData("501")]
    public void IsCappedByTheServerNotByTheCaller(string requested)
    {
        // A page size is a request for work. One arriving as 100000 is a
        // mistake or an attempt, and either way the server decides.
        Assert.Equal(Paging.MaxPageSize, Paging.ReadPageSize(requested));
    }

    [Fact]
    public void AcceptsExactlyTheMaximum()
    {
        Assert.Equal(500, Paging.ReadPageSize("500"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("NaN")]
    public void FallsBackRatherThanRefusingNonsense(string? bad)
    {
        Assert.Equal(Paging.DefaultPageSize, Paging.ReadPageSize(bad));
    }

    [Theory]
    [InlineData("7.9", 7)]
    [InlineData("1.2", 1)]
    public void FloorsAFractionRatherThanRejectingIt(string raw, int expected)
    {
        Assert.Equal(expected, Paging.ReadPageSize(raw));
    }

    [Fact]
    public void StillAnswersToTheNameItHadBeforeThereWerePages()
    {
        // The suggestions box asks for six and predates paging entirely.
        Assert.Equal(6, Paging.ReadPageSize(null, "6"));
        Assert.Equal(6, Paging.ReadPageSize("", "6"));
    }

    [Fact]
    public void PrefersPageSizeWhenBothAreGiven()
    {
        Assert.Equal(25, Paging.ReadPageSize("25", "3"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("abc")]
    [InlineData("")]
    public void PageStartsAtOneAndNeverBelow(string? raw)
    {
        Assert.Equal(1, Paging.ReadPage(raw));
    }

    [Theory]
    [InlineData("2.7", 2)]
    [InlineData("99", 99)]
    public void PageFloorsAFraction(string raw, int expected)
    {
        Assert.Equal(expected, Paging.ReadPage(raw));
    }

    [Fact]
    public void ReturnsTheRowsThatPageCovers()
    {
        var rows = Enumerable.Range(1, 64).ToArray();

        Assert.Equal(50, Paging.PageOf(rows, 1, 50).Count());
        Assert.Equal(1, Paging.PageOf(rows, 1, 50).First());
        Assert.Equal(rows.Skip(50), Paging.PageOf(rows, 2, 50));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(10)]
    [InlineData(50)]
    [InlineData(64)]
    [InlineData(500)]
    public void CoversEveryRowExactlyOnceAcrossAllPages(int pageSize)
    {
        // The property that matters. A slice that overlaps shows a part twice;
        // one that gaps loses a part entirely, and nobody notices either from
        // looking at a single page.
        var rows = Enumerable.Range(1, 64).ToArray();
        var pages = Paging.PageCount(rows.Length, pageSize);

        var seen = new List<int>();
        for (var p = 1; p <= pages; p++) seen.AddRange(Paging.PageOf(rows, p, pageSize));

        Assert.Equal(rows, seen);
        Assert.Equal(rows.Length, seen.Distinct().Count());
    }

    [Fact]
    public void GivesAnEmptyPagePastTheEndRatherThanTheLastOneAgain()
    {
        // Clamping would answer a different question from the one asked while
        // echoing back the page number that was asked for.
        Assert.Empty(Paging.PageOf(Enumerable.Range(1, 64).ToArray(), 99, 50));
    }

    [Theory]
    [InlineData(64, 50, 2)]
    [InlineData(64, 64, 1)]
    [InlineData(65, 64, 2)]
    [InlineData(1, 50, 1)]
    public void CountsPagesSoTheLastIsReachableAndNoFurther(int total, int pageSize, int expected)
    {
        Assert.Equal(expected, Paging.PageCount(total, pageSize));
    }

    [Fact]
    public void ReportsZeroPagesForNoResultsNotOneEmptyOne()
    {
        Assert.Equal(0, Paging.PageCount(0, 50));
    }
}
