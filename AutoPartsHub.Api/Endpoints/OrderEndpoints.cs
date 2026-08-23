using AutoPartsHub.Api.Auth;
using AutoPartsHub.Api.Catalogue;
using AutoPartsHub.Api.Data;
using AutoPartsHub.Api.Inventory;
using AutoPartsHub.Api.Pricing;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AutoPartsHub.Api.Endpoints;

public static class OrderEndpoints
{
    private const int MaxLines = 200;
    private const int MaxQty = 999;

    public static void MapOrderEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /api/orders — the signed-in client's own orders.
        app.MapGet("/api/orders", async (
            HttpContext http, SessionTokens tokens, AutoPartsContext db, CancellationToken ct) =>
        {
            var session = tokens.Decode(http.Request.Cookies[SessionTokens.CookieName]);
            if (session is null) return Results.Json(new { error = "Not signed in." }, statusCode: 401);

            // Raw SQL rather than LINQ over the entity: the scaffolded model
            // does not carry the weight columns, the same way it does not
            // carry partType, and columns added since are read this way.
            var orders = await db.Database.SqlQuery<CustomerOrderRow>($"""
                SELECT "id" AS "Id", "reference" AS "Reference", "status" AS "Status",
                       "createdAt" AS "CreatedAt",
                       "currencyCode" AS "CurrencyCode", "currencyRate" AS "CurrencyRate",
                       "weightGrams" AS "WeightGrams", "weightComplete" AS "WeightComplete"
                FROM "Order"
                WHERE "clientId" = {session.UserId}
                ORDER BY "createdAt" DESC
                """).ToListAsync(ct);

            var orderIds = orders.Select(o => o.Id).ToArray();
            var lines = await db.OrderItems
                .Where(i => orderIds.Contains(i.OrderId))
                .OrderBy(i => i.Product.PartNumber)
                .Select(i => new
                {
                    i.OrderId,
                    i.Quantity,
                    i.UnitPrice,
                    PartNumber = i.Product.PartNumber,
                    Name = i.Product.Name,
                })
                .AsNoTracking()
                .ToListAsync(ct);

            var byOrder = lines.GroupBy(l => l.OrderId).ToDictionary(g => g.Key, g => g.ToList());

            // Line prices are stored in the base currency; the order carries
            // the rate that applied when it was placed. Converting on the way
            // out shows the customer the figures they agreed to, and keeps
            // showing them after their account is moved to another currency.
            return Results.Ok(new
            {
                orders = orders.Select(o =>
                {
                    var mine = byOrder.GetValueOrDefault(o.Id) ?? [];
                    return new
                    {
                        id = o.Id,
                        reference = o.Reference,
                        status = o.Status,
                        createdAt = Timestamps.Iso(o.CreatedAt),
                        units = mine.Sum(l => l.Quantity),
                        total = Money.Round(mine.Sum(l => l.UnitPrice * l.Quantity) * o.CurrencyRate),
                        currencyCode = o.CurrencyCode,
                        // What the order weighed, in grams, as recorded when
                        // it was placed.  false means a
                        // line's part had no weight on file, so the figure is
                        // a floor rather than a fact — and a shipping cost
                        // built on it is wrong in the direction that costs
                        // money. Both halves travel together for that reason.
                        weightGrams = o.WeightGrams,
                        weightComplete = o.WeightComplete,
                        lines = mine.Select(l => new
                        {
                            partNumber = l.PartNumber,
                            name = l.Name,
                            quantity = l.Quantity,
                            unitPrice = Money.Round(l.UnitPrice * o.CurrencyRate),
                        }),
                    };
                }),
            });
        });

        // POST /api/orders { items: [{ productId, quantity }] }
        app.MapPost("/api/orders", async (
            System.Text.Json.JsonElement body, HttpContext http, SessionTokens tokens, AutoPartsContext db,
            PricingContextLoader pricing, CancellationToken ct) =>
        {
            var session = tokens.Decode(http.Request.Cookies[SessionTokens.CookieName]);
            if (session is null)
            {
                return Results.Json(new { error = "Sign in to place an order." }, statusCode: 401);
            }

            // Read as raw JSON rather than a typed model: a quantity of "two"
            // has to come back as "Quantities must be whole numbers…" and not
            // as a binding failure. See JsonValues.
            var items = JsonValues.Get(body, "items");
            if (items is not { ValueKind: System.Text.Json.JsonValueKind.Array } list
                || list.GetArrayLength() == 0)
            {
                return Results.BadRequest(new { error = "Your cart is empty." });
            }
            if (list.GetArrayLength() > MaxLines)
            {
                return Results.BadRequest(new { error = $"An order cannot exceed {MaxLines} lines." });
            }

            // Only ids and quantities are read off the request. Prices are
            // never taken from the client — they are resolved here from the
            // catalogue and the caller's own tier, so a tampered cart cannot
            // set what it pays.
            var wanted = new Dictionary<string, int>();
            foreach (var raw in list.EnumerateArray())
            {
                var productId = JsonValues.AsString(JsonValues.Get(raw, "productId")).Trim();
                var quantity = JsonValues.AsNumber(JsonValues.Get(raw, "quantity"));

                if (productId.Length == 0)
                {
                    return Results.BadRequest(new { error = "A cart line is missing its product." });
                }
                if (!JsonValues.IsWhole(quantity) || quantity < 1 || quantity > MaxQty)
                {
                    return Results.BadRequest(
                        new { error = $"Quantities must be whole numbers between 1 and {MaxQty}." });
                }
                wanted[productId] = wanted.GetValueOrDefault(productId) + (int)quantity!.Value;
            }

            var ids = wanted.Keys.ToArray();
            var products = await db.Database.SqlQuery<PriceableProductRow>($"""
                SELECT p."id" AS "Id", p."partNumber" AS "PartNumber", p."name" AS "Name",
                       p."basePrice" AS "BasePrice", p."supplierId" AS "SupplierId",
                       p."packagingUnit" AS "PackagingUnit",
                       p."quantityPerPackage" AS "QuantityPerPackage",
                       p."goodsCategoryId" AS "GoodsCategoryId",
                       p."weightGrams" AS "WeightGrams",
                       m."name" AS "ManufacturerName", v."slug" AS "SystemSlug",
                       pli."price" AS "ListPrice"
                FROM "Product" p
                JOIN "Manufacturer" m ON m."id" = p."manufacturerId"
                JOIN "VehicleSystem" v ON v."id" = p."vehicleSystemId"
                LEFT JOIN "PriceListItem" pli
                  ON pli."productId" = p."id"
                 AND pli."priceListId" = (SELECT "id" FROM "PriceList" WHERE "active" LIMIT 1)
                WHERE p."id" = ANY({ids}::text[])
                -- A switched-off supplier's part is not orderable. Dropping it
                -- here rather than refusing separately is deliberate: the
                -- caller already compares this count against what was asked
                -- for and answers "a part in your cart is no longer in the
                -- catalogue", which is exactly the case.
                AND (p."supplierId" IS NULL OR EXISTS (
                  SELECT 1 FROM "Supplier" s WHERE s."id" = p."supplierId" AND s."active"
                ))
                """).ToListAsync(ct);

            if (products.Count != wanted.Count)
            {
                return Results.Json(
                    new { error = "A part in your cart is no longer in the catalogue. Remove it and try again." },
                    statusCode: 409);
            }

            // Checked again here, not only in the basket. The basket is a
            // convenience and this endpoint takes its items straight off the
            // request — a client that never touched the basket, or one that
            // was open while the packaging changed, reaches this with a
            // quantity nobody can pick.
            foreach (var p in products)
            {
                var quantity = wanted[p.Id];
                if (Packaging.IsOrderableQuantity(quantity, p.QuantityPerPackage)) continue;

                return Results.BadRequest(new
                {
                    error = $"{p.Name} ({p.PartNumber}): " +
                            Packaging.Refusal(quantity, p.QuantityPerPackage, p.PackagingUnit),
                });
            }

            var ctx = await pricing.LoadAsync(http, ct);

            // Lines are stored in the base currency. The rate is recorded once
            // on the order so the whole thing can be shown back in what the
            // customer was quoted, without the stored numbers moving if their
            // currency is changed later.
            var rate = ctx.Currency?.Rate ?? 1;
            var currencyCode = ctx.Currency?.Code ?? "EUR";
            var symbol = ctx.Currency?.Symbol ?? "€";

            var lines = products.Select(p => new
            {
                productId = p.Id,
                partNumber = p.PartNumber,
                name = p.Name,
                quantity = wanted[p.Id],
                unitPrice = ctx.Price(p)?.NetBase ?? RequestPricing.PurchasePrice(p),
            }).ToList();

            var total = Money.Round(lines.Sum(l => l.unitPrice * l.quantity));

            // The tier's minimum order is a rule the schema already carries.
            var tier = session.CategoryId is null ? null : await db.ClientCategories
                .Where(c => c.Id == session.CategoryId)
                .Select(c => new { c.Name, c.MinOrderAmount })
                .FirstOrDefaultAsync(ct);

            // Compared in the base currency, where both figures are
            // denominated. Doing it after conversion would make the threshold
            // trivial to clear on a weak currency and impossible on a strong
            // one, for the same basket.
            if (tier is not null && tier.MinOrderAmount > 0 && total < tier.MinOrderAmount)
            {
                return Results.Json(new
                {
                    error = $"Orders on the {tier.Name} tier start at {symbol}" +
                            $"{Money.Format(Money.Round(tier.MinOrderAmount * rate))}. " +
                            $"This one comes to {symbol}{Money.Format(Money.Round(total * rate))}.",
                }, statusCode: 409);
            }

            try
            {
                // Weighed from the same rows the prices came from, so the
                // figure recorded on the order describes the parts it was
                // actually placed for.
                var weight = Weight.Sum(products.Select(
                    p => new WeighedLine(p.WeightGrams, wanted[p.Id])));

                var placed = await PlaceAsync(db, session.UserId, currencyCode, rate, weight,
                    lines.Select(l => (l.productId, l.quantity, l.unitPrice)).ToList(), ct);

                return Results.Json(new
                {
                    order = new
                    {
                        id = placed.Id,
                        reference = placed.Reference,
                        status = placed.Status,
                        createdAt = Timestamps.Iso(placed.CreatedAt),
                        total,
                        lines,
                    },
                }, statusCode: 201);
            }
            catch (OutOfStockException e)
            {
                var part = products.FirstOrDefault(p => p.Id == e.Shortfall.ProductId);
                var label = part is null ? "A part in your cart" : $"{part.PartNumber} ({part.Name})";

                return Results.Json(new
                {
                    error = e.Shortfall.Available == 0
                        // Covers both an empty shelf and a part nobody has
                        // counted: to a customer they are the same answer, and
                        // naming the bookkeeping difference would explain
                        // nothing they can act on.
                        ? $"{label} is out of stock. Remove it and try again."
                        : $"Only {e.Shortfall.Available} of {label} " +
                          $"{(e.Shortfall.Available == 1 ? "is" : "are")} available, " +
                          $"and you asked for {e.Shortfall.Wanted}.",
                    productId = e.Shortfall.ProductId,
                    available = e.Shortfall.Available,
                }, statusCode: 409);
            }
        });
    }

    /// <summary>
    /// Writes the order, its lines and its stock allocations in one transaction.
    /// </summary>
    /// <remarks>
    /// Retried on a unique violation, which here means two orders drew the
    /// same reference in the same millisecond — the suffix is random and four
    /// characters, so it happens rarely and is not worth a sequence. Five
    /// attempts, and an out-of-stock is rethrown immediately because retrying
    /// it would only fail the same way.
    /// </remarks>
    private static async Task<PlacedOrder> PlaceAsync(
        AutoPartsContext db, string clientId, string currencyCode, double rate,
        WeightTotal weight,
        List<(string ProductId, int Quantity, double UnitPrice)> lines, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            try
            {
                var orderId = Ids.New();

                var order = (await db.Database.SqlQuery<PlacedOrder>($"""
                    INSERT INTO "Order" ("id", "reference", "clientId", "currencyCode", "currencyRate",
                                        "weightGrams", "weightComplete")
                    VALUES ({orderId}, {Reference()}, {clientId}, {currencyCode}, {rate},
                            {weight.Grams}, {weight.Complete})
                    RETURNING "id" AS "Id", "reference" AS "Reference",
                              "status" AS "Status", "createdAt" AS "CreatedAt"
                    """).ToListAsync(ct)).Single();

                // One line per part — the caller deduplicates — so a part maps
                // to one id.
                var lineIdByProduct = new Dictionary<string, string>();
                foreach (var line in lines)
                {
                    var lineId = Ids.New();
                    lineIdByProduct[line.ProductId] = lineId;
                    await db.Database.ExecuteSqlAsync($"""
                        INSERT INTO "OrderItem" ("id", "orderId", "productId", "quantity", "unitPrice")
                        VALUES ({lineId}, {orderId}, {line.ProductId}, {line.Quantity}, {line.UnitPrice})
                        """, ct);
                }

                var held = await StockMovements.ReserveAsync(
                    db, lines.Select(l => new StockNeed(l.ProductId, l.Quantity)).ToList(), ct);
                if (!held.Ok) throw new OutOfStockException(held.Shortfall!);

                foreach (var a in held.Allocations)
                {
                    await db.Database.ExecuteSqlAsync($"""
                        INSERT INTO "OrderItemAllocation" ("id", "orderItemId", "warehouseId", "quantity")
                        VALUES ({Ids.New()}, {lineIdByProduct[a.ProductId]}, {a.WarehouseId}, {a.Quantity})
                        """, ct);
                }

                await transaction.CommitAsync(ct);
                return order;
            }
            catch (OutOfStockException)
            {
                await transaction.RollbackAsync(ct);
                throw;
            }
            catch (PostgresException e) when (e.SqlState == "23505" && attempt < 4)
            {
                await transaction.RollbackAsync(ct);
            }
        }

        throw new InvalidOperationException("Could not allocate an order reference.");
    }

    /// <summary>APH-260729-K3F9 — short enough to read out over the phone.</summary>
    private static string Reference()
    {
        var now = DateTime.UtcNow;
        var date = $"{now.Year % 100:D2}{now.Month:D2}{now.Day:D2}";
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var suffix = new string(Enumerable.Range(0, 4)
            .Select(_ => Alphabet[Random.Shared.Next(Alphabet.Length)]).ToArray());
        return $"APH-{date}-{suffix}";
    }
}


public record PlacedOrder(string Id, string Reference, string Status, DateTime CreatedAt);

public record PriceableProductRow(
    string Id,
    string PartNumber,
    string Name,
    double BasePrice,
    string? SupplierId,
    /// <summary>What one package is called.</summary>
    string PackagingUnit,
    /// <summary>The step an order moves in. One means no constraint.</summary>
    int QuantityPerPackage,
    /// <summary>The commercial category the part is priced through, or null.</summary>
    string? GoodsCategoryId,
    /// <summary>Per piece, in grams. Null where nobody has weighed the part.</summary>
    int? WeightGrams,
    string ManufacturerName,
    string SystemSlug,
    double? ListPrice) : IPriceable;

public class OutOfStockException(Shortfall shortfall) : Exception
{
    public Shortfall Shortfall { get; } = shortfall;
}

/// <summary>
/// Rounding to cents, and writing a money figure the way the other API does.
/// </summary>
/// <remarks>
/// Summing line totals in binary floating point drifts — three parts at 6.85
/// comes to 20.549999999999997. Harmless once a view formats it, but it also
/// feeds the minimum-order comparison, where a total a hair under the
/// threshold would refuse an order that actually meets it.
/// </remarks>
public static class Money
{
    public static double Round(double value) =>
        Math.Round(value * 100, MidpointRounding.AwayFromZero) / 100;

    /// <summary>Two decimals, invariant — toFixed(2) on the other side.</summary>
    public static string Format(double value) =>
        value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>One of a customer's own orders, as the list reads it.</summary>
public record CustomerOrderRow(
    string Id, string Reference, string Status, DateTime CreatedAt,
    string CurrencyCode, double CurrencyRate,
    /// <summary>What it weighed when it was placed, in grams.</summary>
    int WeightGrams,
    /// <summary>False when a line's part had no weight, making the figure a floor.</summary>
    bool WeightComplete);
