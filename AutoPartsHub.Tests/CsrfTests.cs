using AutoPartsHub.Api.Auth;

namespace AutoPartsHub.Tests;

/// <summary>
/// Cross-site request forgery.
/// </summary>
/// <remarks>
/// Two of these are not really tests of behaviour, they are tests of a
/// contract with something outside this repository: Angular's HttpClient reads
/// a cookie called XSRF-TOKEN and writes a header called X-XSRF-TOKEN, and
/// neither name is configured anywhere in the storefront. Getting either wrong
/// does not fail to compile — it fails every write in the application, at
/// runtime, with a 403 that looks like a session problem.
///
/// The other API asserts the same two names in its own suite. They have to
/// agree with each other as well as with Angular, because a customer's browser
/// may be talking to either.
/// </remarks>
public class CsrfTests
{
    [Fact]
    public void ReadsTheCookieAngularWrites()
    {
        Assert.Equal("XSRF-TOKEN", Csrf.CookieName);
    }

    [Fact]
    public void ReadsTheHeaderAngularSends()
    {
        Assert.Equal("X-XSRF-TOKEN", Csrf.HeaderName);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    [InlineData("post")]
    public void GuardsEverythingThatCanChangeData(string method)
    {
        // Lower case too: the method arrives off the wire, and a client that
        // sends "post" must not walk past the check by shouting quietly.
        Assert.True(Csrf.Guards(method));
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public void LeavesTheOnesAngularDoesNotSign(string method)
    {
        // GET and HEAD because the interceptor skips them; OPTIONS because
        // refusing a preflight refuses the request that would have followed.
        Assert.False(Csrf.Guards(method));
    }

    [Fact]
    public void AcceptsAHeaderThatEqualsTheCookie()
    {
        var token = Csrf.NewToken();

        Assert.True(Csrf.Match(token, token));
    }

    [Fact]
    public void RefusesAHeaderThatDoesNot()
    {
        Assert.False(Csrf.Match(Csrf.NewToken(), Csrf.NewToken()));
    }

    [Theory]
    [InlineData(null, "abc")]
    [InlineData("abc", null)]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("abc", "")]
    public void RefusesAMissingHalfWhicheverHalfItIs(string? cookie, string? header)
    {
        // Every one of these is a real request shape: no cookie yet, a script
        // that forgot the header, a cross-site post that could send neither.
        Assert.False(Csrf.Match(cookie, header));
    }

    [Fact]
    public void RefusesAPrefixOfTheRightToken()
    {
        // The length check is what makes the fixed-time compare safe to reach:
        // FixedTimeEquals throws nothing on unequal lengths, it just returns
        // false, but only if the lengths are checked before it is called.
        var token = Csrf.NewToken();

        Assert.False(Csrf.Match(token, token[..^1]));
        Assert.False(Csrf.Match(token, token + "x"));
    }

    [Fact]
    public void IsUrlSafeSoItSurvivesACookieAndAHeaderUnencoded()
    {
        for (var i = 0; i < 50; i++)
        {
            Assert.Matches("^[A-Za-z0-9_-]+$", Csrf.NewToken());
        }
    }

    [Fact]
    public void IsLongEnoughNotToBeGuessed()
    {
        // 32 bytes, base64url, no padding.
        Assert.Equal(43, Csrf.NewToken().Length);
    }

    [Fact]
    public void DoesNotRepeat()
    {
        var seen = new HashSet<string>();
        for (var i = 0; i < 200; i++) seen.Add(Csrf.NewToken());

        Assert.Equal(200, seen.Count);
    }
}
