using System.Text.Json;
using AutoPartsHub.Api.Auth;
using AutoPartsHub.Api.Catalogue;
using AutoPartsHub.Api.Data;
using AutoPartsHub.Api.Mail;
using AutoPartsHub.Api.Support;
using AutoPartsHub.Domain.Catalogue;
using AutoPartsHub.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AutoPartsHub.Api.Endpoints;

/// <summary>
/// Tickets: a customer with a problem, and the thread about it.
/// </summary>
/// <remarks>
/// THE ONE RULE THIS FILE EXISTS TO KEEP
/// -------------------------------------
/// <c>TicketMessage.internal</c> is a note staff write to each other —
/// "supplier says three weeks, do not promise two" — and it sits on the same
/// table, in the same thread, in the same order as the replies the customer
/// reads. That is what makes it useful and exactly what makes it dangerous:
/// one query forgetting the column sends it to the person it is about.
///
/// So there are two readers and never one with a flag. <see
/// cref="CustomerThreadAsync"/> cannot return an internal note because its
/// WHERE says so; <see cref="StaffThreadAsync"/> returns everything. A single
/// method taking <c>includeInternal</c> would be one wrong argument away from
/// the leak, and the argument would be supplied by whichever endpoint was
/// written last.
/// </remarks>
public static class TicketEndpoints
{
    private const string UniqueViolation = "23505";

    public static void MapTicketEndpoints(this IEndpointRouteBuilder app)
    {
        /* ------------------------------------------- the customer's own --- */

        // GET /api/tickets — the signed-in customer's own, newest talk first.
        app.MapGet("/api/tickets", async (
            HttpContext http, SessionTokens tokens, AutoPartsContext db, CancellationToken ct) =>
        {
            var session = tokens.Decode(http.Request.Cookies[SessionTokens.CookieName]);
            if (session is null) return Results.Json(new { error = "Not signed in." }, statusCode: 401);

            var tickets = await db.Database.SqlQuery<TicketRow>($"""
                SELECT t."id" AS "Id", t."reference" AS "Reference", t."subject" AS "Subject",
                       t."status" AS "Status", t."clientId" AS "ClientId",
                       c."name" AS "ClientName", t."orderId" AS "OrderId",
                       o."reference" AS "OrderReference", t."createdAt" AS "CreatedAt",
                       t."lastMessageAt" AS "LastMessageAt",
                       (SELECT COUNT(*) FROM "TicketMessage" m
                         WHERE m."ticketId" = t."id" AND NOT m."internal") AS "MessageCount"
                FROM "Ticket" t
                JOIN "Client" c ON c."id" = t."clientId"
                LEFT JOIN "Order" o ON o."id" = t."orderId"
                WHERE t."clientId" = {session.UserId}
                ORDER BY t."lastMessageAt" DESC
                """).ToListAsync(ct);

            return Results.Ok(new { tickets = tickets.Select(Customer) });
        });

        // POST /api/tickets { subject, body, orderId? }
        app.MapPost("/api/tickets", async (
            JsonElement body, HttpContext http, SessionTokens tokens, AutoPartsContext db,
            CancellationToken ct) =>
        {
            var session = tokens.Decode(http.Request.Cookies[SessionTokens.CookieName]);
            if (session is null) return Results.Json(new { error = "Not signed in." }, statusCode: 401);

            var input = TicketInput.ReadNewTicket(body);
            if (!input.Ok) return Results.BadRequest(new { error = input.Error });

            var value = input.Value!;

            // Attached only if it is theirs. The id comes from a browser, and
            // without this a customer could put a stranger's order on their own
            // ticket and read its reference back off their own screen through
            // the join.
            if (value.OrderId is not null)
            {
                var mine = await db.Orders
                    .AnyAsync(o => o.Id == value.OrderId && o.ClientId == session.UserId, ct);
                if (!mine)
                {
                    return Results.BadRequest(new { error = "That order is not on your account." });
                }
            }

            var id = await CreateAsync(db, session.UserId, session.Name, value, ct);

            var created = (await TicketsAsync(db, id, session.UserId, null, ct)).Single();

            return Results.Json(new { ticket = Customer(created) }, statusCode: 201);
        });

        // GET /api/tickets/<id> — one of their own threads, notes excluded.
        app.MapGet("/api/tickets/{id}", async (
            string id, HttpContext http, SessionTokens tokens, AutoPartsContext db,
            CancellationToken ct) =>
        {
            var session = tokens.Decode(http.Request.Cookies[SessionTokens.CookieName]);
            if (session is null) return Results.Json(new { error = "Not signed in." }, statusCode: 401);

            var ticket = (await TicketsAsync(db, id, session.UserId, null, ct)).FirstOrDefault();

            // Not theirs, or not there. Both answer the same way: somebody
            // else's ticket does not exist as far as this account is concerned.
            if (ticket is null) return Results.NotFound(new { error = "Ticket not found." });

            var messages = await CustomerThreadAsync(db, ticket.Id, ct);

            return Results.Ok(new
            {
                ticket = Customer(ticket),
                // `internal` is not serialised at all here. Every row is
                // already public by the query, so sending the column would only
                // be sending a field that is always false — and a field that is
                // always false is one somebody eventually reads as meaningful.
                messages = messages.Select(m => new
                {
                    id = m.Id,
                    authorName = m.AuthorName,
                    fromStaff = m.FromStaff,
                    body = m.Body,
                    createdAt = Timestamps.Iso(m.CreatedAt),
                }),
            });
        });

        // POST /api/tickets/<id>/messages { body }
        //
        // Always public and never internal: the flag is not offered on this
        // side at all rather than offered and refused. A parameter that is
        // always rejected is a parameter somebody eventually makes work.
        app.MapPost("/api/tickets/{id}/messages", async (
            string id, JsonElement body, HttpContext http, SessionTokens tokens,
            AutoPartsContext db, CancellationToken ct) =>
        {
            var session = tokens.Decode(http.Request.Cookies[SessionTokens.CookieName]);
            if (session is null) return Results.Json(new { error = "Not signed in." }, statusCode: 401);

            var message = TicketInput.ReadMessageBody(body);
            if (!message.Ok) return Results.BadRequest(new { error = message.Error });

            var ticket = (await TicketsAsync(db, id, session.UserId, null, ct)).FirstOrDefault();
            if (ticket is null) return Results.NotFound(new { error = "Ticket not found." });

            if (!TicketInput.IsKnown(ticket.Status))
            {
                return Results.Json(
                    new { error = "That ticket is in an unknown state." }, statusCode: 409);
            }

            var status = await AddMessageAsync(db, ticket.Id, ticket.Status,
                session.UserId, session.Name, fromStaff: false, internalNote: false,
                message.Value!, ct);

            return Results.Json(new { status }, statusCode: 201);
        });

        /* --------------------------------------------------- the queue --- */

        // GET /api/admin/tickets?status=&page=&pageSize=
        //
        // Ordered by what has been waiting LONGEST, not by what arrived last. A
        // support list read newest-first buries the customer who has been
        // waiting three days under the one who wrote a minute ago, which is
        // exactly the wrong way round for the one list whose job is to be
        // worked through.
        app.MapGet("/api/admin/tickets", async (
            HttpContext http, AdminGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireStaff(http);
            if (!g.Ok) return g.Response!;

            var scope = g.ScopeTo;
            var requested = http.Request.Query["status"].ToString().Trim();

            if (requested.Length > 0 && !TicketInput.IsKnown(requested))
            {
                return Results.BadRequest(new
                {
                    error = $"Status must be one of: {string.Join(", ", TicketInput.Statuses)}.",
                });
            }

            var status = requested.Length > 0 ? requested : null;
            var page = Paging.ReadPage(http.Request.Query["page"]);
            var pageSize = Paging.ReadPageSize(
                http.Request.Query["pageSize"], http.Request.Query["limit"]);

            var tickets = await db.Database.SqlQuery<TicketRow>($"""
                SELECT t."id" AS "Id", t."reference" AS "Reference", t."subject" AS "Subject",
                       t."status" AS "Status", t."clientId" AS "ClientId",
                       c."name" AS "ClientName", t."orderId" AS "OrderId",
                       o."reference" AS "OrderReference", t."createdAt" AS "CreatedAt",
                       t."lastMessageAt" AS "LastMessageAt",
                       (SELECT COUNT(*) FROM "TicketMessage" m
                         WHERE m."ticketId" = t."id" AND NOT m."internal") AS "MessageCount"
                FROM "Ticket" t
                JOIN "Client" c ON c."id" = t."clientId"
                LEFT JOIN "Order" o ON o."id" = t."orderId"
                WHERE ({scope} IS NULL OR c."salesManagerId" = {scope})
                  AND ({status} IS NULL OR t."status" = {status})
                ORDER BY t."lastMessageAt" ASC
                OFFSET {(page - 1) * pageSize} ROWS FETCH NEXT {pageSize} ROWS ONLY
                """).ToListAsync(ct);

            var total = (await db.Database.SqlQuery<int>($"""
                SELECT COUNT(*) AS "Value"
                FROM "Ticket" t
                JOIN "Client" c ON c."id" = t."clientId"
                WHERE ({scope} IS NULL OR c."salesManagerId" = {scope})
                  AND ({status} IS NULL OR t."status" = {status})
                """).ToListAsync(ct)).FirstOrDefault();

            return Results.Ok(new
            {
                tickets = tickets.Select(Staff),
                status,
                total,
                page,
                pageSize,
                pages = Paging.PageCount(total, pageSize),
            });
        });

        // GET /api/admin/tickets/<id> — everything, notes included.
        app.MapGet("/api/admin/tickets/{id}", async (
            string id, HttpContext http, AdminGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireStaff(http);
            if (!g.Ok) return g.Response!;

            var ticket = (await TicketsAsync(db, id, null, g.ScopeTo, ct)).FirstOrDefault();
            if (ticket is null) return Results.NotFound(new { error = "Ticket not found." });

            var messages = await StaffThreadAsync(db, ticket.Id, ct);

            return Results.Ok(new
            {
                ticket = Staff(ticket),
                messages = messages.Select(m => new
                {
                    id = m.Id,
                    authorName = m.AuthorName,
                    fromStaff = m.FromStaff,
                    // Sent here, and only here.
                    @internal = m.Internal,
                    body = m.Body,
                    createdAt = Timestamps.Iso(m.CreatedAt),
                }),
            });
        });

        // PATCH /api/admin/tickets/<id> { status }
        //
        // The only status change anybody makes by hand. `open` and `answered`
        // are consequences of a message arriving and are set by the message;
        // resolving is a decision, and reopening one is another.
        app.MapPatch("/api/admin/tickets/{id}", async (
            string id, JsonElement body, HttpContext http, AdminGate gate,
            AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireOperator(http);
            if (!g.Ok) return g.Response!;

            var status = JsonValues.AsString(JsonValues.Get(body, "status")).Trim();
            if (!TicketInput.IsKnown(status))
            {
                return Results.BadRequest(new
                {
                    error = $"Status must be one of: {string.Join(", ", TicketInput.Statuses)}.",
                });
            }

            var ticket = (await TicketsAsync(db, id, null, g.ScopeTo, ct)).FirstOrDefault();
            if (ticket is null) return Results.NotFound(new { error = "Ticket not found." });

            await db.Database.ExecuteSqlAsync(
                $"""UPDATE "Ticket" SET "status" = {status} WHERE "id" = {ticket.Id}""", ct);

            return Results.Ok(new { id = ticket.Id, status });
        });

        // POST /api/admin/tickets/<id>/messages { body, internal? }
        app.MapPost("/api/admin/tickets/{id}/messages", async (
            string id, JsonElement body, HttpContext http, AdminGate gate,
            AutoPartsContext db, Mailer mailer, CancellationToken ct) =>
        {
            var g = gate.RequireOperator(http);
            if (!g.Ok) return g.Response!;

            var message = TicketInput.ReadMessageBody(body);
            if (!message.Ok) return Results.BadRequest(new { error = message.Error });

            var ticket = (await TicketsAsync(db, id, null, g.ScopeTo, ct)).FirstOrDefault();
            if (ticket is null) return Results.NotFound(new { error = "Ticket not found." });

            if (!TicketInput.IsKnown(ticket.Status))
            {
                return Results.Json(
                    new { error = "That ticket is in an unknown state." }, statusCode: 409);
            }

            // Only an explicit true. Anything else — absent, "false", 1 — is a
            // public reply, which is the safe way round: a note posted as a
            // reply embarrasses somebody, and a reply posted as a note is never
            // read by the person waiting for it. Neither is good; only one of
            // them is a leak.
            var internalNote = JsonValues.Get(body, "internal") is { ValueKind: JsonValueKind.True };

            var status = await AddMessageAsync(db, ticket.Id, ticket.Status,
                g.Session!.UserId, g.Session.Name, fromStaff: true, internalNote,
                message.Value!, ct);

            // Tell the customer — unless it was a note.
            //
            // Email is a SECOND DOOR out of the room the internal note lives
            // in. Every other guard on that column is on the read path: two
            // readers, a WHERE rather than a filter, the flag not offered on
            // the customer's side. None of them is standing here. A mailer that
            // did not check the column would walk the note straight out to the
            // person it was written about, with no screen involved and nothing
            // to take back.
            //
            // So the check is inside TicketReplyEmail, which returns null for a
            // note — one place, next to the wording, rather than a condition
            // somebody has to remember at this call site.
            var recipient = (await db.Database.SqlQuery<TicketRecipientRow>($"""
                SELECT c."email" AS "Email", c."name" AS "Name"
                FROM "Ticket" t
                JOIN "Client" c ON c."id" = t."clientId"
                WHERE t."id" = {ticket.Id}
                """).ToListAsync(ct)).FirstOrDefault();

            if (recipient is not null)
            {
                await mailer.NotifyAsync(
                    BusinessMail.TicketReplyEmail(
                        new TicketMail(
                            recipient.Email,
                            recipient.Name,
                            ticket.Reference,
                            ticket.Subject,
                            message.Value!,
                            internalNote),
                        BusinessMail.Context()),
                    ct);
            }

            return Results.Json(new { status, @internal = internalNote }, statusCode: 201);
        });
    }

    /* ------------------------------------------------------ the reads --- */

    /// <summary>
    /// One ticket, narrowed to whoever is allowed to see it.
    /// </summary>
    /// <remarks>
    /// <paramref name="clientId"/> narrows to the customer's own;
    /// <paramref name="scope"/> narrows to a salesperson's customers. Both are
    /// conditions in the query rather than checks around it, and passing
    /// neither is the admin's view.
    /// </remarks>
    private static Task<List<TicketRow>> TicketsAsync(
        AutoPartsContext db, string id, string? clientId, string? scope, CancellationToken ct) =>
        db.Database.SqlQuery<TicketRow>($"""
            SELECT t."id" AS "Id", t."reference" AS "Reference", t."subject" AS "Subject",
                   t."status" AS "Status", t."clientId" AS "ClientId",
                   c."name" AS "ClientName", t."orderId" AS "OrderId",
                   o."reference" AS "OrderReference", t."createdAt" AS "CreatedAt",
                   t."lastMessageAt" AS "LastMessageAt",
                   (SELECT COUNT(*) FROM "TicketMessage" m
                     WHERE m."ticketId" = t."id" AND NOT m."internal") AS "MessageCount"
            FROM "Ticket" t
            JOIN "Client" c ON c."id" = t."clientId"
            LEFT JOIN "Order" o ON o."id" = t."orderId"
            WHERE t."id" = {id}
              AND ({clientId} IS NULL OR t."clientId" = {clientId})
              AND ({scope} IS NULL OR c."salesManagerId" = {scope})
            """).ToListAsync(ct);

    /// <summary>
    /// The thread as the CUSTOMER sees it.
    /// </summary>
    /// <remarks>
    /// The internal notes are excluded by the WHERE, not by a filter applied to
    /// the rows afterwards. A filter is one forgotten line away from being no
    /// filter at all, and what it would be forgetting is a note written about
    /// the person reading it.
    /// </remarks>
    private static Task<List<TicketMessageRow>> CustomerThreadAsync(
        AutoPartsContext db, string ticketId, CancellationToken ct) =>
        db.Database.SqlQuery<TicketMessageRow>($"""
            SELECT "id" AS "Id", "authorName" AS "AuthorName", "fromStaff" AS "FromStaff",
                   "internal" AS "Internal", "body" AS "Body", "createdAt" AS "CreatedAt"
            FROM "TicketMessage"
            WHERE "ticketId" = {ticketId} AND NOT "internal"
            ORDER BY "createdAt" ASC
            """).ToListAsync(ct);

    /// <summary>The thread as STAFF see it: everything, notes included.</summary>
    private static Task<List<TicketMessageRow>> StaffThreadAsync(
        AutoPartsContext db, string ticketId, CancellationToken ct) =>
        db.Database.SqlQuery<TicketMessageRow>($"""
            SELECT "id" AS "Id", "authorName" AS "AuthorName", "fromStaff" AS "FromStaff",
                   "internal" AS "Internal", "body" AS "Body", "createdAt" AS "CreatedAt"
            FROM "TicketMessage"
            WHERE "ticketId" = {ticketId}
            ORDER BY "createdAt" ASC
            """).ToListAsync(ct);

    /* ----------------------------------------------------- the writes --- */

    /// <summary>
    /// Raises one, with its first message, in one transaction.
    /// </summary>
    /// <remarks>
    /// A ticket with no message is a subject line nobody can answer, so the two
    /// are written together or not at all. The reference is unique, so a
    /// collision is possible and cheap to retry — the same shape as an order.
    /// </remarks>
    private static async Task<string> CreateAsync(
        AutoPartsContext db, string clientId, string authorName, NewTicket input,
        CancellationToken ct)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var id = Ids.New();
            try
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO "Ticket" ("id", "reference", "clientId", "orderId", "subject", "status")
                    VALUES ({id}, {TicketInput.MakeReference()}, {clientId}, {input.OrderId},
                            {input.Subject}, 'open')
                    """, ct);

                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO "TicketMessage" ("id", "ticketId", "authorId", "authorName",
                                                 "fromStaff", "internal", "body")
                    VALUES ({Ids.New()}, {id}, {clientId}, {authorName}, FALSE, FALSE, {input.Body})
                    """, ct);

                await transaction.CommitAsync(ct);
                return id;
            }
            catch (PostgresException e) when (e.SqlState == UniqueViolation && attempt < 4)
            {
                // A reference collision. Try again with a new one.
            }
        }

        throw new InvalidOperationException("Could not allocate a ticket reference.");
    }

    /// <summary>
    /// Adds a message, and moves the ticket with it.
    /// </summary>
    /// <remarks>
    /// The two travel together: a thread whose last message is newer than its
    /// <c>lastMessageAt</c> is a ticket that has dropped out of the queue it
    /// should be at the top of, and nobody would notice until the customer
    /// telephoned to ask.
    ///
    /// <c>lastMessageAt</c> moves even for an internal note. It is the queue's
    /// sort key and means "when did anything happen here", not "when was the
    /// customer last spoken to".
    /// </remarks>
    private static async Task<string> AddMessageAsync(
        AutoPartsContext db, string ticketId, string currentStatus, string authorId,
        string authorName, bool fromStaff, bool internalNote, string body, CancellationToken ct)
    {
        var status = TicketInput.StatusAfterMessage(currentStatus, fromStaff, internalNote);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "TicketMessage" ("id", "ticketId", "authorId", "authorName",
                                         "fromStaff", "internal", "body")
            VALUES ({Ids.New()}, {ticketId}, {authorId}, {authorName},
                    {fromStaff}, {internalNote}, {body})
            """, ct);

        await db.Database.ExecuteSqlAsync($"""
            UPDATE "Ticket" SET "status" = {status}, "lastMessageAt" = CURRENT_TIMESTAMP
            WHERE "id" = {ticketId}
            """, ct);

        await transaction.CommitAsync(ct);

        return status;
    }

    /* ----------------------------------------------------- serialising --- */

    /// <summary>What the customer is shown. No other account appears on it.</summary>
    private static object Customer(TicketRow t) => new
    {
        id = t.Id,
        reference = t.Reference,
        subject = t.Subject,
        status = t.Status,
        orderId = t.OrderId,
        orderReference = t.OrderReference,
        messageCount = t.MessageCount,
        createdAt = Timestamps.Iso(t.CreatedAt),
        lastMessageAt = Timestamps.Iso(t.LastMessageAt),
    };

    /// <summary>What staff are shown: the same, plus whose ticket it is.</summary>
    private static object Staff(TicketRow t) => new
    {
        id = t.Id,
        reference = t.Reference,
        subject = t.Subject,
        status = t.Status,
        clientId = t.ClientId,
        clientName = t.ClientName,
        orderId = t.OrderId,
        orderReference = t.OrderReference,
        messageCount = t.MessageCount,
        createdAt = Timestamps.Iso(t.CreatedAt),
        lastMessageAt = Timestamps.Iso(t.LastMessageAt),
    };
}

/// <summary>Who to tell that a ticket was answered.</summary>
///
/// <remarks>
/// Read unscoped for the same reason the order recipient is: the scope decides
/// who may REPLY, and by this point somebody has. The customer whose ticket it
/// is gets the message whichever salesperson wrote it.
/// </remarks>
public record TicketRecipientRow(string Email, string Name);

public record TicketRow(
    string Id, string Reference, string Subject, string Status, string ClientId,
    string ClientName, string? OrderId, string? OrderReference,
    DateTime CreatedAt, DateTime LastMessageAt, int MessageCount);

public record TicketMessageRow(
    string Id, string AuthorName, bool FromStaff, bool Internal, string Body, DateTime CreatedAt);
