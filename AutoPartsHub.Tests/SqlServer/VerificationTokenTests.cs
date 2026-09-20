using AutoPartsHub.Api.Auth;
using AutoPartsHub.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsHub.Tests.SqlServer;

/// <summary>
/// One-time tokens, against a real engine (T-196).
/// </summary>
/// <remarks>
/// Every interesting property here is about a race, a clock or a rollback, and
/// none of the three can be asserted without a database. "The token is spent
/// exactly once" is a statement about row locking; "expired" is a statement
/// about the server's clock and not the test process's; "the rate limit is not
/// a lockout" is a statement about what a transaction left behind when it
/// declined to commit.
///
/// These rows are made and removed by each test rather than seeded, because
/// several of them count what an account has been issued in the last hour —
/// and a fixture shared with the test beside it would make that count depend
/// on running order.
/// </remarks>
[Collection(CatalogueCollection.Name)]
public class VerificationTokenTests(Catalogue catalogue)
{
    private AutoPartsContext Db => catalogue.Db;

    private VerificationTokens Tokens => new(Db, new UnitOfWork(Db));

    private const string Reset = VerificationTokens.Purposes.PasswordReset;
    private const string Confirm = VerificationTokens.Purposes.EmailConfirmation;

    /// <summary>An account of this test's own, removed on the way out.</summary>
    private async Task WithAccount(Func<string, string, Task> body)
    {
        var id = $"cli-probe-{Guid.NewGuid():n}";
        var email = $"{id}@example.test";

        await Db.Database.ExecuteSqlAsync($"""
            INSERT INTO "Client" ("id", "name", "email", "passwordHash", "discountPercent")
            VALUES ({id}, 'Probe Account', {email}, 'not-a-real-hash', 0)
            """);
        try
        {
            await body(id, email);
        }
        finally
        {
            await Db.Database.ExecuteSqlAsync($"""DELETE FROM "VerificationToken" WHERE "clientId" = {id}""");
            await Db.Database.ExecuteSqlAsync($"""DELETE FROM "Client" WHERE "id" = {id}""");
        }
    }

    // ------------------------------------------------------------ spending

    [SqlServerFact]
    public Task AnIssuedTokenCanBeSpentOnce() => WithAccount(async (id, email) =>
    {
        var issued = await Tokens.IssueAsync(id, Reset);
        Assert.NotNull(issued);

        var spent = 0;
        var first = await Tokens.RedeemAsync(issued.Token, Reset, (_, _) =>
        {
            spent++;
            return Task.CompletedTask;
        });

        Assert.True(first.Ok);
        Assert.Equal(id, first.ClientId);
        Assert.Equal(email, first.Email);
        Assert.Equal(1, spent);

        // And not twice. A reset link opened again — by the person, or by a
        // mail scanner following it before they ever click — must not set a
        // second password.
        var second = await Tokens.RedeemAsync(issued.Token, Reset, (_, _) =>
        {
            spent++;
            return Task.CompletedTask;
        });

        Assert.Equal(VerificationTokens.Refusal.Used, second.Refusal);
        Assert.Equal(1, spent);
    });

    [SqlServerTheory]
    [InlineData("")]
    [InlineData("not-a-token")]
    public async Task ATokenNothingIssuedIsInvalid(string token) =>
        Assert.Equal(
            VerificationTokens.Refusal.Invalid,
            (await Tokens.RedeemAsync(token, Reset, (_, _) => Task.CompletedTask)).Refusal);

    /// <summary>
    /// A confirmation token is not a password reset.
    /// </summary>
    /// <remarks>
    /// They share a table, and a confirmation token is the longer-lived of the
    /// two and the one sent on every registration. If the purpose were not part
    /// of the lookup, the cheap token would open the expensive door.
    /// </remarks>
    [SqlServerFact]
    public Task ATokenIssuedForOnePurposeDoesNotOpenTheOther() => WithAccount(async (id, _) =>
    {
        var issued = await Tokens.IssueAsync(id, Confirm);
        Assert.NotNull(issued);

        var wrong = await Tokens.RedeemAsync(issued.Token, Reset, (_, _) => Task.CompletedTask);

        Assert.Equal(VerificationTokens.Refusal.Invalid, wrong.Refusal);

        // ... and it still works for what it was for.
        Assert.True((await Tokens.RedeemAsync(issued.Token, Confirm, (_, _) => Task.CompletedTask)).Ok);
    });

    /// <remarks>
    /// Expiry is decided by the server's clock, in the statement — the reason
    /// this cannot be a unit test. A check done in C# would be comparing a
    /// timestamp the database wrote against a clock that is not the one that
    /// wrote it.
    /// </remarks>
    [SqlServerFact]
    public Task ATokenPastItsExpiryIsRefused() => WithAccount(async (id, _) =>
    {
        var issued = await Tokens.IssueAsync(id, Reset);
        Assert.NotNull(issued);

        await Db.Database.ExecuteSqlAsync($"""
            UPDATE "VerificationToken" SET "expiresAt" = DATEADD(minute, -1, SYSUTCDATETIME())
            WHERE "tokenHash" = {VerificationTokens.Hash(issued.Token)}
            """);

        var refused = await Tokens.RedeemAsync(issued.Token, Reset, (_, _) => Task.CompletedTask);

        Assert.Equal(VerificationTokens.Refusal.Expired, refused.Refusal);
    });

    // --------------------------------------------------------- superseding

    /// <summary>
    /// Asking again stands the previous one down.
    /// </summary>
    /// <remarks>
    /// This is what "resend" needs. Without it every press leaves another live
    /// token, and the oldest — the one most likely to have been seen by
    /// whoever should not have — keeps working for its full lifetime.
    /// </remarks>
    [SqlServerFact]
    public Task IssuingAgainRetiresTheTokenBefore() => WithAccount(async (id, _) =>
    {
        var first = await Tokens.IssueAsync(id, Confirm);
        var second = await Tokens.IssueAsync(id, Confirm);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first.Token, second.Token);

        Assert.Equal(
            VerificationTokens.Refusal.Used,
            (await Tokens.RedeemAsync(first.Token, Confirm, (_, _) => Task.CompletedTask)).Refusal);

        Assert.True((await Tokens.RedeemAsync(second.Token, Confirm, (_, _) => Task.CompletedTask)).Ok);
    });

    /// <remarks>
    /// Only its own purpose. A resent confirmation must not quietly cancel a
    /// reset link the same person is part-way through using.
    /// </remarks>
    [SqlServerFact]
    public Task SupersedingStopsAtThePurpose() => WithAccount(async (id, _) =>
    {
        var reset = await Tokens.IssueAsync(id, Reset);
        await Tokens.IssueAsync(id, Confirm);

        Assert.NotNull(reset);
        Assert.True((await Tokens.RedeemAsync(reset.Token, Reset, (_, _) => Task.CompletedTask)).Ok);
    });

    // -------------------------------------------------------- the rate limit

    /// <summary>
    /// Five an hour, and the sixth is refused without saying so.
    /// </summary>
    /// <remarks>
    /// The limit is on how much mail one request can cause to be sent to a
    /// third party, not on logging in: without it, "forgot password" is a
    /// button that mails somebody else's inbox as often as you press it.
    ///
    /// The second half is the part worth having a test for. A refusal that had
    /// already superseded the live token would leave the account with no
    /// working link and no way to get one for an hour — a rate limit that is
    /// really a lockout, and one that only shows up for the person who pressed
    /// the button six times because the first five emails had not arrived yet.
    /// </remarks>
    [SqlServerFact]
    public Task TheSixthRequestInAnHourIsRefusedAndCostsNothing() => WithAccount(async (id, _) =>
    {
        VerificationTokens.Issued? last = null;
        for (var i = 0; i < 5; i++) last = await Tokens.IssueAsync(id, Reset);

        Assert.NotNull(last);
        Assert.Null(await Tokens.IssueAsync(id, Reset));

        // The fifth still works, which is the whole point.
        Assert.True((await Tokens.RedeemAsync(last.Token, Reset, (_, _) => Task.CompletedTask)).Ok);
    });

    /// <remarks>
    /// Per purpose, per account. Confirming an address must not use up the
    /// budget for getting back into it.
    /// </remarks>
    [SqlServerFact]
    public Task TheLimitIsCountedPerPurpose() => WithAccount(async (id, _) =>
    {
        for (var i = 0; i < 5; i++) await Tokens.IssueAsync(id, Reset);

        Assert.Null(await Tokens.IssueAsync(id, Reset));
        Assert.NotNull(await Tokens.IssueAsync(id, Confirm));
    });

    // ------------------------------------------------------- the rollback

    /// <summary>
    /// A token is not spent by work that failed.
    /// </summary>
    /// <remarks>
    /// The case this protects: the password write fails, and the token is
    /// already burnt. The person is then holding a dead link, an unchanged
    /// password and no way to ask for another for an hour — the worst
    /// available outcome, and the one that happens if the token is spent
    /// outside the transaction.
    /// </remarks>
    [SqlServerFact]
    public Task WorkThatThrewLeavesTheTokenUnspent() => WithAccount(async (id, _) =>
    {
        var issued = await Tokens.IssueAsync(id, Reset);
        Assert.NotNull(issued);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Tokens.RedeemAsync(issued.Token, Reset, (_, _) =>
                throw new InvalidOperationException("the write failed")));

        Assert.True((await Tokens.RedeemAsync(issued.Token, Reset, (_, _) => Task.CompletedTask)).Ok);
    });

    /// <summary>
    /// And what the work wrote goes with it.
    /// </summary>
    /// <remarks>
    /// <c>apply</c> runs inside the same transaction as the spend, so a
    /// half-finished one is not left behind either.
    /// </remarks>
    [SqlServerFact]
    public Task WorkThatThrewLeavesNothingBehind() => WithAccount(async (id, _) =>
    {
        var issued = await Tokens.IssueAsync(id, Confirm);
        Assert.NotNull(issued);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Tokens.RedeemAsync(issued.Token, Confirm, async (clientId, ct) =>
            {
                await Tokens.MarkEmailConfirmedAsync(clientId, ct);
                throw new InvalidOperationException("after the write, before the commit");
            }));

        Assert.Null(await ConfirmedAt(id));
    });

    // ------------------------------------------------------- confirmation

    /// <remarks>
    /// Idempotent, because two paths reach it — the confirmation link and a
    /// password reset, which proves the same thing. Overwriting would move the
    /// date somebody proved they could read the mailbox to the date they last
    /// forgot their password.
    /// </remarks>
    [SqlServerFact]
    public Task ConfirmingTwiceKeepsTheFirstDate() => WithAccount(async (id, _) =>
    {
        await Tokens.MarkEmailConfirmedAsync(id);
        var first = await ConfirmedAt(id);

        Assert.NotNull(first);

        await Tokens.MarkEmailConfirmedAsync(id);

        Assert.Equal(first, await ConfirmedAt(id));
    });

    [SqlServerFact]
    public Task AnAccountIsFoundByAddressAndById() => WithAccount(async (id, email) =>
    {
        var byAddress = await Tokens.ForRecoveryAsync(email);
        var byId = await Tokens.ByIdAsync(id);

        Assert.NotNull(byAddress);
        Assert.NotNull(byId);
        Assert.Equal(id, byAddress.Id);
        Assert.Equal(email, byId.Email);
        // It has a password, so a reset link is something it can act on.
        Assert.True(byAddress.HasLogin);
        Assert.Null(byId.EmailConfirmedAt);

        Assert.Null(await Tokens.ForRecoveryAsync("nobody@example.test"));
    });

    /// <remarks>
    /// An account created by an admin has no password to reset, and the forgot
    /// endpoint reads exactly this to decide not to send one — sending it would
    /// let anyone who can read that mailbox set the first password.
    /// </remarks>
    [SqlServerFact]
    public async Task AnAccountWithNoPasswordSaysSo()
    {
        var id = $"cli-probe-{Guid.NewGuid():n}";
        try
        {
            await Db.Database.ExecuteSqlAsync($"""
                INSERT INTO "Client" ("id", "name", "email", "discountPercent")
                VALUES ({id}, 'Made By An Admin', {id + "@example.test"}, 0)
                """);

            var account = await Tokens.ByIdAsync(id);

            Assert.NotNull(account);
            Assert.False(account.HasLogin);
        }
        finally
        {
            await Db.Database.ExecuteSqlAsync($"""DELETE FROM "Client" WHERE "id" = {id}""");
        }
    }

    private Task<DateTime?> ConfirmedAt(string clientId) =>
        Db.Database.SqlQuery<DateTime?>($"""
            SELECT "emailConfirmedAt" AS "Value" FROM "Client" WHERE "id" = {clientId}
            """).FirstAsync();
}
