using AutoPartsHub.Api.Auth;
using AutoPartsHub.Api.Data;
using AutoPartsHub.Domain.Catalogue;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsHub.Api.Endpoints;

/// <summary>
/// What customers looked for and did not find, and what was done about it.
/// </summary>
/// <remarks>
/// The shape the backlog specifies, beside <c>GET /api/admin/search-misses</c>
/// rather than instead of it. The old route is unchanged and still answers
/// exactly what it answered — the storefront calls it today, and this is the
/// route it moves to when it moves.
///
/// WHY A NEW ROUTE RATHER THAN A CHANGED ONE
/// -----------------------------------------
/// Crossing a term off means naming the row, and the old response carries no
/// id — it is keyed by term and whether a filter was on. Adding an id to it
/// would be a change to a response both APIs serve, which is a change in two
/// repositories and one that <c>compare.mjs</c> cannot currently check. A new
/// route costs neither: it is free to carry the id, the resolution and the
/// dates, because nothing is reading it yet.
///
/// RESOLVED IS A DECISION SOMEBODY MADE
/// ------------------------------------
/// Recorded as when and by whom rather than as a flag. "Is this dealt with" is
/// <c>resolvedAt IS NULL</c> either way, and the two questions asked straight
/// afterwards are answered by the same columns instead of being lost.
///
/// NOTHING REOPENS ITSELF
/// ----------------------
/// A resolved term searched again keeps climbing and stays resolved.
/// Resolving is a judgement — the part was stocked, or it was decided not to
/// stock it — and a machine undoing a judgement because the case recurred
/// would make "we are not going to sell this" impossible to say once.
/// <c>seenSinceResolved</c> is what the screen shows instead, so a decision
/// worth revisiting looks like one without being reversed on somebody's
/// behalf.
/// </remarks>
public static class FailedSearchEndpoints
{
    /// <summary>Which of them to list.</summary>
    private static readonly string[] States = ["open", "resolved", "all"];

    public static void MapFailedSearchEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /api/admin/failed-searches?state=open|resolved|all&order=searches|recent&page=&pageSize=
        //
        // RequireStaff, like the report it replaces: this is a buying list —
        // which parts to stock next — and it is also what a salesperson reads
        // before a call. There is nothing per-customer in it to scope, because
        // the table records no client by design, so a salesperson and an admin
        // see the same rows and that is the correct answer rather than a
        // missing narrowing.
        app.MapGet("/api/admin/failed-searches", async (
            HttpContext http, AdminGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireStaff(http);
            if (!g.Ok) return g.Response!;

            // Open by default. The report's job is what still needs doing, and
            // a default of "all" would put the pile of already-handled terms
            // back on top of it the moment anybody used this.
            var requested = http.Request.Query["state"].ToString();
            var state = States.Contains(requested) ? requested : "open";

            // Two orderings, because they answer different questions: what is
            // asked for most, and what has STARTED being asked for. The second
            // catches a new model arriving on the roads while the all-time
            // list is still topped by something stocked months ago.
            var byRecent = http.Request.Query["order"].ToString() == "recent";
            var page = Paging.ReadPage(http.Request.Query["page"]);
            var pageSize = Paging.ReadPageSize(
                http.Request.Query["pageSize"], http.Request.Query["limit"]);

            var open = state == "open";
            var everything = state == "all";

            var rows = await db.Database.SqlQuery<FailedSearchRow>($"""
                SELECT m."id" AS "Id", m."term" AS "Term", m."narrowed" AS "Narrowed",
                       m."searches" AS "Searches",
                       m."firstSeenAt" AS "FirstSeenAt", m."lastSeenAt" AS "LastSeenAt",
                       m."resolvedAt" AS "ResolvedAt", c."name" AS "ResolvedByName"
                FROM "SearchMiss" m
                LEFT JOIN "Client" c ON c."id" = m."resolvedById"
                WHERE {everything} = 1
                   OR ({open} = 1 AND m."resolvedAt" IS NULL)
                   OR ({open} = 0 AND m."resolvedAt" IS NOT NULL)
                ORDER BY
                  CASE WHEN {byRecent} = 1 THEN m."lastSeenAt" END DESC,
                  CASE WHEN {byRecent} = 1 THEN NULL ELSE m."searches" END DESC,
                  m."term" ASC
                OFFSET {(page - 1) * pageSize} ROWS FETCH NEXT {pageSize} ROWS ONLY
                """).ToListAsync(ct);

            var total = (await db.Database.SqlQuery<int>($"""
                SELECT COUNT(*) AS "Value" FROM "SearchMiss" m
                WHERE {everything} = 1
                   OR ({open} = 1 AND m."resolvedAt" IS NULL)
                   OR ({open} = 0 AND m."resolvedAt" IS NOT NULL)
                """).ToListAsync(ct)).FirstOrDefault();

            return Results.Ok(new
            {
                searches = rows.Select(m => new
                {
                    id = m.Id,
                    term = m.Term,
                    // Whether a filter was on. A term that only ever missed
                    // while narrowed is a gap in one supplier relationship,
                    // not in the catalogue.
                    narrowed = m.Narrowed,
                    searches = m.Searches,
                    firstSeenAt = Timestamps.Iso(m.FirstSeenAt),
                    lastSeenAt = Timestamps.Iso(m.LastSeenAt),
                    resolvedAt = Timestamps.Iso(m.ResolvedAt),
                    resolvedBy = m.ResolvedByName,
                    // Answered here rather than left to the screen to compare
                    // two timestamps, so a second screen asking the same
                    // question cannot decide it differently. It is the whole
                    // reason nothing reopens itself.
                    seenSinceResolved = m.ResolvedAt is not null && m.LastSeenAt > m.ResolvedAt,
                }),
                state,
                order = byRecent ? "recent" : "searches",
                total,
                page,
                pageSize,
                pages = Paging.PageCount(total, pageSize),
            });
        });

        // POST /api/admin/failed-searches/<id>/resolve
        //
        // RequireAdmin, where the report itself is RequireStaff. Reading which
        // parts are being asked for is everybody's job; deciding that one has
        // been dealt with is a buying decision, and the row carries no
        // customer for a salesperson's scope to narrow — so RequireOperator
        // would be a write opened to sales with nothing to scope it by, which
        // is the shape AdminWriteScopeTests exists to refuse.
        app.MapPost("/api/admin/failed-searches/{id}/resolve", async (
            string id, HttpContext http, AdminGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            return await SetResolutionAsync(db, id, g.Session!.UserId, ct);
        });

        // POST /api/admin/failed-searches/<id>/reopen
        app.MapPost("/api/admin/failed-searches/{id}/reopen", async (
            string id, HttpContext http, AdminGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            return await SetResolutionAsync(db, id, null, ct);
        });
    }

    /// <summary>
    /// Crosses one off, or puts it back.
    /// </summary>
    /// <param name="resolvedBy">Who decided, or null to reopen.</param>
    /// <remarks>
    /// Both columns move together, which is what the CHECK constraint on the
    /// table requires: a date with no decider, or a decider with no date, is
    /// half a record of a decision.
    ///
    /// Idempotent. Resolving something already resolved keeps the FIRST
    /// decision rather than restamping it with today and whoever pressed the
    /// button again — the date on the row should be when it was dealt with,
    /// not when somebody last looked at it.
    /// </remarks>
    private static async Task<IResult> SetResolutionAsync(
        AutoPartsContext db, string id, string? resolvedBy, CancellationToken ct)
    {
        var updated = (await db.Database.SqlQuery<FailedSearchRow>($"""
            UPDATE "SearchMiss"
               SET "resolvedAt" = CASE WHEN {resolvedBy is null} = 1 THEN NULL
                                       ELSE COALESCE("resolvedAt", SYSUTCDATETIME()) END,
                   "resolvedById" = CASE WHEN {resolvedBy is null} = 1 THEN NULL
                                         ELSE COALESCE("resolvedById", {resolvedBy}) END
            OUTPUT INSERTED."id" AS "Id", INSERTED."term" AS "Term",
                   INSERTED."narrowed" AS "Narrowed", INSERTED."searches" AS "Searches",
                   INSERTED."firstSeenAt" AS "FirstSeenAt", INSERTED."lastSeenAt" AS "LastSeenAt",
                   INSERTED."resolvedAt" AS "ResolvedAt", NULL AS "ResolvedByName"
             WHERE "id" = {id}
            """).ToListAsync(ct)).FirstOrDefault();

        if (updated is null) return Results.NotFound(new { error = "No such failed search." });

        return Results.Ok(new
        {
            id = updated.Id,
            term = updated.Term,
            resolvedAt = Timestamps.Iso(updated.ResolvedAt),
            seenSinceResolved = updated.ResolvedAt is not null && updated.LastSeenAt > updated.ResolvedAt,
        });
    }
}

/// <summary>One failed search, with whatever was decided about it.</summary>
public record FailedSearchRow(
    string Id,
    string Term,
    bool Narrowed,
    int Searches,
    DateTime FirstSeenAt,
    DateTime LastSeenAt,
    DateTime? ResolvedAt,
    string? ResolvedByName);
