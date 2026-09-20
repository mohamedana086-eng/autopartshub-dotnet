using System.Text.Json;
using AutoPartsHub.Api.Auth;
using AutoPartsHub.Api.Data;
using AutoPartsHub.Api.Support;
using AutoPartsHub.Domain;
using AutoPartsHub.Domain.Catalogue;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsHub.Api.Endpoints;

/// <summary>
/// Changing a ticket's status, and asking to hear about one.
/// </summary>
/// <remarks>
/// The shape the backlog specifies. <c>PATCH /api/admin/tickets/{id}</c> is
/// unchanged and still answers; this is where it moves to, and it moves out of
/// <c>/api/admin</c> because it is not only staff who may set a status.
///
/// NOT UNDER /api/admin, SO NOT BEHIND ITS GUARD
/// ---------------------------------------------
/// <see cref="AdminRouteGuard"/> is path-based: everything under
/// <c>/api/admin</c> needs staff, and nothing else is touched by it. These two
/// routes are therefore on their own, and they gate themselves — which is the
/// arrangement that has to be right rather than assumed, because a route
/// answering to whoever asks is not obviously wrong from the outside.
///
/// WHO MAY SET WHAT
/// ----------------
/// Staff may set any of the three, on any ticket their scope reaches. A
/// customer may set <c>resolved</c> or <c>open</c> on their own ticket, and
/// not <c>answered</c> — that one means "we replied", which only a staff
/// message produces, and letting a customer claim it would take their own
/// ticket off the queue of things waiting on us.
///
/// The status is otherwise derived from messages arriving, which is the
/// design <see cref="TicketInput.StatusAfterMessage"/> describes. This is the
/// exception rather than a second way of doing it: somebody saying "that
/// answered it" without writing a reply, and somebody saying "it did not".
/// </remarks>
public static class TicketStatusEndpoints
{
    public static void MapTicketStatusEndpoints(this IEndpointRouteBuilder app)
    {
        // PATCH /api/tickets/<id>/status { status }
        app.MapPatch("/api/tickets/{id}/status", async (
            string id, JsonElement body, HttpContext http, SessionTokens tokens,
            ManagerReachLoader reaches, AutoPartsContext db, CancellationToken ct) =>
        {
            // Decoded here rather than inside WhoAsync, so the gate is visible
            // AT the route. AdminWriteScopeTests reads these files as text and
            // a guard one call away is unreadable to it — and these two are
            // writes that sit outside /api/admin, where the path-based
            // middleware does not reach either. A guard nothing can see is a
            // guard nothing is holding in place.
            var session = tokens.Decode(http.Request.Cookies[SessionTokens.CookieName]);
            if (session is null) return Results.Json(new { error = "Not signed in." }, statusCode: 401);

            var caller = await WhoAsync(session, reaches, ct);

            var status = JsonValues.AsString(JsonValues.Get(body, "status")).Trim();
            if (!TicketInput.IsKnown(status))
            {
                return Results.BadRequest(new
                {
                    error = $"Status must be one of: {string.Join(", ", TicketInput.Statuses)}.",
                });
            }

            // The one a customer may not claim. Refused by name rather than by
            // listing what they may set, so the sentence says why.
            if (!caller.IsStaff && status == TicketInput.Answered)
            {
                return Results.Json(
                    new
                    {
                        error = "Only we can mark a ticket answered. Write a message, "
                              + "or mark it resolved if it is settled.",
                    },
                    statusCode: 403);
            }

            var ticket = await FindAsync(db, id, caller, ct);
            if (ticket is null) return Results.NotFound(new { error = "Ticket not found." });

            await db.Database.ExecuteSqlAsync(
                $"""UPDATE "Ticket" SET "status" = {status} WHERE "id" = {ticket.Id}""", ct);

            return Results.Ok(new { id = ticket.Id, status });
        });

        // PUT /api/tickets/<id>/following { following: true | false }
        //
        // The caller's own following, never anybody else's. A route that took
        // a person as well as a ticket would be a way to sign a colleague up
        // for somebody else's mail, and the screen that legitimately wants
        // that is an admin one that does not exist yet.
        app.MapPut("/api/tickets/{id}/following", async (
            string id, JsonElement body, HttpContext http, SessionTokens tokens,
            ManagerReachLoader reaches, AutoPartsContext db, CancellationToken ct) =>
        {
            // Decoded here rather than inside WhoAsync, so the gate is visible
            // AT the route. AdminWriteScopeTests reads these files as text and
            // a guard one call away is unreadable to it — and these two are
            // writes that sit outside /api/admin, where the path-based
            // middleware does not reach either. A guard nothing can see is a
            // guard nothing is holding in place.
            var session = tokens.Decode(http.Request.Cookies[SessionTokens.CookieName]);
            if (session is null) return Results.Json(new { error = "Not signed in." }, statusCode: 401);

            var caller = await WhoAsync(session, reaches, ct);

            if (JsonValues.Get(body, "following") is not { } asked
                || asked.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return Results.BadRequest(new { error = "following must be true or false." });
            }

            var following = asked.ValueKind == JsonValueKind.True;

            // Scoped the same way the read is: you can only follow a ticket
            // you could already open, and one you cannot is reported missing
            // rather than refused — the same answer the thread gives.
            var ticket = await FindAsync(db, id, caller, ct);
            if (ticket is null) return Results.NotFound(new { error = "Ticket not found." });

            if (following)
            {
                // MERGE rather than INSERT, so pressing follow twice is
                // following once rather than a unique-key violation. HOLDLOCK
                // for the reason the cart's upsert has it: two tabs is enough.
                await db.Database.ExecuteSqlAsync($"""
                    MERGE "TicketFollower" WITH (HOLDLOCK) AS target
                    USING (VALUES ({ticket.Id}, {caller.UserId})) AS source("ticketId", "clientId")
                      ON target."ticketId" = source."ticketId"
                     AND target."clientId" = source."clientId"
                    WHEN NOT MATCHED THEN
                      INSERT ("id", "ticketId", "clientId")
                      VALUES ({Ids.New()}, {ticket.Id}, {caller.UserId});
                    """, ct);
            }
            else
            {
                await db.Database.ExecuteSqlAsync($"""
                    DELETE FROM "TicketFollower"
                    WHERE "ticketId" = {ticket.Id} AND "clientId" = {caller.UserId}
                    """, ct);
            }

            var followers = (await db.Database.SqlQuery<int>($"""
                SELECT COUNT(*) AS "Value" FROM "TicketFollower" WHERE "ticketId" = {ticket.Id}
                """).ToListAsync(ct)).FirstOrDefault();

            return Results.Ok(new { id = ticket.Id, following, followers });
        });
    }

    /// <summary>Who is asking, and how much of the ticket table they reach.</summary>
    /// <param name="OwnClientId">
    /// Set for a customer, so the query narrows to their own tickets. Null for
    /// staff, whose narrowing is <paramref name="ScopeTo"/> instead.
    /// </param>
    private record Caller(string UserId, bool IsStaff, string? OwnClientId, string? ScopeTo);

    /// <remarks>
    /// Reads the session once and asks for the manager's reach only when the
    /// caller is staff — a customer's scope is their own id and needs no
    /// query.
    /// </remarks>
    private static async Task<Caller> WhoAsync(
        SessionPayload session, ManagerReachLoader reaches, CancellationToken ct)
    {
        var scope = Scope.From(session);
        if (!scope.IsStaff) return new Caller(session.UserId, false, session.UserId, null);

        var resolved = await reaches.ResolveAsync(scope, ct);

        return new Caller(session.UserId, true, null, resolved.ManagedBy);
    }

    /// <summary>
    /// The ticket, if this caller reaches it.
    /// </summary>
    /// <remarks>
    /// The narrowing is in the WHERE rather than in a check afterwards, which
    /// is the same arrangement the rest of the ticket routes use: a condition
    /// bound into the statement cannot be forgotten by a branch, and it cannot
    /// race something that moves the row in between.
    /// </remarks>
    private static async Task<TicketIdRow?> FindAsync(
        AutoPartsContext db, string id, Caller caller, CancellationToken ct) =>
        (await db.Database.SqlQuery<TicketIdRow>($"""
            SELECT t."id" AS "Id", t."status" AS "Status"
            FROM "Ticket" t
            JOIN "Client" c ON c."id" = t."clientId"
            WHERE t."id" = {id}
              AND ({caller.OwnClientId} IS NULL OR t."clientId" = {caller.OwnClientId})
              AND ({caller.ScopeTo} IS NULL OR c."salesManagerId" = {caller.ScopeTo})
            """).ToListAsync(ct)).FirstOrDefault();

    private record TicketIdRow(string Id, string Status);
}
