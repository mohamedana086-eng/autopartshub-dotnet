using AutoPartsHub.Api.Auth;

namespace AutoPartsHub.Tests;

/// <summary>
/// The signing key, and the three ways a deployment ends up forgeable.
/// </summary>
/// <remarks>
/// Unset, still the published example value, or too short to matter. All three
/// let anyone mint an admin cookie, and none of them looks like a mistake at
/// the time — which is why they are a refusal to start rather than a warning
/// in a log nobody reads.
///
/// The C# version takes a bool rather than the environment name the Node one
/// reads, because the host has already resolved the environment by the time
/// this is called. Same rules, one fewer thing to get wrong.
/// </remarks>
public class AuthSecretTests
{
    private const string Placeholder = "dev-only-insecure-secret-change-me";
    private static readonly string Good = new('a', 64);

    [Fact]
    public void TakesWhatIsConfiguredInProduction()
    {
        Assert.Equal(Good, AuthSecret.Resolve(Good, isProduction: true));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void RefusesToSignWithNothingInProduction(string? configured)
    {
        // Silently falling back is how a deployment ends up forgeable without
        // anyone doing something visibly wrong.
        var e = Assert.Throws<InvalidOperationException>(
            () => AuthSecret.Resolve(configured, isProduction: true));

        Assert.Contains("not set", e.Message);
    }

    [Fact]
    public void RefusesThePlaceholderInProduction()
    {
        // The likeliest mistake by far: the example file ships this value and
        // the first step in the README is to copy that file.
        var e = Assert.Throws<InvalidOperationException>(
            () => AuthSecret.Resolve(Placeholder, isProduction: true));

        Assert.Contains("example file", e.Message);
    }

    [Theory]
    [InlineData("short", "5 characters")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "31 characters")]
    public void RefusesASecretTooShortToBeWorthHaving(string configured, string expected)
    {
        var e = Assert.Throws<InvalidOperationException>(
            () => AuthSecret.Resolve(configured, isProduction: true));

        // The count is in the message because "too short" without a number
        // leaves the reader guessing at the threshold.
        Assert.Contains(expected, e.Message);
    }

    [Fact]
    public void AcceptsExactlyTheMinimum()
    {
        var atTheLine = new string('a', 32);

        Assert.Equal(atTheLine, AuthSecret.Resolve(atTheLine, isProduction: true));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(Placeholder)]
    [InlineData("short")]
    public void SaysHowToGenerateOneWhicheverWayItRefused(string? bad)
    {
        var e = Assert.Throws<InvalidOperationException>(
            () => AuthSecret.Resolve(bad, isProduction: true));

        Assert.Contains("openssl rand -hex 32", e.Message);
    }

    [Theory]
    [InlineData(null, Placeholder)]
    [InlineData("", Placeholder)]
    [InlineData(Placeholder, Placeholder)]
    [InlineData("short", "short")]
    public void LeavesDevelopmentAlone(string? configured, string expected)
    {
        // A local app that will not start until you invent a secret is a worse
        // first five minutes for no gain — nothing local is exposed.
        Assert.Equal(expected, AuthSecret.Resolve(configured, isProduction: false));
    }
}

/// <summary>
/// Narrowing the role column.
/// </summary>
/// <remarks>
/// The column is free text, so an unrecognised value has to fail closed.
/// Reading it as "unknown, therefore allow" is how a typo becomes a login.
/// </remarks>
public class RoleTests
{
    [Theory]
    [InlineData("ADMIN")]
    [InlineData("SALES")]
    [InlineData("SUPPLIER")]
    [InlineData("B2B")]
    [InlineData("RETAIL")]
    public void PassesThroughTheRolesTheAppKnows(string role)
    {
        Assert.Equal(role, Roles.Narrow(role));
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("Admin")]
    [InlineData("SUPERUSER")]
    [InlineData("supplier")]
    [InlineData("")]
    [InlineData(null)]
    public void FallsBackToTheLeastPrivilegedRoleForAnythingElse(string? value)
    {
        Assert.Equal("RETAIL", Roles.Narrow(value));
    }

    [Theory]
    [InlineData("ADMIN", true)]
    [InlineData("SALES", true)]
    [InlineData("SUPPLIER", false)]
    [InlineData("B2B", false)]
    [InlineData("RETAIL", false)]
    [InlineData("nonsense", false)]
    public void OnlyAdminAndSalesAreStaff(string role, bool expected)
    {
        // SUPPLIER is the one worth stating. Selling through this shop is not
        // working in it, and the admin panel carries every customer's orders
        // and every supplier's costs.
        Assert.Equal(expected, Roles.IsStaff(role));
    }

    [Theory]
    [InlineData("ADMIN", true)]
    [InlineData("SALES", false)]
    [InlineData("SUPPLIER", false)]
    [InlineData("admin", false)]
    public void OnlyAdminIsAdmin(string role, bool expected)
    {
        Assert.Equal(expected, Roles.IsAdmin(role));
    }
}
