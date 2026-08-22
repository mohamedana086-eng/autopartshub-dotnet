using System.Text.Json;
using AutoPartsHub.Api.Auth;
using AutoPartsHub.Api.Data;
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
/// </remarks>
public static class AdminOrderWriteEndpoints
{
    /// <summary>The statuses the schema documents on Order.status.</summary>
    private static readonly string[] OrderStatuses = ["order_is_sent", "processing", "shipped", "paid"];

    /// <summary>
    /// Which statuses mean the goods have left the building.
    /// </summary>
    /// <remarks>
    /// <c>paid</c> counts: an order is not marked paid before it is fulfilled,
    /// and treating it as still-on-the-shelf would put the units back the
    /// moment the invoice was settled. Kept as a set rather than a
    /// <c>== "shipped"</c> test so the question has one answer both here and
    /// in a year.
    /// </remarks>
    private static readonly HashSet<string> Gone = ["shipped", "paid"];

    private static bool HasLeft(string status) => Gone.Contains(status);

    public static void MapAdminOrderWriteEndpoints(this IEndpointRouteBuilder app)
    {
        // PATCH /api/admin/orders/<id> { status }
        app.MapPatch("/api/admin/orders/{id}", async (
            string id, JsonElement body, HttpContext http, AdminGate gate,
            AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            var status = JsonValues.AsString(JsonValues.Get(body, "status"));
            if (!OrderStatuses.Contains(status))
            {
                return Results.BadRequest(new
                {
                    error = $"Status must be one of: {string.Join(", ", OrderStatuses)}.",
                });
            }

            var existing = await db.Orders.Where(o => o.Id == id)
                .Select(o => new { o.Status }).AsNoTracking().FirstOrDefaultAsync(ct);
            if (existing is null) return Results.NotFound(new { error = "Order not found." });

            try
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                await db.Database.ExecuteSqlAsync(
                    $"""UPDATE "Order" SET "status" = {status} WHERE "id" = {id}""", ct);

                await ApplyShipmentChangeAsync(db, id, HasLeft(existing.Status), HasLeft(status), ct);

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

            return Results.Ok(new { id, status });
        });
    }

    private const string CheckViolation = "23514";

    /// <summary>
    /// Draws the order's units off the shelves, or puts them back.
    /// </summary>
    /// <remarks>
    /// Signed once rather than per statement: leaving is negative, coming back
    /// is positive, and both columns move by the same amount either way so
    /// <c>reserved &lt;= quantity</c> survives the trip.
    /// </remarks>
    private static async Task ApplyShipmentChangeAsync(
        AutoPartsContext db, string orderId, bool wasShipped, bool isShipped, CancellationToken ct)
    {
        if (wasShipped == isShipped) return;

        var direction = isShipped ? -1 : 1;

        await db.Database.ExecuteSqlAsync($"""
            UPDATE "StockLevel" s
            SET "quantity" = s."quantity" + (a."quantity" * {direction}),
                "reserved" = s."reserved" + (a."quantity" * {direction}),
                "updatedAt" = now()
            FROM "OrderItemAllocation" a
            JOIN "OrderItem" i ON i."id" = a."orderItemId"
            WHERE i."orderId" = {orderId}
              AND s."productId" = i."productId"
              AND s."warehouseId" = a."warehouseId"
            """, ct);
    }
}
