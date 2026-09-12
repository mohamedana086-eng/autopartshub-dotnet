using AutoPartsHub.Api.Auth;
using AutoPartsHub.Api.Mail;

namespace AutoPartsHub.Tests;

/// <summary>
/// The sentences account recovery sends, where the wording is load-bearing.
/// </summary>
/// <remarks>
/// Most error text is for a person to read and can be reworded freely. Three
/// of these cannot, and each breaks silently if it is:
///
/// - the reset refusals, which the storefront tests for the word "link" to
///   decide whether to offer a fresh one;
/// - the forgot answer, which has to be the same sentence for every outcome or
///   it becomes a way to ask whether an address has an account here;
/// - the links in the emails, which are the only copy of a token that exists.
///
/// No database — these are strings and a URL.
/// </remarks>
public class RecoveryMessageTests
{
    /// <summary>
    /// Every refusal about the token says "link".
    /// </summary>
    /// <remarks>
    /// <c>pages/reset-password.page.ts</c> runs <c>/link/i</c> over the error
    /// and shows "ask for a new one" when it matches. Reworded to "that code
    /// has expired", the page still shows the error, still looks right, and no
    /// longer offers the one button that gets the person out of it.
    /// </remarks>
    [Theory]
    [InlineData(VerificationTokens.Refusal.Invalid)]
    [InlineData(VerificationTokens.Refusal.Expired)]
    [InlineData(VerificationTokens.Refusal.Used)]
    public void AReusableRefusalSaysLink(VerificationTokens.Refusal why) =>
        Assert.Matches("link", RecoveryMessages.ResetRefused(why));

    [Theory]
    [InlineData(VerificationTokens.Refusal.Invalid)]
    [InlineData(VerificationTokens.Refusal.Expired)]
    public void AndSoDoesTheConfirmationVersion(VerificationTokens.Refusal why) =>
        Assert.Matches("link", RecoveryMessages.ConfirmationRefused(why));

    /// <summary>
    /// And the one refusal that is not about the link does not say it.
    /// </summary>
    /// <remarks>
    /// The other half of the same contract, and the half that would otherwise
    /// go unnoticed: a too-short password offering "ask for a new link" sends
    /// somebody to their inbox to fix something they could have fixed in the
    /// box in front of them — and burns one of five hourly emails doing it.
    /// </remarks>
    [Fact]
    public void ARefusalAboutThePasswordDoesNot()
    {
        Assert.DoesNotMatch("link", RecoveryMessages.PasswordTooShort);
        Assert.Contains("6", RecoveryMessages.PasswordTooShort);
    }

    /// <remarks>
    /// Registration refuses the same length. Somewhere between the two would
    /// mean an account that can be created and then cannot have its own
    /// password set again.
    /// </remarks>
    [Fact]
    public void TheShortestPasswordIsTheOneRegistrationAsksFor() =>
        Assert.Equal(6, RecoveryMessages.ShortestPassword);

    /// <remarks>
    /// It has to be able to be true when there is no account, so it cannot
    /// promise that anything was sent.
    /// </remarks>
    [Fact]
    public void TheForgotAnswerDoesNotSayWhetherTheAccountExists()
    {
        Assert.Contains("If that address has an account", RecoveryMessages.LinkOnItsWay);
        Assert.DoesNotContain("we have sent", RecoveryMessages.LinkOnItsWay);
    }

    // ------------------------------------------------------------- the links

    private static readonly MailContext Site = new("https://parts.example");

    /// <summary>
    /// The link points at the page the storefront actually serves.
    /// </summary>
    /// <remarks>
    /// Absolute, because there is no page for it to be relative to once it is
    /// in an inbox. The paths are the storefront's routes — <c>/reset-password</c>
    /// and <c>/confirm-email</c> — and a token on the wrong one is a live token
    /// nobody can spend.
    /// </remarks>
    [Fact]
    public void TheResetLinkCarriesItsTokenToTheResetPage()
    {
        var mail = RecoveryMail.PasswordReset(Site, "someone@example.test", "Someone", "tok-123");

        Assert.Contains("https://parts.example/reset-password?token=tok-123", mail.Body);
        Assert.Equal("someone@example.test", mail.To);
        Assert.Contains("Someone", mail.Body);
    }

    [Fact]
    public void TheConfirmationLinkCarriesItsTokenToTheConfirmPage()
    {
        var mail = RecoveryMail.EmailConfirmation(Site, "someone@example.test", "Someone", "tok-123");

        Assert.Contains("https://parts.example/confirm-email?token=tok-123", mail.Body);
    }

    /// <remarks>
    /// base64url happens to contain nothing that needs escaping, which is not
    /// a licence to skip escaping it — a change to how tokens are generated
    /// should not be able to break every link at once and silently.
    /// </remarks>
    [Fact]
    public void ATokenIsEscapedOnItsWayIntoTheLink()
    {
        var mail = RecoveryMail.PasswordReset(Site, "a@b.test", "A", "a+b/c=d&e");

        Assert.Contains("token=a%2Bb%2Fc%3Dd%26e", mail.Body);
        Assert.DoesNotContain("&e", mail.Body);
    }

    /// <summary>
    /// The emails quote the lifetimes, and the lifetimes are read rather than
    /// typed.
    /// </summary>
    /// <remarks>
    /// A message promising thirty minutes on a token that lasts ten is a
    /// support conversation nobody can resolve, because both parties are
    /// right.
    /// </remarks>
    [Fact]
    public void TheEmailsPromiseTheLifetimesTheTokensHave()
    {
        Assert.Contains(
            "30 minutes",
            RecoveryMail.PasswordReset(Site, "a@b.test", "A", "t").Body);

        Assert.Contains(
            "24 hours",
            RecoveryMail.EmailConfirmation(Site, "a@b.test", "A", "t").Body);
    }

    /// <remarks>
    /// A reset email lands in the inbox of somebody who may not have asked for
    /// it, and telling them nothing has changed is the difference between a
    /// shrug and a support ticket.
    /// </remarks>
    [Fact]
    public void TheResetEmailTellsSomebodyWhoDidNotAskThatNothingHappened() =>
        Assert.Contains(
            "If this was not you, nothing has changed",
            RecoveryMail.PasswordReset(Site, "a@b.test", "A", "t").Body);
}
