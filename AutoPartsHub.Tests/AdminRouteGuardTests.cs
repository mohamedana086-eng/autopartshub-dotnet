using System.Text.Json;
using AutoPartsHub.Api.Auth;
using Microsoft.AspNetCore.Http;

namespace AutoPartsHub.Tests;

/// <summary>
/// The lock on <c>/api/admin</c> that does not depend on a handler.
/// </summary>
/// <remarks>
/// <see cref="AdminWriteScopeTests"/> asserts that every admin handler names a
/// gate, which is a fact about the source. This asserts what happens to the
/// request, which is a fact about the pipeline — and the two fail differently:
/// a handler that names its gate and answers anyway passes the first and is
/// caught by this.
///
/// The middleware is run directly against a <see cref="DefaultHttpContext"/>.
/// There is no server here and none is needed: what it decides comes from the
/// path and one cookie.
/// </remarks>
public class AdminRouteGuardTests
{
    private static readonly SessionTokens Tokens = new("a-secret-long-enough-for-a-test");

    private static long InAMinute => DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds();

    /// <summary>Runs the guard over one request. Returns the status, and whether it went through.</summary>
    private static async Task<(int Status, bool Reached, string Body)> Request(
        string path, string? role, string method = "GET")
    {
        var reached = false;
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Method = method;
        context.Response.Body = new MemoryStream();

        if (role is not null)
        {
            var token = Tokens.Encode(new SessionPayload("u-1", role, null, "Test", InAMinute));
            context.Request.Headers.Cookie = $"{SessionTokens.CookieName}={token}";
        }

        var guard = new AdminRouteGuard(_ => { reached = true; return Task.CompletedTask; }, Tokens);
        await guard.InvokeAsync(context);

        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();

        return (context.Response.StatusCode, reached, body);
    }

    [Theory]
    [InlineData(Roles.Admin)]
    [InlineData(Roles.Sales)]
    public async Task StaffGoThrough(string role)
    {
        var (_, reached, _) = await Request("/api/admin/clients", role);

        Assert.True(reached);
    }

    /// <remarks>
    /// A supplier is not staff. Selling through the shop is not working in it,
    /// and the admin pages carry every customer's orders.
    /// </remarks>
    [Theory]
    [InlineData(Roles.SupplierRole)]
    [InlineData(Roles.B2B)]
    [InlineData(Roles.Retail)]
    public async Task CustomersAndSuppliersDoNot(string role)
    {
        var (status, reached, _) = await Request("/api/admin/clients", role);

        Assert.Equal(403, status);
        Assert.False(reached);
    }

    [Fact]
    public async Task AnAnonymousCallerIsToldToSignIn()
    {
        var (status, reached, body) = await Request("/api/admin/clients", null);

        Assert.Equal(401, status);
        Assert.False(reached);
        Assert.Equal("Not signed in.", Error(body));
    }

    /// <remarks>
    /// The same sentences <see cref="AdminGate"/> uses. A route that reaches
    /// this instead of its own gate must not produce a response the storefront
    /// has never seen.
    /// </remarks>
    [Fact]
    public async Task TheRefusalsAreWordedAsTheGateWordsThem()
    {
        Assert.Equal("Admin access required.", Error((await Request("/api/admin/x", Roles.Retail)).Body));
    }

    /// <remarks>
    /// An unknown role is not staff. <see cref="Roles.Narrow"/> sends anything
    /// unrecognised to RETAIL, and a guard that read the string directly would
    /// let a typo through.
    /// </remarks>
    [Theory]
    [InlineData("ADMINISTRATOR")]
    [InlineData("admin")]
    [InlineData("")]
    public async Task AnUnknownRoleIsNotStaff(string role)
    {
        Assert.Equal(403, (await Request("/api/admin/clients", role)).Status);
    }

    [Fact]
    public async Task AForgedCookieIsNoCookie()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/admin/clients";
        context.Request.Headers.Cookie = $"{SessionTokens.CookieName}=made.up";
        context.Response.Body = new MemoryStream();

        var reached = false;
        await new AdminRouteGuard(_ => { reached = true; return Task.CompletedTask; }, Tokens)
            .InvokeAsync(context);

        Assert.Equal(401, context.Response.StatusCode);
        Assert.False(reached);
    }

    // ------------------------------------------------------ what it leaves alone

    [Theory]
    [InlineData("/api/catalog/search")]
    [InlineData("/api/suppliers")]
    [InlineData("/api/cart")]
    [InlineData("/api/auth/login")]
    [InlineData("/health")]
    public async Task EverythingElseIsUntouched(string path)
    {
        var (_, reached, _) = await Request(path, null);

        Assert.True(reached);
    }

    /// <remarks>
    /// Segment matching, not prefix matching. <c>/api/administrators</c> starts
    /// with the same characters and is not an admin route; a guard that used
    /// <c>StartsWith</c> would refuse it, and — the direction that actually
    /// costs something — the same sloppiness in reverse is how a guard ends up
    /// not covering what it was written for.
    /// </remarks>
    [Fact]
    public async Task ARouteThatMerelyStartsWithTheSameLettersIsNotAnAdminRoute()
    {
        var (_, reached, _) = await Request("/api/administrators", null);

        Assert.True(reached);
    }

    [Fact]
    public async Task TheGuardCoversEveryDepthBeneathIt()
    {
        Assert.Equal(401, (await Request("/api/admin", null)).Status);
        Assert.Equal(401, (await Request("/api/admin/price-lists/abc/items/def", null)).Status);
    }

    /// <remarks>
    /// Reads as well as writes. The client list is a read, and it is the thing
    /// most worth not handing out.
    /// </remarks>
    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("PATCH")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task EveryMethodIsCovered(string method)
    {
        Assert.Equal(403, (await Request("/api/admin/clients", Roles.Retail, method)).Status);
    }

    private static string? Error(string body) =>
        JsonDocument.Parse(body).RootElement.GetProperty("error").GetString();
}
