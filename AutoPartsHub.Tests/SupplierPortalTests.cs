using System.Text.RegularExpressions;

namespace AutoPartsHub.Tests;

/// <summary>
/// What the supplier portal is not allowed to send.
/// </summary>
/// <remarks>
/// A supplier is not staff with fewer permissions. They are a counterparty,
/// and two fields on the rows they are shown would be worth money to them:
///
///   who bought it   — our customer list, which they could sell to directly
///   what we sold it — OrderItem.unitPrice embeds the markup, so a supplier
///                     who reads it knows exactly what we make on their parts
///                     and opens the next negotiation holding that number
///
/// Neither can be caught by a type: both are ordinary columns on tables the
/// portal already joins, and adding one to a SELECT is a one-word edit that
/// reviews cleanly. So the queries are read as text and the words are refused.
/// The other API has the same test over its own module.
///
/// This is a blunt instrument on purpose. A false positive here costs somebody
/// a rename; a false negative costs the customer list.
/// </remarks>
public partial class SupplierPortalTests
{
    /// <summary>
    /// Words that must not appear in it.
    /// </summary>
    /// <remarks>
    /// <c>"Client"</c> covers the table itself: there is no reason for a portal
    /// query to reach the customer at all, so the join is refused rather than
    /// the columns one at a time. The GATE is excluded from this scan and does
    /// read that table — that is how it finds which supplier an account speaks
    /// for — and it selects nothing from the row but the supplier beside it.
    /// </remarks>
    private static readonly string[] Forbidden =
    [
        "unitPrice",
        "clientId",
        "clientName",
        "\"Client\"",
        "discountPercent",
        "currencyCode",
        "currencyRate",
        "email",
    ];

    [GeneratedRegex(@"/\*[\s\S]*?\*/")] private static partial Regex BlockComment();
    [GeneratedRegex(@"^\s*///.*$", RegexOptions.Multiline)] private static partial Regex DocComment();
    [GeneratedRegex(@"(^|[^:])//.*$", RegexOptions.Multiline)] private static partial Regex LineComment();

    private static string PortalSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AutoPartsHub.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);

        return File.ReadAllText(Path.Combine(
            dir!.FullName, "AutoPartsHub.Api", "Endpoints", "SupplierPortalEndpoints.cs"));
    }

    /// <summary>The source with its comments removed.</summary>
    /// <remarks>
    /// The file explains at length WHY it does not send the unit price, and a
    /// scan that could not tell the explanation from the leak would make the
    /// explanation impossible to write.
    /// </remarks>
    private static string PortalCode()
    {
        var source = PortalSource();
        source = DocComment().Replace(source, "");
        source = BlockComment().Replace(source, "");
        return LineComment().Replace(source, "$1");
    }

    [Fact]
    public void TheScanFoundThePortalAtAll()
    {
        // A read that returned nothing would make every assertion below pass
        // by being vacuous, which is how this kind of test fails silently.
        Assert.Contains("/api/supplier/orders", PortalCode());
        Assert.Contains("/api/supplier/stock", PortalCode());
        Assert.Contains("/api/supplier/summary", PortalCode());
    }

    [Fact]
    public void NamesNoneOfTheForbiddenFields()
    {
        var code = PortalCode();

        foreach (var word in Forbidden)
        {
            Assert.DoesNotContain(word, code);
        }
    }

    [Fact]
    public void StillMentionsThemInTheExplanation()
    {
        // The other half of the previous test: if the comment stripping ever
        // stopped working, that test would fail; if it started stripping the
        // code as well, that test would pass for the wrong reason. This is
        // what tells the two apart.
        Assert.Contains("unitPrice", PortalSource());
    }

    [Fact]
    public void NarrowsEveryQueryToOneSupplier()
    {
        // Each SqlQuery in the file. One without the condition would be the
        // whole order book rather than one supplier's share of it.
        var queries = PortalCode().Split("SqlQuery<").Skip(1).ToList();

        Assert.True(queries.Count >= 3, $"only found {queries.Count} queries");

        foreach (var query in queries)
        {
            Assert.Contains("\"supplierId\" = {supplierId}", query);
        }
    }

    [Fact]
    public void TakesTheSupplierFromTheGateAndNeverFromTheRequest()
    {
        // The one edit that would turn this into somebody else's portal is a
        // supplier id read off the query string.
        var code = PortalCode();

        Assert.Contains("g.SupplierId", code);
        Assert.DoesNotMatch(new Regex(@"Query\[\s*""supplier", RegexOptions.IgnoreCase), code);
    }

    [Fact]
    public void GatesEveryEndpoint()
    {
        var code = PortalCode();
        var mapped = Regex.Matches(code, @"app\.MapGet\(").Count;
        var gated = Regex.Matches(code, @"await gate\.Require\(").Count;

        Assert.Equal(mapped, gated);
    }
}
