using AutoPartsHub.Api.Catalogue;

namespace AutoPartsHub.Tests;

/// <summary>
/// What the near-miss search asks a full-text index.
/// </summary>
/// <remarks>
/// The search condition is passed as a parameter, so nothing here is about SQL
/// injection — it is about full-text SYNTAX, which is a second little language
/// inside that parameter and has its own way of being malformed. A stray
/// double quote in it is not a security hole; it is a statement that fails,
/// on the exact path that only runs when a customer has already typed
/// something wrong once.
///
/// The rule that makes it safe is narrow enough to state: only letters and
/// digits survive, so the quotes this builds are the only quotes in the
/// string. These tests are that rule.
///
/// No database — this is text.
/// </remarks>
public class FullTextTermsTests
{
    /// <remarks>
    /// OR, not AND: the case this exists for is one of the words being wrong,
    /// and AND would throw away what the other word found.
    /// </remarks>
    [Fact]
    public void TheTermsAreJoinedWithOr() =>
        Assert.Equal("\"oil*\" OR \"filter*\"", FullTextSearch.TermsFor("oil filter"));

    [Fact]
    public void OneWordIsOneTerm() =>
        Assert.Equal("\"brake*\"", FullTextSearch.TermsFor("brake"));

    /// <remarks>
    /// Punctuation is what a part number is full of, and a full-text condition
    /// cannot hold it. Dropping it splits the number into terms, which is
    /// harmless: the part-number lane is the one that answers those, and it
    /// does not come through here.
    /// </remarks>
    [Theory]
    [InlineData("brake-pad", "\"brake*\" OR \"pad*\"")]
    [InlineData("W712/30", "\"W712*\" OR \"30*\"")]
    [InlineData("  brake   pad  ", "\"brake*\" OR \"pad*\"")]
    public void OnlyLettersAndDigitsSurvive(string typed, string expected) =>
        Assert.Equal(expected, FullTextSearch.TermsFor(typed));

    /// <summary>
    /// The property the escaping rests on: no quote reaches the condition
    /// except the ones it adds.
    /// </summary>
    /// <remarks>
    /// Asserted against the shapes that would break the syntax if they got
    /// through — a quote, the NEAR and AND operators' punctuation, a wildcard
    /// of the caller's own. Each is a failed statement rather than a breach,
    /// and each would only ever be seen by whoever typed it.
    /// </remarks>
    [Theory]
    [InlineData("brake\" OR \"x")]
    [InlineData("brake* NEAR pad")]
    [InlineData("\"\"\"")]
    [InlineData("pad~&|()")]
    public void NothingTheCallerTypesReachesTheConditionAsSyntax(string typed)
    {
        var terms = FullTextSearch.TermsFor(typed);
        if (terms is null) return;

        // Every quote is one this method wrote: they come in pairs, each pair
        // holding one run of letters and digits followed by the star.
        Assert.Equal(0, terms.Count(c => c == '"') % 2);
        Assert.All(terms.Split(" OR "), term =>
            Assert.Matches("^\"[A-Za-z0-9]+\\*\"$", term));
    }

    /// <remarks>
    /// A single character is a prefix that matches most of a catalogue, so it
    /// is not worth a term. All of them being too short means there is nothing
    /// to ask, and null is how the caller is told to use the other lane rather
    /// than send an empty condition.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a b c")]
    [InlineData("-/.")]
    public void AQueryWithNoWordWorthAskingAboutIsNull(string typed) =>
        Assert.Null(FullTextSearch.TermsFor(typed));

    [Fact]
    public void AWordOfTwoIsWorthAsking() =>
        Assert.Equal("\"ab*\"", FullTextSearch.TermsFor("a ab"));

    /// <remarks>
    /// A near-miss search runs on whatever was typed, and that is sometimes a
    /// pasted line from a quotation. The cap is there so the size of the
    /// condition is not caller input.
    /// </remarks>
    [Fact]
    public void ALongQueryIsCappedAtSixTerms()
    {
        var terms = FullTextSearch.TermsFor("one two three four five six seven eight nine");

        Assert.Equal(6, terms!.Split(" OR ").Length);
        Assert.StartsWith("\"one*\"", terms);
        Assert.DoesNotContain("seven", terms);
    }
}
