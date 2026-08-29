using System.Text.RegularExpressions;

namespace AutoPartsHub.Tests;

/// <summary>
/// Every query that mentions the best offer has to join it, and join it left.
/// </summary>
/// <remarks>
/// A part can be bought from several suppliers, and which offer prices it
/// comes from the <c>BestOffer</c> view. A dozen queries now reference it — in
/// their SELECT, in the supplier join, in a COALESCE, and in the condition
/// deciding whether a part is still sellable.
///
/// A query that mentions <c>bo</c> without joining it is a Postgres error,
/// which sounds like the safe kind of mistake. It is not, because <b>nothing
/// here runs SQL</b>: the compiler does not read a raw string literal, the
/// tests do not open a database, and the build does not either. The first
/// thing to notice would be a customer's search returning a 500.
///
/// Three other shapes are caught. A join nobody uses is one somebody added
/// while moving a query across and then rewrote past. A join that comes AFTER
/// something reads its alias is invalid SQL — the supplier join reads
/// <c>bo."supplierId"</c>, so the order of those two lines is load-bearing.
/// And an inner join fails silently rather than loudly: it does not error, it
/// quietly stops returning the parts nobody has priced yet, which reads as a
/// small catalogue rather than as a bug. A query that means the inner join
/// says so in the SQL — see <c>InnerByDesign</c> below.
///
/// This found two on the day it was written: a missing join in
/// <c>CartEndpoints</c>, and the supplier join ordered before the offer join
/// in two of the search queries.
/// </remarks>
public partial class BestOfferJoinTests
{
    /// <summary>The join, in either shape.</summary>
    [GeneratedRegex(@"\b(?:LEFT\s+)?JOIN\s+""BestOffer""\s+bo\b")]
    private static partial Regex Join();

    /// <summary>
    /// How a query says it means the inner join.
    /// </summary>
    /// <remarks>
    /// A marker in the SQL rather than a list of exempt files kept over here:
    /// the decision is then written where it is made, and a file that grows a
    /// second query does not inherit the exemption its first one was granted —
    /// which matters more here than in the Node port, where these queries sit
    /// several to a file.
    /// </remarks>
    [GeneratedRegex(@"--\s*inner by design", RegexOptions.IgnoreCase)]
    private static partial Regex InnerByDesign();

    /// <summary>A raw-string SQL block: <c>$""" … """</c>.</summary>
    [GeneratedRegex(@"\$""""""[\s\S]*?""""""")]
    private static partial Regex SqlBlock();

    [GeneratedRegex(@"\bbo\.""")]
    private static partial Regex Reference();

    /// <summary>A join line that reads the offer alias.</summary>
    [GeneratedRegex(@"JOIN[^\n]*\bbo\.""")]
    private static partial Regex ReadsInAJoin();

    [GeneratedRegex(@"\bFROM\s+""")]
    private static partial Regex IsAQuery();

    private static IEnumerable<(string File, string Sql)> Queries()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AutoPartsHub.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);

        var api = Path.Combine(dir!.FullName, "AutoPartsHub.Api");

        foreach (var file in Directory.GetFiles(api, "*.cs", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(file);
            if (!source.Contains("bo.\"") && !Join().IsMatch(source)) continue;

            foreach (Match block in SqlBlock().Matches(source))
            {
                // Raw strings hold more than queries. A block with no FROM is
                // not one, and asking it about joins would be asking nonsense.
                if (!IsAQuery().IsMatch(block.Value)) continue;

                yield return (Path.GetFileName(file), block.Value);
            }
        }
    }

    [Fact]
    public void FoundTheQueriesThatUseItAtAll()
    {
        // A scan that matched nothing would make the assertions below pass by
        // being vacuous, which is how this kind of test fails silently.
        var using_ = Queries().Count(q => Reference().IsMatch(q.Sql));

        Assert.True(using_ >= 8, $"only found {using_} queries reading the offer");
    }

    [Fact]
    public void IsPresentInEveryQueryThatReadsFromIt()
    {
        var missing = Queries()
            .Where(q => Reference().IsMatch(q.Sql) && !Join().IsMatch(q.Sql))
            .Select(q => q.File)
            .ToArray();

        Assert.Empty(missing);
    }

    [Fact]
    public void IsAbsentFromEveryQueryThatDoesNot()
    {
        var unused = Queries()
            .Where(q => Join().IsMatch(q.Sql) && !Reference().IsMatch(q.Sql))
            .Select(q => q.File)
            .ToArray();

        Assert.Empty(unused);
    }

    [Fact]
    public void ComesBeforeAnythingThatReadsItInAJoinCondition()
    {
        var wrong = Queries()
            .Where(q => Join().IsMatch(q.Sql)
                     && ReadsInAJoin().IsMatch(q.Sql[..Join().Match(q.Sql).Index]))
            .Select(q => q.File)
            .ToArray();

        Assert.Empty(wrong);
    }

    [Fact]
    public void IsALeftJoinEverywhereTheMissingOfferIsNotThePoint()
    {
        var inner = Queries()
            .Where(q => !InnerByDesign().IsMatch(q.Sql))
            .Where(q => Join().Match(q.Sql) is { Success: true } m
                     && !m.Value.Contains("LEFT", StringComparison.OrdinalIgnoreCase))
            .Select(q => q.File)
            .ToArray();

        Assert.Empty(inner);
    }
}
