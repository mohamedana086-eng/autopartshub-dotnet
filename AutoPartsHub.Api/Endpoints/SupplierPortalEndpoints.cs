using AutoPartsHub.Api.Auth;
using AutoPartsHub.Api.Catalogue;
using AutoPartsHub.Api.Data;
using AutoPartsHub.Domain.Catalogue;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsHub.Api.Endpoints;

/// <summary>
/// What a supplier can see of their own trade.
/// </summary>
/// <remarks>
/// Its own branch rather than a corner of <c>/api/admin</c>, because a supplier
/// is not staff with fewer permissions — they are a counterparty. Putting their
/// endpoints beside the admin ones would mean every future admin route sat one
/// forgotten guard away from being theirs too.
///
/// Everything here is narrowed to one supplier inside the query, and the
/// supplier comes from the GATE rather than from the request: a supplier id
/// read off a query string is the one edit that would turn this into somebody
/// else's portal.
///
/// WHAT IS DELIBERATELY NOT SENT
/// -----------------------------
/// <b>Who bought it.</b> Not the name, not the id, not the city, not the
/// email. Our customer list is the business; a supplier who can read it can
/// sell to it directly next quarter.
///
/// <b>What we sold it for.</b> <c>OrderItem.unitPrice</c> is the resolved price
/// the customer paid, and it embeds the markup — a supplier who can see it can
/// compute exactly what we make on their parts and open the next negotiation
/// holding that number. It is the single most tempting field on the row and it
/// does not leave.
///
/// What is left is what they actually need: which of their parts are moving,
/// how many, when, and what state the order is in.
///
/// The order REFERENCE does travel. It is the handle a conversation about one
/// shipment needs — "the forty units on the third" is not actionable without it
/// — and it names nobody on its own. That is a judgement rather than a
/// certainty: a supplier who already knows who buys a rare part could pair it
/// with a date. The alternative is a portal nobody can hold a conversation
/// about, which is worse for a real trading relationship.
/// </remarks>
public static class SupplierPortalEndpoints
{
    /// <summary>
    /// Which statuses count as outstanding, from the supplier's side.
    /// </summary>
    /// <remarks>
    /// Named here rather than reaching for the lifecycle, because this is a
    /// question about what a SUPPLIER should count as on order and it wants to
    /// be able to answer differently from what the warehouse counts as
    /// reserved. Today the two lists agree, and the day they stop agreeing this
    /// is where it will be visible.
    /// </remarks>
    private static readonly string[] Open = ["order_is_sent", "accepted", "processing"];

    public static void MapSupplierPortalEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /api/supplier/summary — the four figures the portal opens on.
        app.MapGet("/api/supplier/summary", async (
            HttpContext http, SupplierGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = await gate.Require(http, ct);
            if (!g.Ok) return g.Response!;

            var supplierId = g.SupplierId;

            var summary = (await db.Database.SqlQuery<SupplierSummaryRow>($"""
                SELECT
                  (SELECT COUNT(*) FROM "Product" p WHERE p."supplierId" = {supplierId})
                    AS "Parts",
                  (SELECT COUNT(*) FROM "Product" p
                    WHERE p."supplierId" = {supplierId}
                      AND COALESCE((SELECT SUM(s."quantity") - SUM(s."reserved")
                                      FROM "StockLevel" s WHERE s."productId" = p."id"), 0) <= 0
                      AND EXISTS (SELECT 1 FROM "StockLevel" s WHERE s."productId" = p."id")
                  ) AS "OutOfStock",
                  (SELECT COUNT(*) FROM "Product" p
                    WHERE p."supplierId" = {supplierId}
                      AND NOT EXISTS (SELECT 1 FROM "StockLevel" s WHERE s."productId" = p."id")
                  ) AS "Uncounted",
                  (SELECT COUNT(*) FROM "OrderItem" i
                     JOIN "Product" p ON p."id" = i."productId"
                     JOIN "Order" o ON o."id" = i."orderId"
                    WHERE p."supplierId" = {supplierId} AND o."status" = ANY({Open}::text[])
                  ) AS "OpenLines",
                  (SELECT COALESCE(SUM(i."quantity"), 0) FROM "OrderItem" i
                     JOIN "Product" p ON p."id" = i."productId"
                     JOIN "Order" o ON o."id" = i."orderId"
                    WHERE p."supplierId" = {supplierId} AND o."status" = ANY({Open}::text[])
                  ) AS "OpenUnits"
                """).ToListAsync(ct)).Single();

            return Results.Ok(new
            {
                supplier = g.SupplierName,
                parts = summary.Parts,
                outOfStock = summary.OutOfStock,
                uncounted = summary.Uncounted,
                openLines = summary.OpenLines,
                openUnits = summary.OpenUnits,
            });
        });

        // GET /api/supplier/orders?page=&pageSize= — their parts, on other
        // people's orders. Two fields are missing from every row on purpose.
        app.MapGet("/api/supplier/orders", async (
            HttpContext http, SupplierGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = await gate.Require(http, ct);
            if (!g.Ok) return g.Response!;

            var supplierId = g.SupplierId;
            var page = Paging.ReadPage(http.Request.Query["page"]);
            var pageSize = Paging.ReadPageSize(
                http.Request.Query["pageSize"], http.Request.Query["limit"]);

            var lines = await db.Database.SqlQuery<SupplierOrderLineRow>($"""
                SELECT o."reference" AS "Reference", o."createdAt" AS "PlacedAt",
                       o."status" AS "Status", p."partNumber" AS "PartNumber",
                       p."name" AS "Name", i."quantity" AS "Quantity"
                FROM "OrderItem" i
                JOIN "Product" p ON p."id" = i."productId"
                JOIN "Order" o ON o."id" = i."orderId"
                WHERE p."supplierId" = {supplierId}
                ORDER BY o."createdAt" DESC, p."partNumber" ASC
                OFFSET {(page - 1) * pageSize} ROWS FETCH NEXT {pageSize} ROWS ONLY
                """).ToListAsync(ct);

            var total = (await db.Database.SqlQuery<int>($"""
                SELECT COUNT(*) AS "Value"
                FROM "OrderItem" i
                JOIN "Product" p ON p."id" = i."productId"
                WHERE p."supplierId" = {supplierId}
                """).ToListAsync(ct)).FirstOrDefault();

            return Results.Ok(new
            {
                lines = lines.Select(l => new
                {
                    reference = l.Reference,
                    placedAt = Timestamps.Iso(l.PlacedAt),
                    status = l.Status,
                    partNumber = l.PartNumber,
                    name = l.Name,
                    quantity = l.Quantity,
                }),
                total,
                page,
                pageSize,
                pages = Paging.PageCount(total, pageSize),
            });
        });

        // GET /api/supplier/stock — their parts and what is on the shelf.
        //
        // Summed across warehouses rather than listed per site. Which of our
        // buildings a unit sits in is our logistics and not the supplier's
        // business; how many there are is what tells them whether to expect an
        // order.
        app.MapGet("/api/supplier/stock", async (
            HttpContext http, SupplierGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = await gate.Require(http, ct);
            if (!g.Ok) return g.Response!;

            var supplierId = g.SupplierId;

            // A part nobody has counted comes back with a null quantity, not a
            // zero — the distinction the catalogue already draws, and the one
            // that tells a supplier "we have not counted this yet" apart from
            // "we have sold out of it". Those call for different conversations.
            var parts = await db.Database.SqlQuery<SupplierStockRow>($"""
                SELECT p."partNumber" AS "PartNumber", p."name" AS "Name",
                       SUM(s."quantity") AS "Quantity",
                       COALESCE(SUM(s."reserved"), 0) AS "Reserved",
                       COALESCE(SUM(s."quantity") - SUM(s."reserved"), 0) AS "Available"
                FROM "Product" p
                LEFT JOIN "StockLevel" s ON s."productId" = p."id"
                WHERE p."supplierId" = {supplierId}
                GROUP BY p."id", p."partNumber", p."name"
                ORDER BY p."partNumber" ASC
                """).ToListAsync(ct);

            return Results.Ok(new { parts, total = parts.Count });
        });
    }
}

public record SupplierSummaryRow(
    int Parts, int OutOfStock, int Uncounted, int OpenLines, int OpenUnits);

/// <summary>One line of somebody's order, for a part this supplier supplies.</summary>
public record SupplierOrderLineRow(
    string Reference, DateTime PlacedAt, string Status,
    string PartNumber, string Name, int Quantity);

/// <param name="Quantity">Null where nobody has counted the part. Not zero.</param>
public record SupplierStockRow(
    string PartNumber, string Name, int? Quantity, int Reserved, int Available);
