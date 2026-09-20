using AutoPartsHub.Api.Auth;
using AutoPartsHub.Api.Catalogue;

namespace AutoPartsHub.Tests;

/// <summary>
/// A customer must never be told which supplier a part comes from.
/// </summary>
/// <remarks>
/// The margin the platform earns is the gap between what it pays and what it
/// charges, and a customer who can read the supplier's name off a search
/// result can close that gap by ringing them. So every customer-facing
/// response publishes <c>Supplier.Code</c> where staff see
/// <c>Supplier.Name</c>, and <see cref="SupplierNaming"/> is the only thing
/// allowed to decide which.
///
/// Two kinds of assertion here, because the rule has two halves.
///
/// The behaviour — that each role gets the right one of the two names — is a
/// unit test over the mapper, and it is short because the mapper is.
///
/// The reach — that no route quietly builds a supplier reference of its own —
/// cannot be tested that way at all: a route that never calls the mapper is
/// exactly the route a test of the mapper does not run. So it is asserted
/// against the source, the same way <see cref="AdminWriteScopeTests"/> asserts
/// which gate a write names. A new customer-facing route that carries a
/// supplier fails these until it goes through the mapper.
///
/// WHAT IS NOT HERE YET
/// --------------------
/// The backlog also asks for the end-to-end version: place a real request to
/// each of the six routes as each customer role and fail on any seeded
/// supplier name appearing anywhere in the body. That needs a running
/// application and a seeded database — <c>WebApplicationFactory</c> and
/// Testcontainers, which is T-025 and does not exist in this repository yet.
/// When it lands, the walk belongs beside these.
/// </remarks>
public class SupplierAnonymityTests
{
    // ---------------------------------------------------------- behaviour

    [Theory]
    [InlineData(Roles.Admin)]
    [InlineData(Roles.Sales)]
    public void StaffSeeTheRealName(string role)
    {
        Assert.Equal("IB16 Parts", SupplierNaming.For(role).Of("IB16 Parts", "IB16"));
    }

    [Theory]
    [InlineData(Roles.B2B)]
    [InlineData(Roles.Retail)]
    [InlineData(Roles.SupplierRole)]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("something-nobody-defined")]
    public void EverybodyElseSeesTheCode(string? role)
    {
        Assert.Equal("IB16", SupplierNaming.For(role).Of("IB16 Parts", "IB16"));
    }

    /// <remarks>
    /// A supplier reading the catalogue is reading their competitors' offers.
    /// Their own name is theirs to see in the portal, not here, and a rule that
    /// made an exception for "their own" rows would need to know which rows
    /// those were on every route that carries one.
    /// </remarks>
    [Fact]
    public void ASupplierIsNotStaff()
    {
        Assert.False(SupplierNaming.For(Roles.SupplierRole).ShowsRealNames);
    }

    /// <remarks>
    /// An unknown role string must not be read as staff. <see cref="Roles.Narrow"/>
    /// already sends anything unrecognised to RETAIL, and this pins that down
    /// from the anonymity side: the failure mode worth preventing is a typo in
    /// a role name opening the names to everyone who has one.
    /// </remarks>
    [Fact]
    public void AnUnknownRoleIsNotStaff()
    {
        Assert.False(SupplierNaming.For("ADMINISTRATOR").ShowsRealNames);
        Assert.False(SupplierNaming.For("admin").ShowsRealNames);
    }

    [Fact]
    public void APartWithNoSupplierHasNoReference()
    {
        var naming = SupplierNaming.For(Roles.Retail);

        Assert.Null(naming.OfMaybe(null, null));
        Assert.Null(naming.Search(null, null, null, null, null, null));
    }

    /// <remarks>
    /// The fallback in <c>OfMaybe</c> runs one way only. Staff may fall back to
    /// the code, because showing a code to somebody entitled to the name costs
    /// nothing. Nobody else may fall back to the name, because a fallback that
    /// can reach it is a leak waiting for the row that triggers it.
    /// </remarks>
    [Fact]
    public void TheFallbackNeverReachesTheName()
    {
        Assert.Null(SupplierNaming.For(Roles.Retail).OfMaybe("IB16 Parts", null));
        Assert.Equal("IB16", SupplierNaming.For(Roles.Admin).OfMaybe(null, "IB16"));
    }

    [Fact]
    public void TheSearchReferenceCarriesTheChosenName()
    {
        var mine = SupplierNaming.For(Roles.Retail)
            .Search("ib16-parts", "IB16 Parts", "IB16", 5, "official", true);

        Assert.NotNull(mine);
        Assert.Equal("IB16", mine!.Name);
        // Everything else is unchanged: anonymity is about the name, and a
        // customer still needs the rating and the terms to choose an offer.
        Assert.Equal("ib16-parts", mine.Slug);
        Assert.Equal(5, mine.Rating);
        Assert.Equal("official", mine.Reliability);
        Assert.True(mine.AcceptsReturns);
    }

    // -------------------------------------------------------------- reach

    /// <summary>
    /// The routes a customer can reach that carry, or could carry, a supplier.
    /// </summary>
    /// <remarks>
    /// An explicit list rather than "every file under Endpoints", because the
    /// admin and portal routes show real names on purpose and a test that
    /// could not tell the two apart would either fail on them or be switched
    /// off. Adding a customer-facing route means adding it here, which is the
    /// same shape of deliberate edit <c>AdminWriteScopeTests</c> asks for.
    /// </remarks>
    private static readonly string[] CustomerFacing =
    [
        "SearchEndpoints.cs",
        "ProductEndpoints.cs",
        "CatalogueEndpoints.cs",
        "SupplierPageEndpoints.cs",
        "CartEndpoints.cs",
        "BulkLookupEndpoints.cs",
    ];

    private static DirectoryInfo SolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AutoPartsHub.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!;
    }

    private static string ApiFile(params string[] parts) =>
        File.ReadAllText(Path.Combine([SolutionRoot().FullName, "AutoPartsHub.Api", .. parts]));

    private static string EndpointSource(string name) => ApiFile("Endpoints", name);

    [Fact]
    public void EveryCustomerFacingSourceIsWhereThisTestThinksItIs()
    {
        // A path that stopped resolving would make every assertion below pass
        // by throwing nothing and reading nothing, which is how a source-level
        // test rots without saying so.
        foreach (var name in CustomerFacing)
        {
            Assert.False(string.IsNullOrWhiteSpace(EndpointSource(name)), name);
        }
    }

    /// <remarks>
    /// The acceptance criterion in the backlog is that no supplier reference is
    /// built by hand outside the mapper. That is checkable exactly: the DTO has
    /// one constructor, and it may be called in one file.
    /// </remarks>
    [Fact]
    public void OnlyTheMapperBuildsASupplierReference()
    {
        var api = Path.Combine(SolutionRoot().FullName, "AutoPartsHub.Api");
        var offenders = Directory
            .EnumerateFiles(api, "*.cs", SearchOption.AllDirectories)
            // obj/ holds generated copies of the same source.
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => File.ReadAllText(f).Contains("new SearchSupplierDto("))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Equal(["SupplierReference.cs"], offenders);
    }

    /// <remarks>
    /// A route that reads a supplier's name out of the database and does not
    /// mention the mapper is either publishing the name or about to.
    /// </remarks>
    [Theory]
    [InlineData("SearchEndpoints.cs")]
    [InlineData("ProductEndpoints.cs")]
    [InlineData("CatalogueEndpoints.cs")]
    [InlineData("SupplierPageEndpoints.cs")]
    public void ARouteThatReadsASupplierNameGoesThroughTheMapper(string name)
    {
        var source = EndpointSource(name);

        Assert.True(
            source.Contains("SupplierNaming") || source.Contains("naming."),
            $"{name} reads a supplier name and never asks SupplierNaming which one to publish.");
    }

    /// <remarks>
    /// The basket and the bulk lookup price against a supplier and name none:
    /// both work from ids the whole way through. Asserted rather than assumed,
    /// because the natural way to add "which supplier is this from" to either
    /// response is to join the name in, and that is the change this catches.
    /// </remarks>
    [Theory]
    [InlineData("CartEndpoints.cs")]
    [InlineData("BulkLookupEndpoints.cs")]
    public void TheBasketAndTheBulkLookupNameNoSupplierAtAll(string name)
    {
        var source = EndpointSource(name);

        Assert.False(
            source.Contains("SupplierName"),
            $"{name} now carries a supplier name; it has to go through SupplierNaming.");
    }

    /// <remarks>
    /// Both names have to reach the mapper for it to have a choice to make, so
    /// the queries that feed the customer-facing routes select the code beside
    /// the name. A projection that dropped the code would leave
    /// <c>OfMaybe</c> with nothing to publish, and a customer would see null
    /// where a supplier should be.
    /// </remarks>
    [Theory]
    [InlineData("Catalogue", "SearchQueries.cs")]
    [InlineData("Endpoints", "ProductEndpoints.cs")]
    public void TheQueriesCarryBothNames(string folder, string name)
    {
        var source = ApiFile(folder, name);

        Assert.Contains("\"SupplierName\"", source);
        Assert.Contains("\"SupplierCode\"", source);
    }
}
