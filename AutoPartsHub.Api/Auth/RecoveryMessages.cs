namespace AutoPartsHub.Api.Auth;

/// <summary>
/// What the recovery endpoints say, and why the wording is load-bearing.
/// </summary>
/// <remarks>
/// Named rather than written inline, because two of these sentences are read
/// by a machine as well as by a person.
///
/// THE WORD "LINK"
/// ---------------
/// The reset page decides whether to offer a fresh link by testing the error
/// text for it — <c>/link/i</c>, in <c>pages/reset-password.page.ts</c>. That
/// is a contract with a regular expression in another repository, and it is
/// the kind that breaks quietly: reword "that reset link has expired" to "that
/// code has expired" and the page still shows the error, still looks correct,
/// and no longer offers the one button that gets the person out of it.
///
/// So every refusal about the TOKEN says "link", and the refusal about the
/// password does not. <c>RecoveryMessageTests</c> holds both halves.
///
/// THE ONE THAT NEVER VARIES
/// -------------------------
/// <see cref="LinkOnItsWay"/> is the answer to "forgot password" whatever
/// happened — no such account, an account with no password, the rate limit, a
/// mail transport that is not configured. Whether an address has an account
/// here is not something a stranger gets to find out by typing it into a box.
/// </remarks>
public static class RecoveryMessages
{
    // --------------------------------------------------------- the reset flow

    public const string LinkOnItsWay = "If that address has an account, a reset link is on its way.";

    public const string NoAddressGiven = "Enter the email address on the account.";

    public const string MissingCode = "That link is missing its code.";

    /// <summary>
    /// The shortest password a reset will set.
    /// </summary>
    /// <remarks>
    /// It matches registration. Somewhere between the two would mean an
    /// account that can be created and then cannot have its own password set
    /// again, which is the sort of thing found by the person it happens to.
    /// </remarks>
    public const int ShortestPassword = 6;

    /// <remarks>
    /// Built from the number rather than repeating it, so the sentence cannot
    /// promise six while the check enforces eight.
    /// </remarks>
    public static readonly string PasswordTooShort =
        $"Password must be at least {ShortestPassword} characters.";

    public const string PasswordChanged =
        "Your password has been changed. You can sign in with it now.";

    public static string ResetRefused(VerificationTokens.Refusal why) => why switch
    {
        VerificationTokens.Refusal.Expired => "That reset link has expired. Ask for a new one.",
        VerificationTokens.Refusal.Used =>
            "That reset link has already been used. Ask for a new one if you still need it.",
        _ => "That reset link is not valid. Ask for a new one.",
    };

    // -------------------------------------------------- the confirmation flow

    public const string AddressConfirmed = "Your address is confirmed.";

    public const string AlreadyConfirmed = "That address is already confirmed.";

    public const string CheckYourInbox = "Check your inbox for the confirmation link.";

    public const string NotSignedIn = "Not signed in.";

    public static string ConfirmationRefused(VerificationTokens.Refusal why) => why switch
    {
        VerificationTokens.Refusal.Expired => "That confirmation link has expired. Ask for a new one.",
        _ => "That confirmation link is not valid. Ask for a new one.",
    };
}
