using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutoPartsHub.Api.Auth;

namespace AutoPartsHub.Tests;

/// <summary>
/// Session tokens.
/// </summary>
/// <remarks>
/// There is no auth library here: a session is a JSON payload and an HMAC of
/// it, and <c>Decode</c> is the only thing standing between a cookie and being
/// whoever it claims. Every test below is a forgery attempt that has to fail,
/// plus the one shape that has to succeed.
///
/// The tokens are built here by hand rather than by calling <c>Encode</c>, so
/// the wire format is asserted independently instead of by round-tripping the
/// encoder against itself. That also makes these tests the written-down proof
/// that the format matches the Node original — the two APIs share one cookie.
/// </remarks>
public class SessionTokenTests
{
    private const string Secret = "test-secret-not-the-placeholder";

    private static readonly SessionTokens Tokens = new(Secret);

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Sign(string data, string key = Secret) =>
        Base64Url(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(data)));

    private static string Body(object payload) =>
        Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload));

    private static long InAMinute => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 60_000;

    /// <summary>The camelCase shape the cookie actually carries.</summary>
    private static object Payload(string role = "ADMIN", long? exp = null) => new
    {
        userId = "client-1",
        role,
        categoryId = "cat-1",
        name = "Mohamed",
        exp = exp ?? InAMinute,
    };

    private static string Token(object payload, bool tamperSignature = false)
    {
        var body = Body(payload);
        return $"{body}.{Sign(tamperSignature ? body + "x" : body)}";
    }

    [Fact]
    public void AcceptsAProperlySignedUnexpiredToken()
    {
        var session = Tokens.Decode(Token(Payload()));

        Assert.NotNull(session);
        Assert.Equal("client-1", session.UserId);
        Assert.Equal("ADMIN", session.Role);
        Assert.Equal("cat-1", session.CategoryId);
        Assert.Equal("Mohamed", session.Name);
    }

    [Fact]
    public void RejectsAPayloadEditedAfterSigning()
    {
        // The forgery that matters: take a real RETAIL cookie, rewrite the role.
        var real = Token(Payload(role: "RETAIL"));
        var signature = real[(real.IndexOf('.') + 1)..];
        var forged = Body(Payload(role: "ADMIN"));

        Assert.Null(Tokens.Decode($"{forged}.{signature}"));
    }

    [Fact]
    public void RejectsASignatureNotProducedFromThisPayload()
    {
        Assert.Null(Tokens.Decode(Token(Payload(), tamperSignature: true)));
    }

    [Fact]
    public void RejectsATokenSignedWithADifferentSecret()
    {
        var body = Body(Payload());
        var wrong = Sign(body, "some-other-secret");

        Assert.Null(Tokens.Decode($"{body}.{wrong}"));
    }

    [Fact]
    public void RejectsAnExpiredTokenEvenThoughTheSignatureIsGood()
    {
        var expired = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1;

        Assert.Null(Tokens.Decode(Token(Payload(exp: expired))));
    }

    [Fact]
    public void RejectsAnUnsignedPayload()
    {
        var body = Body(Payload());

        Assert.Null(Tokens.Decode(body));
        Assert.Null(Tokens.Decode($"{body}."));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("...")]
    public void RejectsNothingAtAllRatherThanThrowing(string? token)
    {
        Assert.Null(Tokens.Decode(token));
    }

    [Fact]
    public void RejectsACorrectlySignedBodyThatIsNotJson()
    {
        // Signed, so it gets past the HMAC and has to be caught by the parse.
        var body = Base64Url(Encoding.UTF8.GetBytes("not json at all"));

        Assert.Null(Tokens.Decode($"{body}.{Sign(body)}"));
    }

    [Fact]
    public void RejectsABodyThatIsNotBase64AtAll()
    {
        // Not reachable through Encode, but reachable from a browser. The
        // decoder has to answer null rather than let a FormatException out.
        const string body = "!!!not-base64!!!";

        Assert.Null(Tokens.Decode($"{body}.{Sign(body)}"));
    }

    [Fact]
    public void RoundTripsItsOwnTokens()
    {
        var issued = Tokens.Encode(new SessionPayload("c-9", "SALES", null, "Sara", InAMinute));
        var session = Tokens.Decode(issued);

        Assert.NotNull(session);
        Assert.Equal("c-9", session.UserId);
        Assert.Equal("SALES", session.Role);
        Assert.Null(session.CategoryId);
    }

    [Fact]
    public void WritesTheSameWireFormatTheOtherApiReads()
    {
        // Two halves separated by a dot, the first being base64url JSON with
        // camelCase keys. Asserted rather than assumed, because a change here
        // signs every customer out of the other API without erroring.
        var issued = Tokens.Encode(new SessionPayload("c-1", "ADMIN", "cat-1", "Mohamed", InAMinute));
        var parts = issued.Split('.');

        Assert.Equal(2, parts.Length);
        Assert.DoesNotContain('=', issued);
        Assert.DoesNotContain('+', issued);
        Assert.DoesNotContain('/', issued);

        var json = Encoding.UTF8.GetString(
            Convert.FromBase64String(parts[0].Replace('-', '+').Replace('_', '/')
                + new string('=', (4 - parts[0].Length % 4) % 4)));

        Assert.Contains("\"userId\"", json);
        Assert.Contains("\"categoryId\"", json);
        Assert.Contains("\"exp\"", json);
        Assert.Equal(parts[1], Sign(parts[0]));
    }

    [Fact]
    public void SignsWithTheSecretItWasGivenAndNotAGlobalOne()
    {
        var other = new SessionTokens("a-different-secret-entirely");
        var issued = Tokens.Encode(new SessionPayload("c-1", "ADMIN", null, "M", InAMinute));

        Assert.Null(other.Decode(issued));
    }
}
