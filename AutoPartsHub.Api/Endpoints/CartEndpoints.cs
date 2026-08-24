using AutoPartsHub.Api.Auth;
using AutoPartsHub.Api.Catalogue;
using AutoPartsHub.Api.Data;
using AutoPartsHub.Api.Pricing;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsHub.Api.Endpoints;

/// <summary>
/// The signed-in account's saved basket.
/// </summary>
/// <remarks>
/// No price is ever stored here — the table holds ids and quantities and
/// nothing else, so there is no second answer to go stale and none to submit
/// in place of the real one. What a line costs is still resolved at checkout
/// by the order endpoint, from the catalogue and the caller's tier.
///
/// The read does resolve a price, the same way search does: freshly, from the
/// caller's own tier, on every request. That is what lets a basket restored on
/// a new device render as a basket rather than a list of part numbers, and it
/// cannot drift, because nothing keeps it.
/// </remarks>
public static class CartEndpoints
{
    /// <summary>More lines than a basket can hold.</summary>
    private const int MaxLines = 200;

    public static void MapCartEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/cart", async (
            HttpContext http, SessionTokens tokens, AutoPartsContext db,
            PricingContextLoader pricing, CancellationToken ct) =>
        {
            var session = tokens.Decode(http.Request.Cookies[SessionTokens.CookieName]);
            if (session is null) return Results.Json(new { error = "Not signed in." }, statusCode: 401);

            return Results.Ok(await SerialiseAsync(db, pricing, http, session.UserId, ct));
        });

        // PUT /api/cart — replaces the basket with what was sent.
        //
        // A replace rather than add/remove endpoints, because the client
        // already holds the whole basket in localStorage and is the thing
        // deciding what is in it. Sending the current state avoids the two
        // copies drifting apart, which is what per-item calls would have to
        // reconcile.
        app.MapPut("/api/cart", async (
            System.Text.Json.JsonElement body, HttpContext http, SessionTokens tokens, AutoPartsContext db,
            PricingContextLoader pricing, CancellationToken ct) =>
        {
            var session = tokens.Decode(http.Request.Cookies[SessionTokens.CookieName]);
            if (session is null) return Results.Json(new { error = "Not signed in." }, statusCode: 401);

            // Raw JSON rather than a typed model, so a bad quantity is answered
            // with a sentence instead of a binding failure. See JsonValues.
            var items = JsonValues.Get(body, "items");
            if (items is not { ValueKind: System.Text.Json.JsonValueKind.Array } list)
            {
                return Results.BadRequest(new { error = "Expected a list of items." });
            }
            if (list.GetArrayLength() > MaxLines)
            {
                return Results.BadRequest(new { error = "That is more lines than a basket can hold." });
            }

            var wanted = new Dictionary<string, int>();
            foreach (var entry in list.EnumerateArray())
            {
                if (entry.ValueKind != System.Text.Json.JsonValueKind.Object)
                {
                    return Results.BadRequest(new { error = "Every item must be an object." });
                }

                var productId = JsonValues.AsString(JsonValues.Get(entry, "productId")).Trim();
                var quantity = JsonValues.AsNumber(JsonValues.Get(entry, "quantity"));

                if (productId.Length == 0) return Results.BadRequest(new { error = "Every item needs a product." });
                if (!JsonValues.IsWhole(quantity) || quantity < 1)
                {
                    return Results.BadRequest(new { error = "Quantity must be a whole number of one or more." });
                }

                // The same part twice is one line with the quantities added,
                // which is what the unique key on (cart, product) means.
                wanted[productId] = wanted.GetValueOrDefault(productId) + (int)quantity!.Value;
            }

            var ids = wanted.Keys.ToArray();
            // "Still in the catalogue" includes whether anyone is selling it.
            // A part behind a switched-off supplier is refused on the way into
            // a basket, which is the earliest place to say no.
            // Raw SQL rather than LINQ over the entity, because the packaging
            // columns are not on it — the scaffolded model does not carry
            // partType either, and columns added since are read this way
            // rather than by hand-editing something generated. It also puts
            // this query in the same shape as the Node one it has to agree
            // with.
            var carried = await db.Database.SqlQuery<CartPackagingRow>($"""
                SELECT p."id" AS "Id", p."partNumber" AS "PartNumber", p."name" AS "Name",
                       p."packagingUnit" AS "PackagingUnit",
                       p."quantityPerPackage" AS "QuantityPerPackage"
                FROM "Product" p
                WHERE p."id" = ANY({ids}::text[])
                -- "Still in the catalogue" includes whether anyone is selling
                -- it. A part behind a switched-off supplier is refused on the
                -- way into a basket, which is the earliest place to say no.
                AND (p."supplierId" IS NULL OR EXISTS (
                  SELECT 1 FROM "Supplier" s WHERE s."id" = p."supplierId" AND s."active"
                ))
                """).ToListAsync(ct);

            // Refused on the way into the basket rather than at checkout.
            //
            // The basket is where a customer changes their mind about a
            // number, so it is where the number should be argued with. Finding
            // out at checkout that a part cannot be split means going back to
            // a screen they had finished with, and the message would have to
            // name a part they can no longer see.
            foreach (var part in carried)
            {
                var quantity = wanted[part.Id];
                if (Packaging.IsOrderableQuantity(quantity, part.QuantityPerPackage)) continue;

                return Results.BadRequest(new
                {
                    error = $"{part.Name} ({part.PartNumber}): " +
                            Packaging.Refusal(quantity, part.QuantityPerPackage, part.PackagingUnit),
                });
            }

            var known = carried;
            if (known.Count != ids.Length)
            {
                // A part deleted from the catalogue since it was added. Naming
                // it would mean loading rows the caller may not have asked
                // about; the client reloads the basket on this and shows what
                // survived.
                return Results.Json(
                    new { error = "A part in that basket is no longer in the catalogue." }, statusCode: 409);
            }

            await ReplaceAsync(db, session.UserId, wanted, ct);

            return Results.Ok(await SerialiseAsync(db, pricing, http, session.UserId, ct));
        });
    }

    private static async Task<object> SerialiseAsync(
        AutoPartsContext db, PricingContextLoader pricing, HttpContext http, string clientId, CancellationToken ct)
    {
        var updatedAt = await db.Carts
            .Where(c => c.ClientId == clientId)
            .Select(c => (DateTime?)c.UpdatedAt)
            .FirstOrDefaultAsync(ct);

        var lines = updatedAt is null ? [] : await LinesForAsync(db, clientId, ct);
        var ctx = await pricing.LoadAsync(http, ct);

        return new
        {
            // Timestamps.Iso, not ToUniversalTime: the column is TIMESTAMP
            // without a zone, so Npgsql hands it back with Kind=Unspecified,
            // and ToUniversalTime reads Unspecified as local and shifts it by
            // whatever the machine's offset happens to be. The value stored is
            // already UTC. This read three hours early on my machine and would
            // have read correctly on a server set to UTC — right where nobody
            // is looking, wrong everywhere else.
            updatedAt = Timestamps.Iso(updatedAt),
            items = lines.Select(line => new
            {
                productId = line.ProductId,
                partNumber = line.PartNumber,
                name = line.Name,
                manufacturer = line.ManufacturerName,
                stockDays = line.StockDays,
                // Falls back to the purchase price only when no tier resolves
                // at all, which is the same fallback the order endpoint uses.
                unitPrice = ctx.Price(line)?.FinalPrice ?? RequestPricing.PurchasePrice(line),
                quantity = line.Quantity,
                // What the basket may hold of this part. Sent so the cart can
                // hold its own quantity control to it instead of finding out
                // at checkout, and resolved fresh on every read — a basket
                // saved last week has no claim on stock since sold. Null means
                // nobody counted the part, which sells nothing.
                available = Availability.Sellable(line.Available),
                // Sent so the basket's quantity control can step by the
                // package after a reload, when the part page it was added
                // from is long gone. Without this the control steps by one
                // and offers numbers checkout refuses.
                packagingUnit = line.PackagingUnit,
                quantityPerPackage = line.QuantityPerPackage,
                /* Per piece, in grams. Null where nobody has weighed the part. */
                weightGrams = line.WeightGrams,
            }),
            // What the basket weighs, and whether that is the whole of it.
            //
            // Both halves, because a part nobody has weighed makes the number
            // a floor rather than a fact — and a shipping figure built on a
            // floor is wrong in the direction that costs money. Counting the
            // unknowns as zero would produce something that looks like an
            // answer.
            weight = Weight.Sum(lines.Select(l => new WeighedLine(l.WeightGrams, l.Quantity))),
        };
    }

    private static async Task<List<BasketLineRow>> LinesForAsync(
        AutoPartsContext db, string clientId, CancellationToken ct) =>
        await db.Database.SqlQuery<BasketLineRow>($"""
            SELECT ci."productId" AS "ProductId", ci."quantity" AS "Quantity",
                   p."partNumber" AS "PartNumber", p."name" AS "Name",
                   p."basePrice" AS "BasePrice", p."supplierId" AS "SupplierId",
                   p."stockDays" AS "StockDays",
                   p."packagingUnit" AS "PackagingUnit",
                   p."quantityPerPackage" AS "QuantityPerPackage",
                   p."goodsCategoryId" AS "GoodsCategoryId",
                   p."weightGrams" AS "WeightGrams", p."partType" AS "PartType",
                   m."name" AS "ManufacturerName",
                   v."slug" AS "SystemSlug",
                   pli."price" AS "ListPrice",
                   st."available" AS "Available"
            FROM "CartItem" ci
            JOIN "Cart" c ON c."id" = ci."cartId"
            JOIN "Product" p ON p."id" = ci."productId"
            JOIN "Manufacturer" m ON m."id" = p."manufacturerId"
            JOIN "VehicleSystem" v ON v."id" = p."vehicleSystemId"
            LEFT JOIN "PriceListItem" pli
              ON pli."productId" = p."id"
             AND pli."priceListId" = (SELECT "id" FROM "PriceList" WHERE "active" LIMIT 1)
            LEFT JOIN LATERAL (
              SELECT SUM(sl."quantity" - sl."reserved")::int AS "available"
              FROM "StockLevel" sl
              JOIN "Warehouse" w ON w."id" = sl."warehouseId"
              WHERE sl."productId" = p."id" AND w."active" = true
            ) st ON true
            WHERE c."clientId" = {clientId}
            -- A part whose supplier has been switched off drops out of the
            -- basket the same way a deleted part already does — the JOIN above
            -- simply stops matching. One behaviour rather than two: if it
            -- cannot be added and cannot be ordered, showing it in the basket
            -- shows something that cannot be bought.
            AND (p."supplierId" IS NULL OR EXISTS (
              SELECT 1 FROM "Supplier" s WHERE s."id" = p."supplierId" AND s."active"
            ))
            ORDER BY ci."addedAt" ASC
            """).ToListAsync(ct);

    /// <summary>
    /// Replaces the basket with what was sent, in one transaction.
    /// </summary>
    /// <remarks>
    /// <c>updatedAt</c> is touched explicitly: the column only moves on a
    /// write to Cart itself, and every change here is to its items. The
    /// admin's open-baskets list is ordered by it, so a basket edited today
    /// must not read as untouched.
    /// </remarks>
    private static async Task ReplaceAsync(
        AutoPartsContext db, string clientId, Dictionary<string, int> wanted, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var cartId = (await db.Database.SqlQuery<string>($"""
            INSERT INTO "Cart" ("id", "clientId", "updatedAt")
            VALUES ({Ids.New()}, {clientId}, now())
            ON CONFLICT ("clientId") DO UPDATE SET "updatedAt" = now()
            RETURNING "id" AS "Value"
            """).ToListAsync(ct)).Single();

        var ids = wanted.Keys.ToArray();
        await db.Database.ExecuteSqlAsync($"""
            DELETE FROM "CartItem"
            WHERE "cartId" = {cartId} AND NOT ("productId" = ANY({ids}::text[]))
            """, ct);

        foreach (var (productId, quantity) in wanted)
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "CartItem" ("id", "cartId", "productId", "quantity")
                VALUES ({Ids.New()}, {cartId}, {productId}, {quantity})
                ON CONFLICT ("cartId", "productId") DO UPDATE SET "quantity" = {quantity}
                """, ct);
        }

        await transaction.CommitAsync(ct);
    }
}

/// <summary>What the basket needs to know about a part before it accepts one.</summary>
public record CartPackagingRow(
    string Id,
    string PartNumber,
    string Name,
    string PackagingUnit,
    int QuantityPerPackage);



/// <summary>One basket line, flat and priced from the caller's tier.</summary>
public record BasketLineRow(
    string ProductId,
    int Quantity,
    string PartNumber,
    string Name,
    double BasePrice,
    string? SupplierId,
    int StockDays,
    string ManufacturerName,
    string SystemSlug,
    double? ListPrice,
    int? Available,
    /// <summary>What one package is called.</summary>
    string PackagingUnit,
    /// <summary>The step this line moves in. One means no constraint.</summary>
    int QuantityPerPackage,
    /// <summary>The commercial category the part is priced through, or null.</summary>
    string? GoodsCategoryId,
    /// <summary>Per piece, in grams. Null where nobody has weighed the part.</summary>
    int? WeightGrams,
    /// <summary>oem | aftermarket | substitute, for the "part type" dimension.</summary>
    string PartType) : IPriceable;
