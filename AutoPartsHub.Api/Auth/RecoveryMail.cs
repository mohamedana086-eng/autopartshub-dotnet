using AutoPartsHub.Api.Mail;

namespace AutoPartsHub.Api.Auth;

/// <summary>
/// The two messages that carry a one-time link.
/// </summary>
/// <remarks>
/// Composed in one place because three endpoints send them — register and
/// resend both send the confirmation, and forgot sends the reset — and the
/// link is the part that must not drift. A message that named the wrong page
/// or dropped the encoding would produce a token that exists, is live, and
/// cannot be redeemed by the person holding it.
///
/// Plain text, like everything else this application sends: there is no
/// transport that renders HTML yet (NOTIF-02).
/// </remarks>
public static class RecoveryMail
{
    /// <summary>
    /// A link carrying a token, absolute.
    /// </summary>
    /// <remarks>
    /// Escaped, because the token is base64url and that alphabet is url-safe
    /// only in the sense that it contains nothing needing escaping — it is not
    /// a licence to skip escaping, and a future change to how tokens are
    /// generated should not be able to break every link silently.
    /// </remarks>
    private static string Link(MailContext ctx, string page, string token) =>
        $"{ctx.SiteUrl}/{page}?token={Uri.EscapeDataString(token)}";

    /// <remarks>
    /// It says the link works once and says how long it lasts, because both are
    /// true and both are things somebody will otherwise write in to ask. The
    /// lifetime is read from <see cref="VerificationTokens.LifetimeMinutes"/>
    /// rather than typed, so a message cannot promise thirty minutes on a token
    /// that lasts ten.
    /// </remarks>
    public static Email PasswordReset(MailContext ctx, string to, string name, string token) =>
        new(to,
            "Reset your AutoParts Hub password",
            $"Hello {name},\n\n"
            + "Somebody asked to reset the password on this account. To set a new one, open:\n\n"
            + $"{Link(ctx, "reset-password", token)}\n\n"
            + $"The link works once and stops working in "
            + $"{VerificationTokens.LifetimeMinutes(VerificationTokens.Purposes.PasswordReset)} minutes.\n\n"
            + "If this was not you, nothing has changed and you can ignore this message.\n");

    public static Email EmailConfirmation(MailContext ctx, string to, string name, string token) =>
        new(to,
            "Confirm your AutoParts Hub address",
            $"Hello {name},\n\n"
            + "Please confirm this address by opening:\n\n"
            + $"{Link(ctx, "confirm-email", token)}\n\n"
            + "The link works once and stops working in "
            + $"{VerificationTokens.LifetimeMinutes(VerificationTokens.Purposes.EmailConfirmation) / 60} hours.\n");
}
