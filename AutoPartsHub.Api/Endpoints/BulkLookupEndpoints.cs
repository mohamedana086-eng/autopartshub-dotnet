using System.Text.Json;
using AutoPartsHub.Api.Catalogue;
using AutoPartsHub.Api.Data;
using AutoPartsHub.Api.Pricing;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsHub.Api.Endpoints;

/// <summary>
/// A list of part numbers off a customer's spreadsheet, answered in order.
/// </summary>
/// <remarks>
/// Comparison ignores separators, and a number this catalogue does not stock
/// still resolves if one of its parts lists that number as a replacement.
/// </remarks>
public static class BulkLookupEndpoints
{
    /// <summary>Guards the request against someone pasting a whole catalogue in.</summary>
    private const int MaxRows = 1000;

    public static void MapBulkLookupEndpoints(this IEndpointRouteBuilder app)
    {
        // POST /api/catalog/bulk { partNumbers: string[] }
        app.MapPost("/api/catalog/bulk", async (
            JsonElement body, AutoPartsContext db, PricingContextLoader pricing,
            HttpContext http, CancellationToken ct) =>
        {
            if (JsonValues.Get(body, "partNumbers") is not { ValueKind: JsonValueKind.Array } list)
            {
                return Results.BadRequest(new { error = "partNumbers must be an array." });
            }

            var submitted = list.GetArrayLength();
            var inputs = list.EnumerateArray()
                .Select(v => JsonValues.AsString(v).Trim())
                .Where(v => v.Length > 0)
                .Take(MaxRows)
                .ToList();

            if (inputs.Count == 0)
            {
                return Results.BadRequest(new { error = "No part numbers found in that file." });
            }

            // De-duplicate the lookup while keeping every input row in the
            // answer, so a sheet that lists the same number twice still lines
            // up row for row.
            var needles = inputs.Select(PartNumbers.Normalise).Where(n => n.Length > 0)
                .Distinct().ToArray();
            if (needles.Length == 0)
            {
                return Results.BadRequest(new { error = "No usable part numbers in that file." });
            }

            // The normalised form is not stored, so both of these are scans —
            // fine for a catalogue this size, and the same statements the other
            // API runs.
            var direct = await db.Database.SqlQuery<NormalisedMatch>($"""
                SELECT "id" AS "Id",
                       regexp_replace(upper("partNumber"), '[^A-Z0-9]', '', 'g') AS "Norm"
                FROM "Product"
                WHERE regexp_replace(upper("partNumber"), '[^A-Z0-9]', '', 'g') = ANY({needles}::text[])
                """).ToListAsync(ct);

            var viaInterchange = await db.Database.SqlQuery<InterchangeMatch>($"""
                SELECT i."sourceId" AS "Id",
                       regexp_replace(upper(i."targetPartNo"), '[^A-Z0-9]', '', 'g') AS "Norm",
                       i."targetPartNo" AS "Target"
                FROM "Interchange" i
                WHERE regexp_replace(upper(i."targetPartNo"), '[^A-Z0-9]', '', 'g') = ANY({needles}::text[])
                """).ToListAsync(ct);

            var ctx = await pricing.LoadAsync(http, ct);

            var ids = direct.Select(r => r.Id).Concat(viaInterchange.Select(r => r.Id))
                .Distinct().ToArray();

            var products = ids.Length == 0 ? [] : await db.Database.SqlQuery<BulkRow>($"""
                SELECT p."id" AS "Id", p."partNumber" AS "PartNumber", p."name" AS "Name",
                       p."basePrice" AS "BasePrice", p."supplierId" AS "SupplierId",
                       p."stockDays" AS "StockDays",
                       p."goodsCategoryId" AS "GoodsCategoryId",
                       p."weightGrams" AS "WeightGrams", p."partType" AS "PartType",
                       m."name" AS "ManufacturerName",
                       v."name" AS "SystemName", v."slug" AS "SystemSlug",
                       pli."price" AS "ListPrice",
                       st."available" AS "Available"
                FROM "Product" p
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
                WHERE p."id" = ANY({ids}::text[])
                -- Same rule as search and the detail page: a switched-off
                -- supplier's parts are not in the catalogue, so a pasted list
                -- reports them as not carried rather than quoting a price
                -- nobody can buy at.
                AND (p."supplierId" IS NULL OR EXISTS (
                  SELECT 1 FROM "Supplier" s WHERE s."id" = p."supplierId" AND s."active"
                ))
                """).ToListAsync(ct);

            var byId = products.ToDictionary(p => p.Id);

            // A direct hit beats a cross-reference for the same input, so the
            // cross-references go in first and the direct matches overwrite
            // them. Among cross-references the first one found wins.
            var resolved = new Dictionary<string, Resolution>();
            foreach (var row in viaInterchange)
            {
                if (!resolved.ContainsKey(row.Norm))
                {
                    resolved[row.Norm] = new Resolution(row.Id, "interchange", row.Target);
                }
            }
            foreach (var row in direct) resolved[row.Norm] = new Resolution(row.Id, "part-number", null);

            var rows = new List<object>(inputs.Count);
            var found = 0;
            var total = 0d;

            foreach (var input in inputs)
            {
                BulkRow? product = null;
                if (resolved.TryGetValue(PartNumbers.Normalise(input), out var hit))
                {
                    byId.TryGetValue(hit.Id, out product);
                }

                if (hit is null || product is null)
                {
                    rows.Add(new { input, found = false, product = (object?)null });
                    continue;
                }

                var priced = ctx.Price(product);
                var price = priced?.FinalPrice ?? RequestPricing.PurchasePrice(product);

                found++;
                total += price;

                rows.Add(new
                {
                    input,
                    found = true,
                    matchedOn = hit.MatchedOn,
                    matchedVia = hit.MatchedVia,
                    product = new
                    {
                        id = product.Id,
                        partNumber = product.PartNumber,
                        name = product.Name,
                        manufacturer = product.ManufacturerName,
                        system = product.SystemName,
                        stockDays = product.StockDays,
                        price,
                        appliedRule = priced?.AppliedRule,
                        // A spreadsheet of fifty numbers is exactly where
                        // adding more than exists would go unnoticed, so the
                        // figure travels with the row. Null means nobody
                        // counted the part, which sells nothing.
                        available = Availability.Sellable(product.Available),
                    },
                });
            }

            return Results.Ok(new
            {
                tierName = ctx.TierName,
                isLoggedIn = ctx.IsLoggedIn,
                submitted = inputs.Count,
                truncated = submitted > MaxRows,
                maxRows = MaxRows,
                foundCount = found,
                missingCount = rows.Count - found,
                total = Math.Round(total * 100, MidpointRounding.AwayFromZero) / 100,
                rows,
            });
        });
    }

    /// <param name="MatchedVia">The cross-referenced number that led here, null on a direct hit.</param>
    private record Resolution(string Id, string MatchedOn, string? MatchedVia);
}

public record NormalisedMatch(string Id, string Norm);

public record InterchangeMatch(string Id, string Norm, string Target);

public record BulkRow(
    string Id, string PartNumber, string Name, double BasePrice, string? SupplierId,
    int StockDays, string? GoodsCategoryId, int? WeightGrams, string PartType,
    string ManufacturerName, string SystemName, string SystemSlug,
    double? ListPrice, int? Available) : IPriceable;
