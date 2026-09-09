using System.Text.Json;
using AutoPartsHub.Api.Auth;
using AutoPartsHub.Api.Data;
using AutoPartsHub.Api.Inventory;
using AutoPartsHub.Api.Mail;
using AutoPartsHub.Domain.Orders;
using Microsoft.EntityFrameworkCore;
using Npgsql;

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
        app.MapPatch("/api/admin/orders/{id}", async (
            string id, JsonElement body, HttpContext http, AdminGate gate,
            AutoPartsContext db, Mailer mailer, CancellationToken ct) =>
        {
            var g = gate.RequireOperator(http);
            if (!g.Ok) return g.Response!;

            var scope = g.ScopeTo;

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
                  AND ({scope}::text IS NULL OR c."salesManagerId" = {scope})
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

            var change = OrderStatuses.ReadStatusChange(body, existing.Status);
            if (!change.Ok) return Results.BadRequest(new { error = change.Error });

            var to = change.Value!;
            var shelf = OrderStatuses.ShelfChangeFor(existing.Status, to.Status);
            var byId = g.Session!.UserId;

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
                           "statusChangedAt" = CURRENT_TIMESTAMP,
                           "statusChangedById" = {byId},
                           "trackingNumber" = CASE WHEN {to.TrackingNumber is not null}
                                                   THEN {to.TrackingNumber}::text
                                                   ELSE "trackingNumber" END,
                           "carrier" = CASE WHEN {to.Carrier is not null}
                                            THEN {to.Carrier}::text
                                            ELSE "carrier" END
                     WHERE "id" = {id}
                       AND ({scope}::text IS NULL OR EXISTS (
                             SELECT 1 FROM "Client" c
                              WHERE c."id" = "Order"."clientId"
                                AND c."salesManagerId" = {scope}
                           ))
                    RETURNING "id" AS "Id", "status" AS "Status",
                              "statusReason" AS "StatusReason",
                              "statusChangedAt" AS "StatusChangedAt",
                              "trackingNumber" AS "TrackingNumber", "carrier" AS "Carrier"
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
            catch (PostgresException e) when (e.SqlState == CheckViolation)
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
        });
    }

    private const string CheckViolation = "23514";
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
