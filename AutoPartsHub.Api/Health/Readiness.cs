using System.Diagnostics;
using AutoPartsHub.Api.Catalogue;
using AutoPartsHub.Api.Data;
using AutoPartsHub.Api.Mail;
using AutoPartsHub.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsHub.Api.Health;

/// <summary>
/// Whether this instance can serve traffic.
/// </summary>
/// <remarks>
/// Kept apart from liveness on purpose, and the distinction is the whole
/// point: <c>/health</c> answers "is this process alive", and a host that
/// restarts a container because the database blinked turns a brief outage into
/// a long one. <c>/health/ready</c> answers "should this instance be in the
/// rotation", which is a question with a different answer and a different
/// consequence.
///
/// WHAT MAKES AN INSTANCE UNREADY
/// ------------------------------
/// Only the things without which most requests are wrong. There are two.
///
/// The database, obviously. And the SCHEMA — a database that answers but is
/// two migrations behind is not one this instance can serve from, and during a
/// cutover that is not a hypothetical: it is the window between a deployment
/// starting and its migrations finishing, which is exactly when a load
/// balancer would otherwise send it traffic.
///
/// The cache is NOT one of them, and that is <see cref="IPriceCache"/>'s own
/// decision rather than this file's: "a cache that is down is a slow shop, and
/// a cache that is down and throwing is a closed one". Taking the instance out
/// of rotation for it would close the shop to save it from being slow. It is
/// reported, because an operator wants to know, and it does not decide.
///
/// Full text is reported for the same reason and decides nothing either — the
/// search has a lane that works without it (T-067).
///
/// EVERY CHECK HAS A DEADLINE
/// --------------------------
/// A readiness probe that hangs is worse than one that fails: the host waits,
/// the instance stays in rotation, and requests queue behind the same
/// dependency the probe is stuck on. So each check is given a budget and
/// counted as failed when it runs out, which turns a hang into an answer.
/// </remarks>
public sealed class Readiness(
    AutoPartsContext db,
    IPriceCache cache,
    FullTextSearch fullText,
    IHostEnvironment environment)
{
    /// <summary>
    /// How long any one check may take.
    /// </summary>
    /// <remarks>
    /// Short, because a probe runs every few seconds and its job is to answer.
    /// A database that needs more than two seconds to return <c>1</c> is not
    /// one this instance should be serving from, whatever it would eventually
    /// have said.
    ///
    /// The FIRST probe after a start is the slow one — 1.5s against LocalDB
    /// here, where a warm one is 90ms, because the connection does not exist
    /// yet. That is close enough to the budget to be worth knowing, and it is
    /// the right answer anyway: an instance that cannot yet reach its database
    /// quickly is not ready, and the host will ask again a second later.
    /// </remarks>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);

    public async Task<Report> CheckAsync(CancellationToken ct = default)
    {
        // Sequential rather than concurrent. Two of these share a DbContext,
        // which is not thread-safe, and the whole set is bounded by four
        // budgets — which is still well inside any probe interval.
        var checks = new List<Check>
        {
            await Timed("database", required: true, DatabaseAsync, ct),
            await Timed("schema", required: true, SchemaAsync, ct),
            await Timed("cache", required: false, CacheAsync, ct),
            await Timed("fullText", required: false, FullTextAsync, ct),
            Mail(),
        };

        return new Report(checks.All(c => c.Ok || !c.Required), checks);
    }

    /// <summary>Runs one check against the clock, and turns anything it throws
    /// into an answer.</summary>
    private static async Task<Check> Timed(
        string name, bool required, Func<CancellationToken, Task<string>> check, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Budget);

        var clock = Stopwatch.StartNew();
        try
        {
            var detail = await check(deadline.Token);
            return new Check(name, true, required, clock.ElapsedMilliseconds, detail);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return new Check(name, false, required, clock.ElapsedMilliseconds,
                $"no answer within {Budget.TotalSeconds:0}s");
        }
        catch (Exception cause)
        {
            // The type as well as the message. "Login failed for user" and
            // "A network-related error" are the same sentence to a reader
            // skimming, and the exception type is what tells them apart.
            return new Check(name, false, required, clock.ElapsedMilliseconds,
                $"{cause.GetType().Name}: {cause.Message.Split('\n')[0]}");
        }
    }

    /// <remarks>
    /// <c>SELECT 1</c> rather than counting a table. A count takes a lock that
    /// a probe has no business taking, and its answer says nothing that
    /// connecting did not.
    /// </remarks>
    private async Task<string> DatabaseAsync(CancellationToken ct)
    {
        await db.Database.SqlQuery<int>($"SELECT 1 AS \"Value\"").FirstAsync(ct);

        return db.Database.ProviderName?.Split('.').Last().ToLowerInvariant() ?? "unknown";
    }

    /// <remarks>
    /// A schema behind the code is the failure this endpoint exists for. It
    /// costs one query against a table with as many rows as there are
    /// migrations.
    /// </remarks>
    private async Task<string> SchemaAsync(CancellationToken ct)
    {
        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToArray();

        if (pending.Length > 0)
        {
            throw new InvalidOperationException(
                $"{pending.Length} migration(s) not applied, beginning {pending[0]}");
        }

        var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).Count();
        return $"{applied} migration(s), none pending";
    }

    /// <remarks>
    /// The version, because reading it is what every cached price does first
    /// and because it proves a round trip rather than a connection. Today the
    /// implementation is in memory and cannot fail; the check is here for the
    /// day it is Redis, which is what the backlog names.
    /// </remarks>
    private async Task<string> CacheAsync(CancellationToken ct)
    {
        var version = await cache.VersionAsync(ct);

        return $"{cache.GetType().Name}, version {version}";
    }

    /// <remarks>
    /// Not a failure when it is absent — LocalDB cannot host it at all, and
    /// the near-miss search has a lane that does without. Reported because
    /// which lane a deployment is on is otherwise invisible.
    /// </remarks>
    private async Task<string> FullTextAsync(CancellationToken ct) =>
        await fullText.IsIndexedAsync(ct)
            ? "indexed; names matched by word"
            : "not available; names matched by prefix";

    /// <remarks>
    /// No I/O, so no budget. It is a reading of one environment variable, and
    /// it is here because "no transport" is the state in which a password
    /// reset is accepted and never arrives — which looks like nothing at all
    /// from outside.
    /// </remarks>
    private Check Mail()
    {
        var transport = MailTransport.Name();
        var configured = transport != TransportName.Unconfigured;

        return new Check("mail", true, false, 0, configured
            ? $"{transport.ToString().ToLowerInvariant()} transport"
            : environment.IsProduction()
                ? "no transport: password resets and confirmations will not be sent"
                : "no transport (development)");
    }

    /// <summary>One dependency, and what it said.</summary>
    /// <param name="Required">False means it is reported and does not decide.</param>
    public sealed record Check(string Name, bool Ok, bool Required, long Ms, string Detail);

    /// <summary>Ready when every REQUIRED check passed.</summary>
    public sealed record Report(bool Ready, IReadOnlyList<Check> Checks);
}
