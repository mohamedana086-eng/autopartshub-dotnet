using System.Text.Json;
using AutoPartsHub.Api.Admin;
using AutoPartsHub.Api.Auth;
using AutoPartsHub.Api.Data;
using AutoPartsHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsHub.Api.Endpoints;

/// <summary>
/// A supplier's own lines, and the three things they may state about one.
/// </summary>
/// <remarks>
/// The last half of the last contract difference. <c>summary</c>, <c>orders</c>
/// and <c>stock</c> are what a supplier can SEE; this is the first thing they
/// can change.
///
/// THEIR OFFERS, NOT OUR PARTS
/// ---------------------------
/// A <c>Product</c> row is ours — its name, its category, which vehicle system
/// it belongs to, what we sell it for. A <c>SupplierOffer</c> is theirs: their
/// terms for one part. So this lists offers and edits offers, and a supplier
/// who has no offer for a part has no line here to edit, whatever else is true
/// of the part.
///
/// WHAT A SUPPLIER MAY STATE
/// -------------------------
/// Their lead time, their own part number, and whether the line is live. All
/// three are facts about THEM that we would otherwise be guessing at or
/// emailing about, and a supplier is the only person who knows them.
///
/// <b>Not the price.</b> <c>purchasePrice</c> is what we pay, and it is the
/// first number the markup engine multiplies — a change to it moves every
/// customer-facing price for that part, immediately and without anybody
/// reading it. This application already has a path for a supplier changing
/// their prices, and it is a price list that somebody reviews and publishes
/// (T-131). Accepting a live edit here would be a second path with none of
/// that, and the second path is the one that gets used.
///
/// It is refused by name rather than ignored. A field silently dropped is a
/// supplier who believes they have repriced a part and has not, and the
/// discovery is a disputed invoice.
///
/// <b>This is a judgement, not a certainty.</b> If the business would rather
/// suppliers repriced themselves directly, the refusal below is the one line
/// to change and the validator already reads the field.
/// </remarks>
public static class SupplierProductEndpoints
{
    public static void MapSupplierProductEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /api/supplier/products
        app.MapGet("/api/supplier/products", async (
            HttpContext http, SupplierGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = await gate.Require(http, ct);
            if (!g.Ok) return g.Response!;

            // From the gate, never from the request. A supplier id read off a
            // query string is the one edit that turns this into somebody
            // else's portal.
            var supplierId = g.SupplierId;

            var products = await db.Database.SqlQuery<SupplierProductRow>($"""
                SELECT o."productId" AS "ProductId", p."partNumber" AS "PartNumber",
                       p."name" AS "Name", m."name" AS "Manufacturer",
                       o."supplierPartNumber" AS "SupplierPartNumber",
                       o."purchasePrice" AS "PurchasePrice",
                       o."stockDays" AS "StockDays", o."active" AS "Active",
                       o."updatedAt" AS "UpdatedAt"
                FROM "SupplierOffer" o
                JOIN "Product" p ON p."id" = o."productId"
                JOIN "Manufacturer" m ON m."id" = p."manufacturerId"
                WHERE o."supplierId" = {supplierId}
                ORDER BY p."partNumber" ASC
                """).ToListAsync(ct);

            // What is NOT here, deliberately, alongside everything else this
            // portal withholds: whether their offer is the one we currently
            // buy at. It names no competitor and no price, and it still tells
            // a supplier that somebody else is underneath them on a part —
            // which is a thing they should learn from us in a negotiation
            // rather than from a green tick on a screen.
            return Results.Ok(new { products, total = products.Count });
        });

        // PATCH /api/supplier/products/<productId> { stockDays?, supplierPartNumber?, active? }
        app.MapPatch("/api/supplier/products/{productId}", async (
            string productId, JsonElement body, HttpContext http, SupplierGate gate,
            AutoPartsContext db, CancellationToken ct) =>
        {
            var g = await gate.Require(http, ct);
            if (!g.Ok) return g.Response!;

            var supplierId = g.SupplierId;

            var read = SupplierOfferInput.Read(body);
            if (!read.Ok) return Results.BadRequest(new { error = read.Error });

            var change = read.Value!;

            // Their own line or nothing. Narrowed inside the UPDATE rather
            // than checked before it: a condition bound into the statement
            // cannot be skipped by a branch and cannot race something that
            // moves the row in between.
            //
            // Each field goes through a CASE on whether it was sent, so a
            // PATCH naming one thing does not blank the two beside it.
            // Two statements in one transaction rather than one with a richer
            // OUTPUT: SQL Server refuses a subquery in an OUTPUT clause, and
            // the part's name and brand live on two other tables. The UPDATE
            // reports WHETHER it matched, and the read that follows says what
            // the row now looks like — inside the transaction, so it cannot
            // report a state that was overwritten in between.
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            var matched = (await db.Database.SqlQuery<string>($"""
                UPDATE "SupplierOffer"
                   SET "stockDays" = CASE WHEN {change.SetsStockDays} = 1
                                          THEN {change.StockDays} ELSE "stockDays" END,
                       "supplierPartNumber" = CASE WHEN {change.SetsSupplierPartNumber} = 1
                                                   THEN {change.SupplierPartNumber}
                                                   ELSE "supplierPartNumber" END,
                       "active" = CASE WHEN {change.SetsActive} = 1
                                       THEN {change.Active} ELSE "active" END,
                       "updatedAt" = SYSUTCDATETIME()
                OUTPUT INSERTED."productId" AS "Value"
                 WHERE "productId" = {productId} AND "supplierId" = {supplierId}
                """).ToListAsync(ct)).FirstOrDefault();

            // Not theirs, or no such part. Both answer the same way: a part
            // this supplier does not offer does not exist as far as their
            // portal is concerned, and a distinct refusal would tell them
            // which of the two it was.
            if (matched is null) return Results.NotFound(new { error = "That part is not one of yours." });

            var updated = (await db.Database.SqlQuery<SupplierProductRow>($"""
                SELECT o."productId" AS "ProductId", p."partNumber" AS "PartNumber",
                       p."name" AS "Name", m."name" AS "Manufacturer",
                       o."supplierPartNumber" AS "SupplierPartNumber",
                       o."purchasePrice" AS "PurchasePrice",
                       o."stockDays" AS "StockDays", o."active" AS "Active",
                       o."updatedAt" AS "UpdatedAt"
                FROM "SupplierOffer" o
                JOIN "Product" p ON p."id" = o."productId"
                JOIN "Manufacturer" m ON m."id" = p."manufacturerId"
                WHERE o."productId" = {productId} AND o."supplierId" = {supplierId}
                """).ToListAsync(ct)).First();

            await transaction.CommitAsync(ct);

            return Results.Ok(new { product = updated });
        });
    }
}

/// <summary>
/// The three things a supplier may state about their own line.
/// </summary>
/// <param name="SetsStockDays">
/// Whether the field was sent at all. Kept apart from the value because null
/// is a meaningful lead time — "fall back to the supplier default" — and a
/// PATCH that did not mention it means something different again.
/// </param>
public record SupplierOfferChange(
    bool SetsStockDays, int? StockDays,
    bool SetsSupplierPartNumber, string? SupplierPartNumber,
    bool SetsActive, bool Active);

/// <summary>Reads what a supplier sent, and refuses what is not theirs to set.</summary>
public static class SupplierOfferInput
{
    public static Validated<SupplierOfferChange> Read(JsonElement body)
    {
        // Refused by name. A price silently dropped is a supplier who believes
        // they have repriced a part and has not, and the discovery is a
        // disputed invoice. See SupplierProductEndpoints for why it is not
        // theirs to set here and where it is.
        if (JsonValues.Get(body, "purchasePrice") is { ValueKind: not JsonValueKind.Null })
        {
            return Validators.Fail<SupplierOfferChange>(
                "A price change goes through a price list so somebody reviews it. "
                + "Send us one, or ask your contact here.");
        }

        var setsStockDays = JsonValues.Get(body, "stockDays") is not null;
        int? stockDays = null;

        if (setsStockDays
            && JsonValues.Get(body, "stockDays") is { ValueKind: not JsonValueKind.Null } days)
        {
            var value = JsonValues.AsNumber(days);

            // The same range the admin editor takes, said the same way. Two
            // ranges for one column is how a supplier is told 400 is fine and
            // an admin is told it is not.
            if (!JsonValues.IsWhole(value) || value is < 0 or > 365)
            {
                return Validators.Fail<SupplierOfferChange>(
                    "A lead time must be a whole number of days, 0 to 365.");
            }

            stockDays = (int)value!.Value;
        }

        var setsPartNumber = JsonValues.Get(body, "supplierPartNumber") is not null;
        string? supplierPartNumber = null;

        if (setsPartNumber)
        {
            var text = JsonValues.AsString(JsonValues.Get(body, "supplierPartNumber")).Trim();

            if (text.Length > OfferInputs.MaxSupplierPartNumber)
            {
                return Validators.Fail<SupplierOfferChange>(
                    $"Keep your part number under {OfferInputs.MaxSupplierPartNumber} characters.");
            }

            // An empty string clears it rather than storing a blank, which is
            // what "I do not have my own number for this" means.
            supplierPartNumber = text.Length > 0 ? text : null;
        }

        var setsActive = false;
        var active = true;

        if (JsonValues.Get(body, "active") is { } sent)
        {
            if (sent.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return Validators.Fail<SupplierOfferChange>("active must be true or false.");
            }

            setsActive = true;
            active = sent.ValueKind == JsonValueKind.True;
        }

        if (!setsStockDays && !setsPartNumber && !setsActive)
        {
            return Validators.Fail<SupplierOfferChange>(
                "Send a stockDays, a supplierPartNumber, or an active.");
        }

        return Validators.Ok(new SupplierOfferChange(
            setsStockDays, stockDays, setsPartNumber, supplierPartNumber, setsActive, active));
    }
}

/// <summary>One of a supplier's lines, as their portal shows it.</summary>
/// <param name="PurchasePrice">What they charge us — theirs already, and not
/// editable here. See SupplierProductEndpoints.</param>
public record SupplierProductRow(
    string ProductId, string PartNumber, string Name, string Manufacturer,
    string? SupplierPartNumber, double PurchasePrice, int? StockDays,
    bool Active, DateTime UpdatedAt);
