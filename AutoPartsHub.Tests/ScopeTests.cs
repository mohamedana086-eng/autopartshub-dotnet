using AutoPartsHub.Api.Auth;

namespace AutoPartsHub.Tests;

/// <summary>
/// What each role may be asking about, across the three manager reaches.
/// </summary>
/// <remarks>
/// Five roles times three degrees is fifteen answers, and the backlog asks for
/// all fifteen to be covered. They are cheap to assert because
/// <see cref="Scope"/> is a value with no database in it — which is most of
/// why it is a value.
///
/// Two of the three degrees are not reachable yet: nothing records that a
/// manager was granted somebody else's customers, or all of them. The tests
/// for those are not speculative all the same. They pin down what the degrees
/// will mean the day the storage lands, so that landing it is a change to one
/// resolver and not a negotiation about semantics — and they are what makes
/// adding the case to the enum now, rather than later, worth anything.
/// </remarks>
public class ScopeTests
{
    private static Scope As(string role, ManagerReach reach = ManagerReach.Own,
        params string[] extras) =>
        Scope.From(new SessionPayload("u-self", role, null, "Test", Expiry))
            .WithReach(reach, extras);

    private static long Expiry => DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds();

    // -------------------------------------------------- what a query narrows to

    [Fact]
    public void AnAdminNarrowsToNothing()
    {
        Assert.Null(As(Roles.Admin).ManagedBy);
        Assert.Null(As(Roles.Admin, ManagerReach.All).ManagedBy);
    }

    [Theory]
    [InlineData(ManagerReach.Own)]
    [InlineData(ManagerReach.Selected)]
    public void AManagerBelowTheWholeListNarrowsToThemselves(ManagerReach reach)
    {
        Assert.Equal("u-self", As(Roles.Sales, reach).ManagedBy);
    }

    /// <remarks>
    /// The same null an admin gets, because a <c>WHERE</c> clause cannot tell
    /// the two apart and does not need to. What separates them is the policy
    /// routes, which ask <see cref="Scope.IsAdmin"/> instead — a manager given
    /// every customer has not thereby been given the pricing rules.
    /// </remarks>
    [Fact]
    public void AManagerWithTheWholeListNarrowsToNothingEither()
    {
        var reaching = As(Roles.Sales, ManagerReach.All);

        Assert.Null(reaching.ManagedBy);
        Assert.False(reaching.IsAdmin);
    }

    /// <remarks>
    /// A customer manages nobody, so narrowing a customer query by their own id
    /// matches nothing. That is the failure mode worth having: an admin route
    /// that somehow lost its gate answers with an empty list rather than with
    /// the table.
    /// </remarks>
    [Theory]
    [InlineData(Roles.B2B)]
    [InlineData(Roles.Retail)]
    [InlineData(Roles.SupplierRole)]
    public void ACustomerNarrowsToThemselvesAndSoMatchesNothing(string role)
    {
        Assert.Equal("u-self", As(role).ManagedBy);
        Assert.False(As(role).IsStaff);
    }

    [Fact]
    public void AnAnonymousCallerHasNoIdAndNoReach()
    {
        Assert.False(Scope.Anonymous.IsSignedIn);
        Assert.False(Scope.Anonymous.IsStaff);
        Assert.False(Scope.Anonymous.IsAdmin);
        Assert.Equal(Scope.Anonymous, Scope.From(null));
    }

    // ------------------------------------------------------ reaching a customer

    [Fact]
    public void AnAdminReachesEverybodyWithoutAsking()
    {
        Assert.True(As(Roles.Admin).Reaches("someone-else"));
    }

    [Fact]
    public void AManagerWithTheWholeListReachesEverybodyWithoutAsking()
    {
        Assert.True(As(Roles.Sales, ManagerReach.All).Reaches("someone-else"));
    }

    [Fact]
    public void AGrantedCustomerIsReachedWithoutAsking()
    {
        Assert.True(As(Roles.Sales, ManagerReach.Selected, "c-granted").Reaches("c-granted"));
    }

    /// <remarks>
    /// Null, not false. Whether this manager reaches that customer now depends
    /// on who the account names as its manager, which is a question for the
    /// database — and answering false here to avoid asking would refuse a
    /// manager their own customers.
    /// </remarks>
    [Theory]
    [InlineData(ManagerReach.Own)]
    [InlineData(ManagerReach.Selected)]
    public void OtherwiseTheDatabaseHasToBeAsked(ManagerReach reach)
    {
        Assert.Null(As(Roles.Sales, reach, "c-granted").Reaches("c-somebody-elses"));
    }

    [Theory]
    [InlineData(Roles.B2B)]
    [InlineData(Roles.Retail)]
    [InlineData(Roles.SupplierRole)]
    public void ACustomerReachesNobody(string role)
    {
        Assert.False(As(role).Reaches("anyone"));
        Assert.False(As(role, ManagerReach.All).Reaches("anyone"));
    }

    /// <remarks>
    /// A reach is something an admin grants. Setting <c>All</c> on a role that
    /// is not staff must not turn a customer into one, and this is the assertion
    /// that says so — the reach is checked after the role, never instead of it.
    /// </remarks>
    [Fact]
    public void AReachDoesNotPromoteAnybody()
    {
        Assert.False(As(Roles.Retail, ManagerReach.All).IsStaff);
        Assert.False(As(Roles.Retail, ManagerReach.All).Reaches("anyone"));
    }

    // ------------------------------------------------------ granted lists

    /// <remarks>
    /// Extras mean nothing outside <c>Selected</c>, and are dropped rather than
    /// kept dormant: a list granted last month must not come back to life
    /// because somebody was moved to <c>Selected</c> again for other reasons.
    /// </remarks>
    [Theory]
    [InlineData(ManagerReach.Own)]
    [InlineData(ManagerReach.All)]
    public void LeavingSelectedDropsTheGrantedList(ManagerReach reach)
    {
        var granted = As(Roles.Sales, ManagerReach.Selected, "c-granted");
        Assert.Single(granted.ExtraClientIds);

        Assert.Empty(granted.WithReach(reach).ExtraClientIds);
    }

    [Fact]
    public void TheDefaultReachIsTheNarrowest()
    {
        Assert.Equal(ManagerReach.Own, Scope.From(
            new SessionPayload("u-self", Roles.Sales, null, "Test", Expiry)).Reach);
    }

    // ---------------------------------------------------------- suppliers

    [Fact]
    public void ASupplierSpeaksForTheirOwn()
    {
        var supplier = As(Roles.SupplierRole).ForSupplier("s-mine");

        Assert.True(supplier.Speaks("s-mine"));
        Assert.False(supplier.Speaks("s-theirs"));
    }

    [Fact]
    public void AnAdminSpeaksForEverySupplier()
    {
        Assert.True(As(Roles.Admin).Speaks("s-anyone"));
    }

    /// <remarks>
    /// A manager is staff and still speaks for no supplier. Buying terms are
    /// not a customer-facing thing to be scoped by customer.
    /// </remarks>
    [Fact]
    public void AManagerSpeaksForNoSupplier()
    {
        Assert.False(As(Roles.Sales, ManagerReach.All).Speaks("s-anyone"));
    }
}

/// <summary>
/// Refusing an id that names something outside the caller's scope.
/// </summary>
/// <remarks>
/// Only the decisions that need no database are asserted here, and the null
/// context is how that is enforced: a path that reached for the database would
/// throw rather than quietly pass, so this doubles as the statement that an
/// admin check costs no round trip.
///
/// <see cref="ScopeGuard.EnsureClientAsync"/> and
/// <see cref="ScopeGuard.EnsureOrderAsync"/> have a second half that does read,
/// for the manager who has to be matched against <c>Client.salesManagerId</c>.
/// Covering it needs a real database — T-025, the integration harness this
/// repository does not have yet — and it belongs beside the rest of the
/// scoping battery when that lands.
/// </remarks>
public class ScopeGuardTests
{
    private static readonly IScopeGuard Guard = new ScopeGuard(null!);

    private static Scope As(string role, ManagerReach reach = ManagerReach.Own,
        params string[] extras) =>
        Scope.From(new SessionPayload(
                "u-self", role, null, "Test",
                DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds()))
            .WithReach(reach, extras);

    [Fact]
    public void AnAdminPassesTheAdminCheck() =>
        Assert.True(Guard.EnsureAdmin(As(Roles.Admin)).Ok);

    [Theory]
    [InlineData(Roles.Sales)]
    [InlineData(Roles.SupplierRole)]
    [InlineData(Roles.B2B)]
    [InlineData(Roles.Retail)]
    public void NobodyElseDoes(string role) =>
        Assert.False(Guard.EnsureAdmin(As(role)).Ok);

    /// <remarks>
    /// Including a manager handed every customer. The whole list is a reach
    /// over customers, not a promotion.
    /// </remarks>
    [Fact]
    public void NorDoesAManagerWithTheWholeList() =>
        Assert.False(Guard.EnsureAdmin(As(Roles.Sales, ManagerReach.All)).Ok);

    [Theory]
    [InlineData(Roles.Admin)]
    [InlineData(Roles.Sales)]
    public void StaffPassTheStaffCheck(string role) =>
        Assert.True(Guard.EnsureStaff(As(role)).Ok);

    [Theory]
    [InlineData(Roles.SupplierRole)]
    [InlineData(Roles.B2B)]
    [InlineData(Roles.Retail)]
    public void CustomersAndSuppliersDoNot(string role) =>
        Assert.False(Guard.EnsureStaff(As(role)).Ok);

    /// <remarks>
    /// Refused before the scope is consulted, so an anonymous caller is told to
    /// sign in rather than told that something is not theirs — the second would
    /// be true of everything and useless.
    /// </remarks>
    [Fact]
    public void AnAnonymousCallerIsRefusedEverything()
    {
        Assert.False(Guard.EnsureAdmin(Scope.Anonymous).Ok);
        Assert.False(Guard.EnsureStaff(Scope.Anonymous).Ok);
        Assert.False(Guard.EnsureSupplier(Scope.Anonymous, "s-any").Ok);
    }

    [Fact]
    public void ASupplierMayOnlyNameTheirOwn()
    {
        var supplier = As(Roles.SupplierRole).ForSupplier("s-mine");

        Assert.True(Guard.EnsureSupplier(supplier, "s-mine").Ok);
        Assert.False(Guard.EnsureSupplier(supplier, "s-theirs").Ok);
    }

    [Fact]
    public async Task TheAnswersThatNeedNoReadAreGivenWithoutOne()
    {
        // A null context: any of these reaching for the database fails here.
        Assert.True((await Guard.EnsureClientAsync(As(Roles.Admin), "c-any")).Ok);
        Assert.True((await Guard.EnsureClientAsync(
            As(Roles.Sales, ManagerReach.All), "c-any")).Ok);
        Assert.True((await Guard.EnsureClientAsync(
            As(Roles.Sales, ManagerReach.Selected, "c-granted"), "c-granted")).Ok);
        Assert.False((await Guard.EnsureClientAsync(As(Roles.Retail), "c-any")).Ok);
    }
}
