using System.Text.Json;
using AutoPartsHub.Api.Admin;
using AutoPartsHub.Api.Auth;
using AutoPartsHub.Api.Catalogue;
using AutoPartsHub.Api.Data;
using AutoPartsHub.Domain.Catalogue;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsHub.Api.Endpoints;

/// <summary>
/// Who will sell us a part, and on what terms.
/// </summary>
/// <remarks>
/// ADMIN only, like the price lists and for the same reason: these set what a
/// part costs to buy, which is the number the whole markup engine multiplies
/// up. A salesperson moving their own customers' orders is one thing; changing
/// the cost basis of a part is another.
///
/// Which of the offers actually prices the part is not decided here — it is
/// the <c>BestOffer</c> view, so that the queries which price a row all read
/// one ranking rather than a copy each.
/// </remarks>
public static class AdminOfferEndpoints
{
    public static void MapAdminOfferEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /api/admin/products/<id>/offers
        app.MapGet("/api/admin/products/{id}/offers", async (
            string id, HttpContext http, AdminGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            if (!await db.Products.AnyAsync(p => p.Id == id, ct))
            {
                return Results.NotFound(new { error = "Product not found." });
            }

            return Results.Ok(new { offers = (await OffersAsync(db, id, ct)).Select(Serialise) });
        });

        // PUT /api/admin/products/<id>/offers — replaces them.
        //
        // The whole set together, the same shape as the stock editor: one
        // request takes all of them or none, and a part cannot be left priced
        // from a supplier who was meant to be removed in the same edit.
        //
        // A supplier left out of the list no longer offers the part at all.
        // That is a different thing from `active: false`, which is an offer we
        // are not buying from today — "they do not sell it" and "we do not buy
        // it" both have to be sayable, and only the first is an absence.
        app.MapPut("/api/admin/products/{id}/offers", async (
            string id, JsonElement body, HttpContext http, AdminGate gate,
            AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            if (!await db.Products.AnyAsync(p => p.Id == id, ct))
            {
                return Results.NotFound(new { error = "Product not found." });
            }

            var parsed = OfferInputs.Read(body);
            if (!parsed.Ok) return Results.BadRequest(new { error = parsed.Error });

            var offers = parsed.Value!;
            var ids = offers.Select(o => o.SupplierId).ToArray();
            var known = await db.Suppliers.CountAsync(s => ids.Contains(s.Id), ct);
            if (known != ids.Length)
            {
                return Results.BadRequest(new { error = "One of those suppliers no longer exists." });
            }

            await ReplaceAsync(db, id, offers, ct);

            // Read back rather than echoed: `isBest` is the view's answer, and
            // which offer wins can change as a consequence of this edit in a
            // way the request does not say. Sending back what was submitted
            // would show the admin their own input and call it the result.
            return Results.Ok(new { offers = (await OffersAsync(db, id, ct)).Select(Serialise) });
        });
    }

    /// <summary>
    /// Every offer on one part, best first.
    /// </summary>
    /// <remarks>
    /// <c>IsBest</c> is read from the view rather than worked out here.
    /// Recomputing the ranking would be another copy of it, and this is the one
    /// the admin is looking at while deciding what to change — so it is the one
    /// that most has to agree with what the shop actually charges.
    /// </remarks>
    private static Task<List<OfferRow>> OffersAsync(
        AutoPartsContext db, string productId, CancellationToken ct) =>
        db.Database.SqlQuery<OfferRow>($"""
            SELECT o."supplierId" AS "SupplierId", s."name" AS "SupplierName",
                   s."code" AS "SupplierCode", s."priority" AS "SupplierPriority",
                   s."active" AS "SupplierActive", o."purchasePrice" AS "PurchasePrice",
                   o."stockDays" AS "StockDays", o."supplierPartNumber" AS "SupplierPartNumber",
                   o."active" AS "Active",
                   CASE WHEN bo."supplierId" = o."supplierId" THEN 1 ELSE 0 END AS "IsBest"
            FROM "SupplierOffer" o
            JOIN "Supplier" s ON s."id" = o."supplierId"
            LEFT JOIN "BestOffer" bo ON bo."productId" = o."productId"
            WHERE o."productId" = {productId}
            -- The winning offer first, then the same order BestOffer itself
            -- uses. Two PostgreSQL spellings had to change here and they are
            -- easy to conflate:
            --
            -- `(a = b)` is a boolean VALUE there and can be sorted. T-SQL has
            -- no boolean type at all, so the comparison becomes a CASE
            -- producing a number.
            --
            -- `NULLS LAST` has no equivalent either, and the default differs:
            -- PostgreSQL sorts nulls last under DESC, SQL Server sorts them
            -- first. The CASE removes the question — a part with no best offer
            -- yields 0, not null, so there is nothing left to place.
            ORDER BY CASE WHEN bo."supplierId" = o."supplierId" THEN 1 ELSE 0 END DESC,
                     s."priority" DESC, o."purchasePrice" ASC, s."code" ASC
            """).ToListAsync(ct);

    /// <summary>
    /// Replaces the offers on one part, in one transaction.
    /// </summary>
    /// <remarks>
    /// Deleted-then-inserted rather than merged. The alternative is three
    /// statements — insert the new, update the changed, delete the missing —
    /// and the interesting one is always the third, which is the one a merge
    /// forgets. A part has a handful of suppliers, so replacing the set costs
    /// nothing and cannot half-apply.
    /// </remarks>
    private static async Task ReplaceAsync(
        AutoPartsContext db, string productId, List<OfferInput> offers, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        await db.Database.ExecuteSqlAsync(
            $"""DELETE FROM "SupplierOffer" WHERE "productId" = {productId}""", ct);

        if (offers.Count > 0)
        {
            // One timestamp for the whole write, not one per row. The eight
            // parallel arrays this replaced evaluated DateTime.UtcNow once per
            // offer, so a large save spread its rows across a few milliseconds
            // for no reason — they are one edit and they are stamped as one.
            var savedAt = DateTime.UtcNow;

            var rows = offers.Select(o => new
            {
                id = Ids.New(),
                productId,
                supplierId = o.SupplierId,
                purchasePrice = o.PurchasePrice,
                stockDays = o.StockDays,
                supplierPartNumber = o.SupplierPartNumber,
                active = o.Active,
                updatedAt = savedAt,
            });

            // PostgreSQL zipped eight equal-length arrays back into rows with
            // unnest. SQL Server has no such thing, and the replacement is
            // better than a translation would have been: each value now
            // carries its own name, so the columns cannot be silently
            // misaligned by somebody inserting one in the wrong place — which
            // the positional form could not detect at all.
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "SupplierOffer" ("id", "productId", "supplierId", "purchasePrice",
                                             "stockDays", "supplierPartNumber", "active", "updatedAt")
                SELECT "id", "productId", "supplierId", "purchasePrice",
                       "stockDays", "supplierPartNumber", "active", "updatedAt"
                FROM OPENJSON({SqlList.Rows(rows)})
                WITH (
                  "id" nvarchar(400) '$.id',
                  "productId" nvarchar(400) '$.productId',
                  "supplierId" nvarchar(400) '$.supplierId',
                  "purchasePrice" float '$.purchasePrice',
                  "stockDays" int '$.stockDays',
                  "supplierPartNumber" nvarchar(400) '$.supplierPartNumber',
                  "active" bit '$.active',
                  "updatedAt" datetime2(3) '$.updatedAt'
                )
                """, ct);
        }

        await transaction.CommitAsync(ct);
    }

    private static object Serialise(OfferRow o) => new
    {
        supplierId = o.SupplierId,
        supplierName = o.SupplierName,
        supplierCode = o.SupplierCode,
        supplierPriority = o.SupplierPriority,
        /* A stopped supplier's offer cannot win, whatever it says. */
        supplierActive = o.SupplierActive,
        purchasePrice = o.PurchasePrice,
        stockDays = o.StockDays,
        supplierPartNumber = o.SupplierPartNumber,
        active = o.Active,
        // Whether this is the one the catalogue is actually priced from, read
        // from the view — so the badge on the screen and the figure in the shop
        // cannot disagree.
        isBest = o.IsBest,
    };
}

/// <param name="IsBest">Whether the BestOffer view picked this one.</param>
public record OfferRow(
    string SupplierId, string SupplierName, string SupplierCode, int SupplierPriority,
    bool SupplierActive, double PurchasePrice, int? StockDays, string? SupplierPartNumber,
    bool Active, bool? IsBest);
