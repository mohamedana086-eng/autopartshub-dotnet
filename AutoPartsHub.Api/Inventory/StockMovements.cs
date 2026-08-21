using AutoPartsHub.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsHub.Api.Inventory;

/// <summary>
/// Moving stock as orders move.
/// </summary>
/// <remarks>
/// Two facts, kept apart, as StockLevel describes them: <c>quantity</c> is what
/// is on the shelf and <c>reserved</c> is what is promised. Placing an order
/// raises <c>reserved</c> and leaves <c>quantity</c> alone — the goods have not
/// gone anywhere yet. Shipping lowers both, because that is the moment they
/// leave.
///
/// The rule that decides everything else here: stock is the authority. A part
/// with no counted stock has none to sell, and an order for it is refused the
/// same way an order for a counted part that has run out is refused.
///
/// This is the one part of the port that LINQ cannot express. <c>FOR UPDATE</c>
/// has no translation, and it is not decoration — it is the whole mechanism.
/// So these are raw statements, run against the caller's transaction.
/// </remarks>
public static class StockMovements
{
    /// <summary>
    /// Holds stock for an order, inside a transaction the caller has already
    /// opened.
    /// </summary>
    /// <remarks>
    /// The transaction is not a detail: the row locks below last exactly as
    /// long as it does. Run these statements outside one and each commits on
    /// its own, the locks lift immediately, and two customers can be sold the
    /// same last unit — which is what this exists to prevent.
    ///
    /// Unlike the TypeScript original, which takes a transaction-bound handle
    /// and so cannot be called wrongly, this takes the context and checks. EF
    /// has no type that means "inside a transaction", so the guarantee has to
    /// be asserted instead of carried.
    ///
    /// Warehouses are drawn in <c>priority</c> order, highest first, which is
    /// what the column is for. A line that one site cannot fill alone is split
    /// across the next ones rather than refused.
    /// </remarks>
    public static async Task<ReserveResult> ReserveAsync(
        AutoPartsContext db, IReadOnlyList<StockNeed> needs, CancellationToken ct = default)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "ReserveAsync must run inside a transaction: the FOR UPDATE locks it takes " +
                "last only as long as one, and without it two orders can be sold the same unit.");
        }

        var allocations = new List<Allocation>();

        foreach (var need in needs)
        {
            // FOR UPDATE OF s locks the stock rows for the rest of the
            // transaction, so the availability read cannot go stale between
            // here and the write. Only StockLevel is locked; the warehouse
            // rows are read, not claimed.
            var rows = await db.Database
                .SqlQuery<AvailableRow>($"""
                    SELECT s."id" AS "Id",
                           s."warehouseId" AS "WarehouseId",
                           s."quantity" - s."reserved" AS "Available"
                    FROM "StockLevel" s
                    JOIN "Warehouse" w ON w."id" = s."warehouseId"
                    WHERE s."productId" = {need.ProductId}
                      AND w."active" = true
                    ORDER BY w."priority" DESC, w."code" ASC
                    FOR UPDATE OF s
                    """)
                .ToListAsync(ct);

            // No rows means nothing counted, which sums to nothing available —
            // the shortfall below refuses it without needing a case of its own.
            var available = rows.Sum(r => r.Available);
            if (available < need.Quantity)
            {
                return ReserveResult.Short(new Shortfall(need.ProductId, need.Quantity, available));
            }

            var outstanding = need.Quantity;
            foreach (var row in rows)
            {
                if (outstanding == 0) break;

                var take = Math.Min(outstanding, row.Available);
                if (take <= 0) continue;

                await db.Database.ExecuteSqlAsync(
                    $"""UPDATE "StockLevel" SET "reserved" = "reserved" + {take} WHERE "id" = {row.Id}""",
                    ct);

                allocations.Add(new Allocation(need.ProductId, row.WarehouseId, take));
                outstanding -= take;
            }
        }

        return ReserveResult.Held(allocations);
    }

    /// <summary>
    /// Applies a change of order status to the shelves it drew on.
    /// </summary>
    /// <remarks>
    /// Shipping is the moment goods leave: both <c>quantity</c> and
    /// <c>reserved</c> come down by what was held, which keeps the promise and
    /// the shelf consistent — dropping only one would leave either phantom
    /// stock or a permanent promise against it.
    ///
    /// Reversing a status set by mistake puts both back. Both move together in
    /// either direction, so <c>reserved &lt;= quantity</c> holds throughout and
    /// the check constraint never has to catch us.
    ///
    /// Idempotent by construction: driven by whether the order crossed into or
    /// out of shipped, not by what it was set to, so saving shipped twice
    /// moves nothing the second time.
    /// </remarks>
    public static async Task ApplyShipmentChangeAsync(
        AutoPartsContext db, string orderId, bool wasShipped, bool isShipped, CancellationToken ct = default)
    {
        if (wasShipped == isShipped) return;

        // Signed once rather than per statement: leaving is negative, coming
        // back is positive, and both columns move by the same amount either
        // way so reserved <= quantity survives the trip.
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

    /// <summary>A row of the availability read. Names match the aliases above.</summary>
    private record AvailableRow(string Id, string WarehouseId, int Available);
}

/// <summary>What one order line needs.</summary>
public record StockNeed(string ProductId, int Quantity);

/// <summary>Where one line's stock was found.</summary>
public record Allocation(string ProductId, string WarehouseId, int Quantity);

public record Shortfall(string ProductId, int Wanted, int Available);

public record ReserveResult(bool Ok, List<Allocation> Allocations, Shortfall? Shortfall)
{
    public static ReserveResult Held(List<Allocation> allocations) => new(true, allocations, null);
    public static ReserveResult Short(Shortfall shortfall) => new(false, [], shortfall);
}
