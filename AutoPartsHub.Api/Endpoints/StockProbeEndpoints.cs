using AutoPartsHub.Api.Data;
using AutoPartsHub.Api.Inventory;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsHub.Api.Endpoints;

/// <summary>
/// Drives stock reservation directly, so the locking can be raced.
/// </summary>
/// <remarks>
/// DEVELOPMENT ONLY — see the guard at the call site in Program.cs. It holds
/// stock without an order behind it, which is not a thing any caller should be
/// able to ask for.
///
/// It exists because the interesting property of <c>ReserveAsync</c> is not
/// visible in a single call. One request at a time proves arithmetic; the
/// question is whether two requests for the same last unit can both be told
/// yes, and the only way to ask is to send them at once.
/// </remarks>
public static class StockProbeEndpoints
{
    public static void MapStockProbe(this IEndpointRouteBuilder app)
    {
        // Reserves inside a transaction, then rolls back or commits as asked.
        // Rolling back is the default so a race can be run repeatedly against
        // the same shelf without draining it.
        app.MapPost("/dev/reserve", async (
            ReserveProbeRequest body, AutoPartsContext db, CancellationToken ct) =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            var result = await StockMovements.ReserveAsync(
                db, [new StockNeed(body.ProductId, body.Quantity)], ct);

            // Held briefly on purpose: a race in which each side finishes
            // before the other starts is not a race.
            if (body.HoldMs > 0) await Task.Delay(body.HoldMs, ct);

            if (body.Commit && result.Ok) await transaction.CommitAsync(ct);
            else await transaction.RollbackAsync(ct);

            return Results.Ok(new
            {
                ok = result.Ok,
                allocations = result.Allocations,
                shortfall = result.Shortfall,
                committed = body.Commit && result.Ok,
            });
        });

        // The same call with no transaction open, to show the guard fires
        // rather than quietly reserving without a lock.
        app.MapPost("/dev/reserve-unsafe", async (
            ReserveProbeRequest body, AutoPartsContext db, CancellationToken ct) =>
        {
            try
            {
                await StockMovements.ReserveAsync(db, [new StockNeed(body.ProductId, body.Quantity)], ct);
                return Results.Ok(new { guarded = false, note = "reserved with no transaction — the guard did not fire" });
            }
            catch (InvalidOperationException e)
            {
                return Results.Ok(new { guarded = true, message = e.Message });
            }
        });
    }
}

public record ReserveProbeRequest(string ProductId, int Quantity, bool Commit = false, int HoldMs = 0);
