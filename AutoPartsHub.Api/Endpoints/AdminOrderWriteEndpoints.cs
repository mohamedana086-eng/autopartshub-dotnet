using System.Text.Json;
using AutoPartsHub.Api.Auth;
using AutoPartsHub.Api.Data;
using AutoPartsHub.Api.Inventory;
using AutoPartsHub.Api.Mail;
using AutoPartsHub.Domain;
using AutoPartsHub.Domain.Orders;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsHub.Api.Endpoints;

/// <summary>
/// Moving an order along its statuses, and the shelves with it.
/// </summary>
/// <remarks>
/// The two travel together for the same reason the order and its reservation
/// do: an order shown as shipped whose stock was never drawn down is the
/// discrepancy a warehouse finds at the next count and cannot explain.
///
/// The rules about which moves are legal, which ones have to say why, and what
/// each does to the shelves all live in <see cref="OrderStatuses"/> — a class
/// with no database in it, so both APIs read the same answers and both can be
/// tested without one. What is here is the part that needs a request: who
/// asked, what the order is now, and what to say when the shelves refuse.
///
/// This is the write that turns a salesperson from a scoped viewer into a
/// scoped operator. The narrowing is a condition inside both the read and the
/// update rather than a check standing in front of them, and neither the
/// status nor the shelves move when it matches nothing.
/// </remarks>
public static class AdminOrderWriteEndpoints
{
    public static void MapAdminOrderWriteEndpoints(this IEndpointRouteBuilder app)
    {
        // PATCH /api/admin/orders/<id> { status, reason?, trackingNumber?, carrier? }
        //
        // The move named in the body. It is the shape the storefront calls
        // today and it stays; the four routes below are the shape the backlog
        // specifies, and all five go through one MoveAsync.
        app.MapPatch("/api/admin/orders/{id}", async (
            string id, JsonElement body, HttpContext http, AdminGate gate,
            AutoPartsContext db, Mailer mailer, CancellationToken ct) =>
        {
            var g = gate.RequireOperator(http);
            if (!g.Ok) return g.Response!;

            return await MoveAsync(id, g.ScopeTo, g.Session!.UserId, db, mailer, ct,
                from => OrderStatuses.ReadStatusChange(body, from));
        });

        // POST /api/admin/orders/<id>/approve
        // POST /api/admin/orders/<id>/reject  { reason }
        // POST /api/admin/orders/<id>/cancel  { reason }
        //
        // The same three moves with the destination in the path instead of the
        // body. Nothing about them is looser for it: the status still has to be
        // reachable from where the order is, and reject and cancel still have
        // to say why — ReadStatusChange decides both, from one place, whether
        // it read the destination or was handed it.
        //
        // The body is optional because approve has nothing to say. A POST with
        // no body would otherwise be refused before any of this ran, which is
        // a 415 for a request that is completely correct.
        //
        // Written out three times rather than looped, and each one naming its
        // own gate rather than letting MoveAsync name it once. Both are for
        // AdminWriteScopeTests, which reads these files as text and asserts
        // that every admin route names a guard: a looped `$"…{verb}"` route is
        // invisible to it, and a gate one call away is unreadable to it. A
        // guard it cannot see is a guard nothing is holding in place, and
        // three new admin WRITES are not the place to find that out.
        app.MapPost("/api/admin/orders/{id}/approve", async (
            string id, JsonElement? body, HttpContext http, AdminGate gate,
            AutoPartsContext db, Mailer mailer, CancellationToken ct) =>
        {
            var g = gate.RequireOperator(http);
            if (!g.Ok) return g.Response!;

            return await MoveAsync(id, g.ScopeTo, g.Session!.UserId, db, mailer, ct,
                from => OrderStatuses.ReadStatusChange(Sent(body), from, OrderStatuses.Accepted));
        });

        app.MapPost("/api/admin/orders/{id}/reject", async (
            string id, JsonElement? body, HttpContext http, AdminGate gate,
            AutoPartsContext db, Mailer mailer, CancellationToken ct) =>
        {
            var g = gate.RequireOperator(http);
            if (!g.Ok) return g.Response!;

            return await MoveAsync(id, g.ScopeTo, g.Session!.UserId, db, mailer, ct,
                from => OrderStatuses.ReadStatusChange(Sent(body), from, OrderStatuses.Rejected));
        });

        app.MapPost("/api/admin/orders/{id}/cancel", async (
            string id, JsonElement? body, HttpContext http, AdminGate gate,
            AutoPartsContext db, Mailer mailer, CancellationToken ct) =>
        {
            var g = gate.RequireOperator(http);
            if (!g.Ok) return g.Response!;

            return await MoveAsync(id, g.ScopeTo, g.Session!.UserId, db, mailer, ct,
                from => OrderStatuses.ReadStatusChange(Sent(body), from, OrderStatuses.Cancelled));
        });

        MapShipping(app);
    }

    /// <summary>The body, or an empty one when the caller sent none.</summary>
    private static JsonElement Sent(JsonElement? body) =>
        body ?? JsonDocument.Parse("{}").RootElement;

    /// <summary>
    /// PATCH /api/admin/orders/&lt;id&gt;/shipping { trackingNumber?, carrier? }
    /// </summary>
    /// <remarks>
    /// Correcting where a shipment is, without moving the order. The carrier
    /// reissued the number, or somebody typed it wrong — and the order is
    /// still exactly as shipped as it was.
    ///
    /// NOT a status change, which is the whole reason it is its own route.
    /// Doing it through the move would write <c>statusChangedAt</c> and
    /// <c>statusChangedById</c> for a change that did not happen, so the desk
    /// would show somebody moving an order at a time they did not.
    ///
    /// NO EMAIL either. The customer was told when it shipped; a second
    /// message with the same subject and a different number reads as a second
    /// shipment. Correcting a number they have already been given is worth
    /// telling them about, but as its own message with its own wording, and
    /// that is a thing to write rather than a flag to pass here.
    ///
    /// The shelves do not move. Nothing about where a parcel is changes what
    /// is on them.
    /// </remarks>
    private static void MapShipping(IEndpointRouteBuilder app) =>
        app.MapPatch("/api/admin/orders/{id}/shipping", async (
            string id, JsonElement body, HttpContext http, AdminGate gate,
            AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireOperator(http);
            if (!g.Ok) return g.Response!;

            var scope = g.ScopeTo;

            // Scoped, and read before the write for the same reason the move
            // is: the refusals are worded from it, and an unscoped read would
            // let a salesperson learn another manager's order status from the
            // sentence explaining why they could not touch it.
            var existing = (await db.Database.SqlQuery<OrderStatusOnlyRow>($"""
                SELECT o."status" AS "Status"
                FROM "Order" o
                JOIN "Client" c ON c."id" = o."clientId"
                WHERE o."id" = {id}
                  AND ({scope} IS NULL OR c."salesManagerId" = {scope})
                """).ToListAsync(ct)).FirstOrDefault();

            if (existing is null) return Results.NotFound(new { error = "Order not found." });

            var change = OrderStatuses.ReadShippingChange(body, existing.Status);
            if (!change.Ok) return Results.BadRequest(new { error = change.Error });

            var to = change.Value!;

            // The same CASE the move uses: a field the caller did not send
            // keeps what is already there, so correcting the carrier alone
            // does not wipe the number beside it.
            var updated = (await db.Database.SqlQuery<OrderStatusRow>($"""
                UPDATE "Order"
                   SET "trackingNumber" = CASE WHEN {to.TrackingNumber is not null} = 1
                                               THEN {to.TrackingNumber}
                                               ELSE "trackingNumber" END,
                       "carrier" = CASE WHEN {to.Carrier is not null} = 1
                                        THEN {to.Carrier}
                                        ELSE "carrier" END
                OUTPUT INSERTED."id" AS "Id", INSERTED."status" AS "Status",
                       INSERTED."statusReason" AS "StatusReason",
                       INSERTED."statusChangedAt" AS "StatusChangedAt",
                       INSERTED."trackingNumber" AS "TrackingNumber",
                       INSERTED."carrier" AS "Carrier"
                 WHERE "id" = {id}
                   AND ({scope} IS NULL OR EXISTS (
                         SELECT 1 FROM "Client" c
                          WHERE c."id" = "Order"."clientId"
                            AND c."salesManagerId" = {scope}
                       ))
                """).ToListAsync(ct)).FirstOrDefault();

            // The order changed hands between the read and the write, so it is
            // no longer theirs to touch.
            if (updated is null) return Results.NotFound(new { error = "Order not found." });

            // The same shape the move answers with, so a screen can read one
            // response type from either.
            return Results.Ok(new
            {
                id,
                status = updated.Status,
                statusReason = updated.StatusReason,
                statusChangedAt = updated.StatusChangedAt is null
                    ? null
                    : Timestamps.Iso(updated.StatusChangedAt.Value),
                trackingNumber = updated.TrackingNumber,
                carrier = updated.Carrier,
            });
        });

    /// <summary>
    /// Moves one order, whichever route asked.
    /// </summary>
    /// <param name="scope">
    /// The sales manager whose customers the caller may touch, or null for an
    /// admin who may touch any. Passed in rather than read from the gate here,
    /// so that every route naming <c>RequireOperator</c> also names the scope
    /// it narrows by — which is what AdminWriteScopeTests asserts, and what
    /// stops a write being opened to sales without one.
    /// </param>
    /// <param name="change">
    /// What to do, given where the order actually is. A function rather than a
    /// value because the current status is not known until it has been read
    /// through the caller's scope, and it is half of every rule about whether
    /// the move is allowed.
    /// </param>
    private static async Task<IResult> MoveAsync(
        string id, string? scope, string byId, AutoPartsContext db, Mailer mailer,
        CancellationToken ct, Func<string, Validated<StatusChange>> change)
    {
        {

            // The scope is on the read as well as on the write that follows,
            // because the read is what the refusals are worded from: an
            // unscoped read here would let a salesperson learn another
            // manager's order status from the sentence explaining why they
            // could not change it.
            var existing = (await db.Database.SqlQuery<OrderStatusOnlyRow>($"""
                SELECT o."status" AS "Status"
                FROM "Order" o
                JOIN "Client" c ON c."id" = o."clientId"
                WHERE o."id" = {id}
                  AND ({scope} IS NULL OR c."salesManagerId" = {scope})
                """).ToListAsync(ct)).FirstOrDefault();

            // Not theirs, or not there. Both answer the same way: to a
            // salesperson another manager's order does not exist, and a
            // distinct refusal would confirm that it does.
            if (existing is null) return Results.NotFound(new { error = "Order not found." });

            if (!OrderStatuses.IsKnown(existing.Status))
            {
                // A status the vocabulary does not know. The CHECK added with
                // the lifecycle makes this unreachable for anything written
                // since, and it is still worth saying rather than crashing on
                // a row written before it.
                return Results.Json(
                    new
                    {
                        error = $"This order is in an unknown status ({existing.Status}) "
                              + "and cannot be moved.",
                    },
                    statusCode: 409);
            }

            var requested = change(existing.Status);
            if (!requested.Ok) return Results.BadRequest(new { error = requested.Error });

            var to = requested.Value!;
            var shelf = OrderStatuses.ShelfChangeFor(existing.Status, to.Status);

            OrderStatusRow? updated;

            try
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                // The tracking fields go through a CASE on whether the caller
                // sent them, the way the price-list update writes its own, so
                // re-saving `shipped` without a number does not wipe the number
                // already there.
                updated = (await db.Database.SqlQuery<OrderStatusRow>($"""
                    UPDATE "Order"
                       SET "status" = {to.Status},
                           "statusReason" = {to.Reason},
                           "statusChangedAt" = SYSUTCDATETIME(),
                           "statusChangedById" = {byId},
                           "trackingNumber" = CASE WHEN {to.TrackingNumber is not null} = 1
                                                   THEN {to.TrackingNumber}
                                                   ELSE "trackingNumber" END,
                           "carrier" = CASE WHEN {to.Carrier is not null} = 1
                                            THEN {to.Carrier}
                                            ELSE "carrier" END
                    -- PostgreSQL's RETURNING, which sits at the end. SQL
                    -- Server's OUTPUT says the same thing and sits here,
                    -- before the WHERE — INSERTED is the row as it now stands,
                    -- which is what RETURNING gives on an UPDATE too.
                    OUTPUT INSERTED."id" AS "Id", INSERTED."status" AS "Status",
                           INSERTED."statusReason" AS "StatusReason",
                           INSERTED."statusChangedAt" AS "StatusChangedAt",
                           INSERTED."trackingNumber" AS "TrackingNumber",
                           INSERTED."carrier" AS "Carrier"
                     WHERE "id" = {id}
                       AND ({scope} IS NULL OR EXISTS (
                             SELECT 1 FROM "Client" c
                              WHERE c."id" = "Order"."clientId"
                                AND c."salesManagerId" = {scope}
                           ))
                    """).ToListAsync(ct)).FirstOrDefault();

                // Nothing matched: the order changed hands between the scoped
                // read above and this write, so it is no longer theirs to
                // move. The shelves must not move either — adjusting stock for
                // an order this caller was not allowed to touch would be the
                // leak doing damage on its way out.
                if (updated is null)
                {
                    await transaction.RollbackAsync(ct);
                    return Results.NotFound(new { error = "Order not found." });
                }

                await StockMovements.ApplyShelfChangeAsync(db, id, shelf, ct);

                await transaction.CommitAsync(ct);
            }
            catch (Exception e) when (e.Is(DatabaseRefusal.Check))
            {
                // The CHECK on StockLevel refusing a negative count: the
                // shelves and the orders holding them disagree, so releasing
                // this one would drive reserved below zero. No amount of
                // retrying fixes it and the admin cannot diagnose it from a
                // stack trace. The reconciliation tool reports and repairs it.
                return Results.Json(
                    new
                    {
                        error = "This order holds more stock than its warehouses have reserved, so it "
                              + "cannot be released. The status has not changed. Run the stock "
                              + "reconciliation to repair it.",
                    },
                    statusCode: 409);
            }

            // Tell the customer, for the four statuses worth telling them about.
            //
            // After the write and outside the transaction, and NotifyAsync
            // never throws: the order HAS moved, the row is committed, and an
            // admin who pressed "accept" must not get an error page for
            // something that worked — they would press it again.
            //
            // Which statuses are worth a message, and the wording, are both in
            // BusinessMail rather than here. Most of what happens to an order is
            // not news, and mail about things that are not news is mail nobody
            // reads when it is.
            var recipient = (await db.Database.SqlQuery<OrderRecipientRow>($"""
                SELECT c."email" AS "Email", c."name" AS "Name", o."reference" AS "Reference"
                FROM "Order" o
                JOIN "Client" c ON c."id" = o."clientId"
                WHERE o."id" = {id}
                """).ToListAsync(ct)).FirstOrDefault();

            if (recipient is not null)
            {
                await mailer.NotifyAsync(
                    BusinessMail.OrderStatusEmail(
                        new OrderMail(
                            recipient.Email,
                            recipient.Name,
                            recipient.Reference,
                            updated!.Status,
                            updated.StatusReason,
                            updated.TrackingNumber,
                            updated.Carrier),
                        BusinessMail.Context()),
                    ct);
            }

            return Results.Ok(new
            {
                id,
                status = updated!.Status,
                statusReason = updated.StatusReason,
                statusChangedAt = updated.StatusChangedAt is null
                    ? null
                    : Timestamps.Iso(updated.StatusChangedAt.Value),
                trackingNumber = updated.TrackingNumber,
                carrier = updated.Carrier,
            });
        }
    }
}

/// <summary>An order's status columns, as the update hands them back.</summary>
public record OrderStatusRow(
    string Id, string Status, string? StatusReason, DateTime? StatusChangedAt,
    string? TrackingNumber, string? Carrier);

/// <summary>Where an order is now, read through whatever scope the caller has.</summary>
public record OrderStatusOnlyRow(string Status);

/// <summary>
/// Who to tell that an order moved, and what to call it.
/// </summary>
/// <remarks>
/// Read unscoped, deliberately: the scope decides who may MOVE an order, and
/// by this point one has been moved. The customer whose order it is gets the
/// message whichever salesperson pressed the button.
/// </remarks>
public record OrderRecipientRow(string Email, string Name, string Reference);
