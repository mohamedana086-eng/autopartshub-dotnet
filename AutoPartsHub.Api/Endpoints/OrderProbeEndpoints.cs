using AutoPartsHub.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsHub.Api.Endpoints;

/// <summary>
/// Removes an order and gives back the stock it was holding.
/// </summary>
/// <remarks>
/// DEVELOPMENT ONLY — see the guard at the call site in Program.cs. Deleting a
/// sale is not something any caller should be able to ask for: an order is a
/// record of what somebody bought, and the product delete in the admin refuses
/// for exactly that reason.
///
/// It exists so a test can place a real order and not leave it behind. The
/// order it deletes is one a test script created, on a part the same script
/// created.
///
/// Releasing the reserve is the part worth being careful about. Deleting the
/// allocation rows without lowering StockLevel.reserved would leave the shelf
/// promising units to an order that no longer exists — the same drift the
/// reconcile tool was written to find, introduced by the tool meant to clean up.
/// </remarks>
public static class OrderProbeEndpoints
{
    public static void MapOrderProbe(this IEndpointRouteBuilder app)
    {
        app.MapPost("/dev/forget-order", async (
            ForgetOrderRequest body, AutoPartsContext db, CancellationToken ct) =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            // Give the units back first, while the allocations still say how
            // many and where. Only reserved moves: the goods never left, so
            // quantity was never touched when the order was placed.
            var released = await db.Database.ExecuteSqlAsync($"""
                -- PostgreSQL names the table being updated and lists the joined
                -- ones in FROM. T-SQL names the ALIAS and puts the target into
                -- the FROM with the rest, which also means the conditions
                -- linking it to them become join conditions rather than sitting
                -- in the WHERE.
                UPDATE s
                SET "reserved" = s."reserved" - a."quantity",
                    "updatedAt" = SYSUTCDATETIME()
                FROM "StockLevel" s
                JOIN "OrderItemAllocation" a ON a."warehouseId" = s."warehouseId"
                JOIN "OrderItem" i ON i."id" = a."orderItemId"
                                  AND i."productId" = s."productId"
                WHERE i."orderId" = {body.OrderId}
                """, ct);

            var allocations = await db.Database.ExecuteSqlAsync($"""
                -- PostgreSQL's USING is T-SQL's second FROM: the alias goes
                -- after DELETE to say which of the joined tables loses rows.
                DELETE a
                FROM "OrderItemAllocation" a
                JOIN "OrderItem" i ON i."id" = a."orderItemId"
                WHERE i."orderId" = {body.OrderId}
                """, ct);

            var lines = await db.Database.ExecuteSqlAsync(
                $"""DELETE FROM "OrderItem" WHERE "orderId" = {body.OrderId}""", ct);

            var orders = await db.Database.ExecuteSqlAsync(
                $"""DELETE FROM "Order" WHERE "id" = {body.OrderId}""", ct);

            await transaction.CommitAsync(ct);

            return Results.Ok(new { released, allocations, lines, orders });
        });
    }
}

public record ForgetOrderRequest(string OrderId);
