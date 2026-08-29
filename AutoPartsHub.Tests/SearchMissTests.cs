using AutoPartsHub.Api.Catalogue;

namespace AutoPartsHub.Tests;

/// <summary>
/// What a search term is allowed to become before it is written down.
/// </summary>
/// <remarks>
/// The table exists to answer one question — what did people come looking for
/// and leave without — and everything here is either about making the same
/// search count as the same term, or about the things a search box collects
/// that are not searches at all. Both APIs write into the same table, so both
/// have to normalise a term the same way or the counter splits in two.
/// </remarks>
public class SearchMissTests
{
    /* ------------------------------ making the same search one term --- */

    [Fact]
    public void LowerCases()
    {
        Assert.Equal("brake pad", SearchMisses.ReadTerm("Brake Pad"));
    }

    [Fact]
    public void CollapsesWhitespaceAndTrims()
    {
        Assert.Equal("brake pad", SearchMisses.ReadTerm("  brake   pad \n"));
    }

    [Fact]
    public void GivesTheSameAnswerForEverySpellingOfTheSameSpacing()
    {
        string[] forms = ["Brake Pad", "brake pad", "brake  pad", " BRAKE PAD "];

        Assert.Single(forms.Select(SearchMisses.ReadTerm).Distinct());
    }

    [Fact]
    public void LeavesAPartNumberAloneButForItsCase()
    {
        Assert.Equal("0986424815", SearchMisses.ReadTerm("0986424815"));
        Assert.Equal("w712/75", SearchMisses.ReadTerm("W712/75"));
    }

    /* -------------------------------------- what is not written down --- */

    [Fact]
    public void IgnoresAnEmptySearch()
    {
        Assert.Null(SearchMisses.ReadTerm(""));
        Assert.Null(SearchMisses.ReadTerm("   "));
    }

    [Fact]
    public void IgnoresSomethingLongEnoughToBeAPaste()
    {
        // A search box accepts whatever is on the clipboard. Past a certain
        // length what arrives is a paragraph or an address, not a search.
        Assert.Null(SearchMisses.ReadTerm(new string('x', SearchMisses.MaxTerm + 1)));
        Assert.NotNull(SearchMisses.ReadTerm(new string('x', SearchMisses.MaxTerm)));
    }

    [Fact]
    public void RefusesAnEmailAddress()
    {
        // The one personal detail a search box collects, because it is the
        // first field on the page and people paste into it.
        Assert.Null(SearchMisses.ReadTerm("someone@example.com"));
        Assert.Null(SearchMisses.ReadTerm("brake pad someone@example.com"));
    }

    [Fact]
    public void KeepsALongRunOfDigitsWhichIsAPartNumberAsOftenAsAPhone()
    {
        // Deliberately not refused. `0986424815` is ten digits and is a Bosch
        // filter; there is no rule separating it from a phone number, and one
        // that dropped both would blind the report to exactly the searches it
        // exists to catch — somebody typing the number off the old part.
        Assert.Equal("01234567890", SearchMisses.ReadTerm("01234567890"));
    }

    [Fact]
    public void DoesNotMistakeAPartNumberWithAHyphenForAnAddress()
    {
        Assert.Equal("bp-1234-a", SearchMisses.ReadTerm("BP-1234-A"));
    }
}
