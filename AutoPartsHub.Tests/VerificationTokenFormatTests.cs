using AutoPartsHub.Api.Auth;

namespace AutoPartsHub.Tests;

/// <summary>
/// The three things about a token that are a contract with the other API.
/// </summary>
/// <remarks>
/// Both APIs read one <c>VerificationToken</c> table, and only the HASH is
/// stored — so a link mailed by one has to hash to something the other wrote.
/// A customer does not know which process sent their reset email and cannot be
/// asked to; the link is in their inbox either way.
///
/// That makes the encoding a contract rather than a preference, and it is the
/// kind that fails silently: a token hashed with uppercase hex, or generated
/// with standard base64 instead of base64url, produces "that reset link is not
/// valid" on a link that is perfectly valid. Nothing logs, nothing throws, and
/// the person is locked out of their account.
///
/// The expected values below were produced by the other API's own crypto, not
/// by reading this code back to itself:
///
/// <code>
/// node -e "console.log(require('node:crypto').createHash('sha256').update('hello').digest('hex'))"
/// node -e "console.log(require('node:crypto').randomBytes(32).toString('base64url').length)"
/// </code>
///
/// No database — this is bytes.
/// </remarks>
public class VerificationTokenFormatTests
{
    /// <remarks>
    /// Lowercase, because Node's <c>digest('hex')</c> is lowercase and the
    /// column holds whatever was written first. .NET's older
    /// <c>Convert.ToHexString</c> is uppercase, which is exactly the slip this
    /// is here to catch.
    /// </remarks>
    [Theory]
    [InlineData("hello", "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824")]
    [InlineData("", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    public void TheHashIsTheOneTheOtherApiWrites(string token, string expected) =>
        Assert.Equal(expected, VerificationTokens.Hash(token));

    [Fact]
    public void TheHashIsLowercaseHex() =>
        Assert.Matches("^[0-9a-f]{64}$", VerificationTokens.Hash("anything"));

    /// <summary>
    /// A token is 32 bytes as base64url, unpadded — 43 characters.
    /// </summary>
    /// <remarks>
    /// Url-safe matters because the token spends its life as a query
    /// parameter. Standard base64 would carry <c>+</c> and <c>/</c>, which
    /// survive being pasted into a browser and do not survive every mail
    /// client that rewrites links — and padding <c>=</c> is the character most
    /// likely to be dropped by whatever is shortening it.
    ///
    /// Asserted over many draws rather than one, because these properties are
    /// about an alphabet: a single token that happened to contain no <c>+</c>
    /// proves nothing.
    /// </remarks>
    [Fact]
    public void ATokenIsUrlSafeAndUnpadded()
    {
        for (var i = 0; i < 200; i++)
        {
            var token = NewToken();

            Assert.Equal(43, token.Length);
            Assert.Matches("^[A-Za-z0-9_-]{43}$", token);
            Assert.Equal(token, Uri.EscapeDataString(token));
        }
    }

    /// <remarks>
    /// Two in a row being equal would mean the generator is not random, which
    /// is the failure that turns every reset link into everybody's reset link.
    /// </remarks>
    [Fact]
    public void TokensDoNotRepeat()
    {
        var drawn = new HashSet<string>();

        for (var i = 0; i < 500; i++) Assert.True(drawn.Add(NewToken()));
    }

    /// <summary>
    /// A reset token is short-lived and a confirmation token is not.
    /// </summary>
    /// <remarks>
    /// A reset token is the whole account in one string. A confirmation token
    /// proves an address is reachable and grants nothing, so it can survive a
    /// night's sleep — expiring it in thirty minutes would mostly generate
    /// second attempts. The numbers are the other API's, and the emails quote
    /// them, so they are asserted rather than left to be noticed.
    /// </remarks>
    [Fact]
    public void TheLifetimesAreTheOnesTheEmailsPromise()
    {
        Assert.Equal(30, VerificationTokens.LifetimeMinutes(VerificationTokens.Purposes.PasswordReset));
        Assert.Equal(24 * 60, VerificationTokens.LifetimeMinutes(VerificationTokens.Purposes.EmailConfirmation));
    }

    /// <remarks>
    /// The purpose is written into a column and compared on the way back out,
    /// so a renamed constant is a token nobody can redeem. Spelled out here
    /// rather than compared to itself.
    /// </remarks>
    [Fact]
    public void ThePurposesAreTheStringsTheTableHolds()
    {
        Assert.Equal("password_reset", VerificationTokens.Purposes.PasswordReset);
        Assert.Equal("email_confirmation", VerificationTokens.Purposes.EmailConfirmation);
    }

    [Fact]
    public void APurposeNothingRecognisesIsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => VerificationTokens.LifetimeMinutes("something_else"));

    private static string NewToken() => VerificationTokens.Generate();
}
