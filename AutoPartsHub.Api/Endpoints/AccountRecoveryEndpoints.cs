using AutoPartsHub.Api.Auth;
using AutoPartsHub.Api.Mail;
using AutoPartsHub.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsHub.Api.Endpoints;

/// <summary>
/// Getting back into an account, and proving an address is reachable (T-196).
/// </summary>
/// <remarks>
/// Four routes the storefront has been calling since before this port started
/// and nothing here answered — a forgotten password, a reset, a confirmation
/// link, and the banner's "send it again". They were the whole of the gap this
/// repository's CONTRACTS.md lists under "what the storefront asks for and
/// nothing answers".
///
/// THE UNIFORM ANSWER
/// ------------------
/// Three of the four say the same thing whatever happened. Whether an address
/// has an account here is not something a stranger gets to find out by typing
/// it into a box: the moment "forgot password" answers differently for a known
/// address, it is a tool for sorting a leaked address list into customers and
/// strangers. So no such account, an account with no password, the rate limit
/// and a missing mail transport all end in one sentence, and the differences
/// go to the log where they belong.
///
/// Reset is the exception, and deliberately. By the time somebody is there
/// they are holding a token, and a token is not a guess — "that link has
/// expired, ask for another" tells them nothing they could not work out from
/// it not working, and it is the difference between finishing and giving up.
/// </remarks>
public static class AccountRecoveryEndpoints
{
    public static void MapAccountRecoveryEndpoints(this IEndpointRouteBuilder app)
    {
        // POST /api/auth/password/forgot { email }
        app.MapPost("/api/auth/password/forgot", async (
            ForgotRequest body, VerificationTokens verification, IEmailSender mail,
            ILoggerFactory logs, CancellationToken ct) =>
        {
            var email = (body.Email ?? "").Trim().ToLowerInvariant();

            // The one thing it will say no to. An empty box is the caller's own
            // mistake rather than a question about somebody else's account, so
            // answering it reveals nothing.
            if (email.Length == 0)
            {
                return Results.BadRequest(new { error = RecoveryMessages.NoAddressGiven });
            }

            var account = await verification.ForRecoveryAsync(email, ct);

            // An account created by an admin has no password to reset. Sending
            // a reset link would let anyone who can read that mailbox set the
            // first one — which may be the right feature one day, but it is a
            // different one, and it is not being added here by accident.
            if (account is { HasLogin: true })
            {
                var issued = await verification.IssueAsync(
                    account.Id, VerificationTokens.Purposes.PasswordReset, ct);

                if (issued is not null)
                {
                    var message = RecoveryMail.PasswordReset(
                        BusinessMail.Context(), account.Email, account.Name, issued.Token);

                    // Never throws — see IEmailSender. A transport that is not
                    // configured becomes a named line in the log, and the
                    // caller gets the sentence below either way.
                    await mail.SendAsync(message.To, message.Subject, message.Body, ct);
                }
                else
                {
                    logs.CreateLogger(typeof(AccountRecoveryEndpoints)).LogInformation(
                        "Password reset asked for more than the hourly limit on one account; "
                        + "no mail sent, and the caller was told the usual sentence.");
                }
            }

            return Results.Ok(new { ok = true, message = RecoveryMessages.LinkOnItsWay });
        });

        // POST /api/auth/password/reset { token, password }
        //
        // It does NOT sign the account in afterwards. Resetting a password is
        // something you may be doing from a machine you do not own, at the end
        // of a link in an email, and turning that into a session is a decision
        // worth making on purpose rather than as a convenience.
        app.MapPost("/api/auth/password/reset", async (
            ResetRequest body, VerificationTokens verification, AutoPartsHub.Api.Data.AutoPartsContext db,
            CancellationToken ct) =>
        {
            var token = (body.Token ?? "").Trim();
            var password = body.Password ?? "";

            if (token.Length == 0)
            {
                return Results.BadRequest(new { error = RecoveryMessages.MissingCode });
            }

            // Checked before the token is looked up, so a too-short password
            // does not burn the token and leave the person needing a second
            // email.
            if (password.Length < RecoveryMessages.ShortestPassword)
            {
                return Results.BadRequest(new { error = RecoveryMessages.PasswordTooShort });
            }

            // Hashed outside the transaction: bcrypt at ten rounds takes long
            // enough that doing it while holding a row lock would hold the lock
            // for no reason. Ten because that is the cost the accounts already
            // in the table were hashed at, and the two APIs share one login.
            var passwordHash = BCrypt.Net.BCrypt.HashPassword(password, 10);

            var redeemed = await verification.RedeemAsync(
                token, VerificationTokens.Purposes.PasswordReset,
                async (clientId, inner) =>
                {
                    await db.Database.ExecuteSqlAsync($"""
                        UPDATE "Client" SET "passwordHash" = {passwordHash} WHERE "id" = {clientId}
                        """, inner);

                    // Proving you can read the mailbox is exactly what
                    // confirmation asks, and the reset link went to that
                    // mailbox. Making somebody confirm separately afterwards
                    // would be asking a question already answered.
                    await verification.MarkEmailConfirmedAsync(clientId, inner);
                },
                ct);

            if (!redeemed.Ok)
            {
                // Every one of these says "link", which the reset page reads to
                // decide whether to offer a fresh one. See RecoveryMessages.
                return Results.BadRequest(new { error = RecoveryMessages.ResetRefused(redeemed.Refusal) });
            }

            return Results.Ok(new { ok = true, message = RecoveryMessages.PasswordChanged });
        });

        // POST /api/auth/email/confirm { token }
        //
        // Records that the address is reachable. Nothing is gated on it yet,
        // and that is deliberate rather than unfinished: gating anything on
        // confirmation while there is no email transport would lock people out
        // of a shop to add a feature that cannot deliver its own emails.
        app.MapPost("/api/auth/email/confirm", async (
            ConfirmRequest body, VerificationTokens verification, CancellationToken ct) =>
        {
            var token = (body.Token ?? "").Trim();
            if (token.Length == 0)
            {
                return Results.BadRequest(new { error = RecoveryMessages.MissingCode });
            }

            var redeemed = await verification.RedeemAsync(
                token, VerificationTokens.Purposes.EmailConfirmation,
                verification.MarkEmailConfirmedAsync, ct);

            // Already used is not a failure worth alarming anybody with. The
            // address is confirmed, which is what they wanted, and a mail
            // client that prefetches links reaches this on its own before the
            // person ever clicks — so it answers like the success it
            // effectively is, rather than telling somebody their working link
            // is broken.
            if (redeemed.Refusal == VerificationTokens.Refusal.Used)
            {
                return Results.Ok(new { ok = true, message = RecoveryMessages.AlreadyConfirmed });
            }

            if (!redeemed.Ok)
            {
                return Results.BadRequest(new
                {
                    error = RecoveryMessages.ConfirmationRefused(redeemed.Refusal),
                });
            }

            return Results.Ok(new
            {
                ok = true, email = redeemed.Email, message = RecoveryMessages.AddressConfirmed,
            });
        });

        // POST /api/auth/email/resend
        //
        // Signed in only, and that is what keeps it simple. Asked by address
        // instead, it would be a second endpoint that has to lie uniformly
        // about whether an account exists; asked by session, the caller has
        // already proved who they are and there is nothing left to leak.
        //
        // It works because confirmation gates nothing — an unconfirmed account
        // can still sign in, which is exactly how somebody reaches this.
        app.MapPost("/api/auth/email/resend", async (
            HttpContext http, SessionTokens tokens, VerificationTokens verification,
            IEmailSender mail, CancellationToken ct) =>
        {
            var session = tokens.Decode(http.Request.Cookies[SessionTokens.CookieName]);
            if (session is null) return Results.Json(new { error = RecoveryMessages.NotSignedIn }, statusCode: 401);

            // The address comes from the account the session names, never from
            // the request. A resend that accepted an address would be a way to
            // post mail to strangers from this domain.
            //
            // Read fresh rather than from the cookie: a session issued this
            // morning predates a confirmation made this afternoon, and
            // resending to somebody already confirmed is a wasted email and a
            // confusing one.
            var account = await verification.ByIdAsync(session.UserId, ct);
            if (account is null) return Results.Json(new { error = RecoveryMessages.NotSignedIn }, statusCode: 401);

            if (account.EmailConfirmedAt is not null)
            {
                return Results.Ok(new
                {
                    ok = true,
                    alreadyConfirmed = true,
                    message = RecoveryMessages.AlreadyConfirmed,
                });
            }

            var issued = await verification.IssueAsync(
                account.Id, VerificationTokens.Purposes.EmailConfirmation, ct);

            if (issued is not null)
            {
                var message = RecoveryMail.EmailConfirmation(
                    BusinessMail.Context(), account.Email, account.Name, issued.Token);

                await mail.SendAsync(message.To, message.Subject, message.Body, ct);
            }

            // The same answer whether it sent, was rate limited, or found no
            // transport. Not to hide the account — the caller owns it — but
            // because none of those is something they could act on differently.
            return Results.Ok(new { ok = true, message = RecoveryMessages.CheckYourInbox });
        });
    }
}

public record ForgotRequest(string? Email);

public record ResetRequest(string? Token, string? Password);

public record ConfirmRequest(string? Token);
