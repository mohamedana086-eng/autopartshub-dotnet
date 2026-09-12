using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using AutoPartsHub.Api.Data;
using AutoPartsHub.Application.Abstractions;
using AutoPartsHub.Domain.Catalogue;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsHub.Api.Auth;

/// <summary>
/// One-time tokens sent to an address.
/// </summary>
/// <remarks>
/// Password recovery and email confirmation are the same mechanism: prove you
/// can read a mailbox, and something happens to the account behind it. They
/// share a table and differ by purpose, so there is one place where "expired",
/// "already used" and "never existed" are decided.
///
/// The token never touches the database. What is stored is a SHA-256 of it,
/// and the only copy of the token itself goes into the email — so a leaked
/// backup is a leak of hashes rather than of working password resets. SHA-256
/// rather than bcrypt because these are 32 random bytes from a CSPRNG: there
/// is nothing to guess, and a slow hash would only make every check slower.
///
/// BYTE FOR BYTE WITH THE OTHER API
/// --------------------------------
/// The two APIs share one database and one <c>VerificationToken</c> table
/// today, so a link mailed by one has to be redeemable by the other — a
/// customer does not know which process sent their reset email, and the link
/// is in their inbox either way. That makes three things a contract rather
/// than a choice: base64url with no padding (43 characters), SHA-256, and
/// lowercase hex. A port that reached for the .NET habit on any of them would
/// produce tokens that hash to something the other API never stored, and the
/// symptom would be "that link is not valid" on a link that is.
/// </remarks>
public sealed class VerificationTokens(AutoPartsContext db, IUnitOfWork uow)
{
    /// <summary>The two things a token can be for. The values the table holds.</summary>
    public static class Purposes
    {
        public const string PasswordReset = "password_reset";
        public const string EmailConfirmation = "email_confirmation";
    }

    /// <summary>
    /// How long each kind lives.
    /// </summary>
    /// <remarks>
    /// A reset token is the whole account in one string, so it is short. A
    /// confirmation token proves an address is reachable and grants nothing,
    /// so it can survive a night's sleep — expiring it in thirty minutes would
    /// mostly generate second attempts.
    /// </remarks>
    public static int LifetimeMinutes(string purpose) => purpose switch
    {
        Purposes.PasswordReset => 30,
        Purposes.EmailConfirmation => 24 * 60,
        _ => throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "Not a token purpose."),
    };

    /// <summary>
    /// How many may be issued per account per hour, per purpose.
    /// </summary>
    /// <remarks>
    /// Not a login rate limit — it is a limit on how much mail one request can
    /// cause to be sent to a third party. Without it, "forgot password" is a
    /// button that mails somebody else's inbox as often as you press it.
    /// </remarks>
    private const int MostPerHour = 5;

    /// <summary>Lowercase hex of the SHA-256, which is what the table holds.</summary>
    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>
    /// 32 bytes of CSPRNG, base64url — url-safe, so it survives being a query
    /// parameter.
    /// </summary>
    /// <remarks>
    /// Public because the encoding is a contract with the other API rather
    /// than an implementation detail, and a contract that cannot be asserted
    /// without reflection is one that gets asserted by the person it locks out
    /// of their account. See VerificationTokenFormatTests.
    /// </remarks>
    public static string Generate() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>The only copy. It is not stored and cannot be recovered afterwards.</summary>
    public record Issued(string Token, DateTime ExpiresAt);

    /// <summary>
    /// Issues a token, standing down any others of the same purpose.
    /// </summary>
    /// <remarks>
    /// Superseding matters for "resend": without it, every press leaves
    /// another live token, and the oldest one — the one most likely to have
    /// been seen by whoever should not have — keeps working.
    ///
    /// Returns null when the account has asked too often in the last hour. The
    /// caller still answers the same way it always does; refusing visibly here
    /// would turn the rate limit into the account oracle the flow is built to
    /// avoid.
    /// </remarks>
    public async Task<Issued?> IssueAsync(string clientId, string purpose, CancellationToken ct = default)
    {
        var token = Generate();
        var expiresAt = DateTime.UtcNow.AddMinutes(LifetimeMinutes(purpose));

        await using var transaction = await uow.BeginAsync(ct);

        var recent = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*) AS "Value" FROM "VerificationToken"
            WHERE "clientId" = {clientId} AND "purpose" = {purpose}
              AND "createdAt" > DATEADD(hour, -1, SYSUTCDATETIME())
            """).FirstAsync(ct);

        // Not committed, so the supersede below never happened either. Leaving
        // the previous tokens spent while refusing to issue a new one would
        // turn a rate limit into a lockout.
        if (recent >= MostPerHour) return null;

        // Spent, not deleted: the row is the record that a token existed, and
        // deleting it would also delete the evidence the rate check reads.
        await db.Database.ExecuteSqlAsync($"""
            UPDATE "VerificationToken" SET "usedAt" = SYSUTCDATETIME()
            WHERE "clientId" = {clientId} AND "purpose" = {purpose} AND "usedAt" IS NULL
            """, ct);

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "VerificationToken" ("id", "clientId", "purpose", "tokenHash", "expiresAt")
            VALUES ({Ids.New()}, {clientId}, {purpose}, {Hash(token)}, {expiresAt})
            """, ct);

        await transaction.CommitAsync(ct);

        return new Issued(token, expiresAt);
    }

    /// <summary>Why a token could not be spent, or <see cref="None"/>.</summary>
    public enum Refusal
    {
        None,
        Invalid,
        Expired,
        Used,
    }

    /// <summary>What a redemption came to.</summary>
    public readonly record struct Redemption(Refusal Refusal, string? ClientId, string? Email)
    {
        public bool Ok => Refusal == Refusal.None;

        public static Redemption Refused(Refusal why) => new(why, null, null);
    }

    /// <summary>
    /// Spends a token, or says why it cannot.
    /// </summary>
    /// <remarks>
    /// The check and the write are one transaction with the row locked, so two
    /// requests carrying the same token cannot both succeed. Without the lock
    /// the window is small and real: a reset link opened twice in quick
    /// succession, or clicked once by a mail scanner and once by the person.
    ///
    /// <c>UPDLOCK</c> is where PostgreSQL said <c>FOR UPDATE OF v</c>, and the
    /// <c>OF v</c> matters — only the token row is locked, not the account
    /// joined to it. <c>ROWLOCK</c> asks the engine not to widen that to a
    /// page, which at this table's size it otherwise might.
    ///
    /// <paramref name="apply"/> runs inside that transaction. If it throws, the
    /// token is not spent — which is the behaviour worth having, because a
    /// password that failed to save with a token already burnt leaves the
    /// account unreachable.
    /// </remarks>
    public async Task<Redemption> RedeemAsync(
        string token,
        string purpose,
        Func<string, CancellationToken, Task> apply,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(token)) return Redemption.Refused(Refusal.Invalid);

        await using var transaction = await uow.BeginAsync(ct);

        var row = await db.Database.SqlQuery<TokenRow>($"""
            SELECT v."id" AS "Id", v."clientId" AS "ClientId", c."email" AS "Email",
                   v."usedAt" AS "UsedAt",
                   CAST(CASE WHEN v."expiresAt" <= SYSUTCDATETIME() THEN 1 ELSE 0 END AS bit) AS "Expired"
            FROM "VerificationToken" v WITH (UPDLOCK, ROWLOCK)
            JOIN "Client" c ON c."id" = v."clientId"
            WHERE v."tokenHash" = {Hash(token)} AND v."purpose" = {purpose}
            """).FirstOrDefaultAsync(ct);

        if (row is null) return Redemption.Refused(Refusal.Invalid);
        if (row.UsedAt is not null) return Redemption.Refused(Refusal.Used);
        if (row.Expired) return Redemption.Refused(Refusal.Expired);

        await apply(row.ClientId, ct);

        await db.Database.ExecuteSqlAsync($"""
            UPDATE "VerificationToken" SET "usedAt" = SYSUTCDATETIME() WHERE "id" = {row.Id}
            """, ct);

        await transaction.CommitAsync(ct);

        return new Redemption(Refusal.None, row.ClientId, row.Email);
    }

    private record TokenRow(string Id, string ClientId, string Email, DateTime? UsedAt, bool Expired);

    /// <summary>Marks an address reachable. Idempotent: confirming twice is not
    /// an error.</summary>
    public Task MarkEmailConfirmedAsync(string clientId, CancellationToken ct = default) =>
        db.Database.ExecuteSqlAsync($"""
            UPDATE "Client"
               SET "emailConfirmedAt" = COALESCE("emailConfirmedAt", SYSUTCDATETIME())
             WHERE "id" = {clientId}
            """, ct);

    /// <summary>The account behind an address, or null. Only ever used where
    /// the answer is not reported.</summary>
    public Task<Account?> ForRecoveryAsync(string email, CancellationToken ct = default) =>
        db.Database.SqlQuery<Account>($"""
            SELECT "id" AS "Id", "name" AS "Name", "email" AS "Email",
                   CAST(CASE WHEN "passwordHash" IS NOT NULL THEN 1 ELSE 0 END AS bit) AS "HasLogin",
                   "emailConfirmedAt" AS "EmailConfirmedAt"
            FROM "Client" WHERE "email" = {email}
            """).FirstOrDefaultAsync(ct);

    /// <summary>The signed-in account, by id. Never by an address the caller
    /// supplied.</summary>
    public Task<Account?> ByIdAsync(string clientId, CancellationToken ct = default) =>
        db.Database.SqlQuery<Account>($"""
            SELECT "id" AS "Id", "name" AS "Name", "email" AS "Email",
                   CAST(CASE WHEN "passwordHash" IS NOT NULL THEN 1 ELSE 0 END AS bit) AS "HasLogin",
                   "emailConfirmedAt" AS "EmailConfirmedAt"
            FROM "Client" WHERE "id" = {clientId}
            """).FirstOrDefaultAsync(ct);

    public record Account(string Id, string Name, string Email, bool HasLogin, DateTime? EmailConfirmedAt);
}
