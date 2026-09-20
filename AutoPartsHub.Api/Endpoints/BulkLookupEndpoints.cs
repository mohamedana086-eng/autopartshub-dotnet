using System.Text.Json;
using AutoPartsHub.Api.Catalogue;
using AutoPartsHub.Api.Data;
using AutoPartsHub.Api.Pricing;
using AutoPartsHub.Domain;
using AutoPartsHub.Domain.Catalogue;
using Microsoft.AspNetCore.Http.Metadata;
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
    /// <remarks>
    /// Two caps, because there are two shapes. <c>partNumbers</c> has answered
    /// a thousand since it existed and keeps answering a thousand: the figure
    /// is echoed back as <c>maxRows</c>, and quietly raising it would change
    /// what an existing caller is told about its own request.
    ///
    /// <c>rows</c> is the shape the backlog specifies and takes two thousand,
    /// which is what it asks for.
    /// </remarks>
    private const int MaxPartNumbers = 1000;

    private const int MaxRows = 2000;

    /// <summary>
    /// The largest body this will read.
    /// </summary>
    /// <remarks>
    /// Two thousand rows of part number and manufacturer is a few hundred
    /// kilobytes; two megabytes is room for that and the whitespace a
    /// spreadsheet export brings with it. The limit exists because the row cap
    /// cannot be applied until the body has been parsed, and parsing is the
    /// expensive part — a hundred megabytes of JSON is refused before it is
    /// read rather than after.
    /// </remarks>
    private const int MaxBodyBytes = 2 * 1024 * 1024;

    public static void MapBulkLookupEndpoints(this IEndpointRouteBuilder app)
    {
        // POST /api/catalog/bulk { partNumbers: string[] }
        //                       | { rows: [{ partNumber, manufacturer? }] }
        //
        // Both shapes are served. `partNumbers` is what the storefront sends
        // today; `rows` is what the backlog specifies, and it carries a brand
        // per line — which is what a pasted quotation actually looks like.
        app.MapPost("/api/catalog/bulk", async (
            JsonElement body, AutoPartsContext db, PricingContextLoader pricing,
            HttpContext http, CancellationToken ct) =>
        {
            var read = ReadRequest(body);
            if (!read.Ok) return Results.BadRequest(new { error = read.Error });

            var request = read.Value!;
            var inputs = request.Rows;

            if (inputs.Count == 0)
            {
                return Results.BadRequest(new { error = "No part numbers found in that file." });
            }

            // De-duplicate the lookup while keeping every input row in the
            // answer, so a sheet that lists the same number twice still lines
            // up row for row.
            var needles = inputs.Select(r => PartNumbers.Normalise(r.PartNumber))
                .Where(n => n.Length > 0)
                .Distinct().ToArray();
            if (needles.Length == 0)
            {
                return Results.BadRequest(new { error = "No usable part numbers in that file." });
            }

            // The normalised form is a stored, indexed column, so both of
            // these seek rather than deriving a value per row to compare — see
            // AutoPartsContext.SearchIndexSql. They are the same statements the
            // other API runs.
            var direct = await db.Database.SqlQuery<NormalisedMatch>($"""
                SELECT "id" AS "Id", "partNumberNormalised" AS "Norm"
                FROM "Product"
                WHERE "partNumberNormalised" IN (SELECT value COLLATE DATABASE_DEFAULT FROM OPENJSON({SqlList.Of(needles)}))
                """).ToListAsync(ct);

            var viaInterchange = await db.Database.SqlQuery<InterchangeMatch>($"""
                SELECT i."sourceId" AS "Id", i."targetPartNoNormalised" AS "Norm",
                       i."targetPartNo" AS "Target"
                FROM "Interchange" i
                WHERE i."targetPartNoNormalised" IN (SELECT value COLLATE DATABASE_DEFAULT FROM OPENJSON({SqlList.Of(needles)}))
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
                       pli."markupPercent" AS "ListRowMarkupPercent",
                       bo."purchasePrice" AS "OfferPrice", bo."supplierId" AS "OfferSupplierId",
                       st."available" AS "Available"
                FROM "Product" p
                JOIN "Manufacturer" m ON m."id" = p."manufacturerId"
                JOIN "VehicleSystem" v ON v."id" = p."vehicleSystemId"
                LEFT JOIN "BestOffer" bo ON bo."productId" = p."id"
                LEFT JOIN "PriceListItem" pli
                  ON pli."productId" = p."id"
                 AND pli."priceListId" = (SELECT TOP 1 "id" FROM "PriceList" WHERE "active" = 1)
                OUTER APPLY (
                  SELECT SUM(sl."quantity" - sl."reserved") AS "available"
                  FROM "StockLevel" sl
                  JOIN "Warehouse" w ON w."id" = sl."warehouseId"
                  WHERE sl."productId" = p."id" AND w."active" = 1
                ) st
                WHERE p."id" IN (SELECT value COLLATE DATABASE_DEFAULT FROM OPENJSON({SqlList.Of(ids)}))
                -- Same rule as search and the detail page: a switched-off
                -- supplier's parts are not in the catalogue, so a pasted list
                -- reports them as not carried rather than quoting a price
                -- nobody can buy at.
                -- A part stays sellable while somebody will actually sell it to us: a live
                -- offer from a live supplier, or no supplier relationship at all. What goes
                -- is a part whose every supplier is switched off or whose every offer has
                -- been withdrawn.
                AND (
                  bo."productId" IS NOT NULL
                  OR (p."supplierId" IS NULL AND NOT EXISTS (
                    SELECT 1 FROM "SupplierOffer" so WHERE so."productId" = p."id"
                  ))
                )
                """).ToListAsync(ct);

            var byId = products.ToDictionary(p => p.Id);

            // EVERY candidate per number, not one.
            //
            // A part number is not unique across brands — this catalogue has
            // numbers that normalise onto each other from different makers,
            // and until now the winner among them was whichever row the
            // planner returned first. That is fine when nobody said which
            // brand they meant and indefensible when they did, so the choice
            // is made per input row below rather than here.
            //
            // A direct hit still beats a cross-reference, which is why they
            // are ordered rather than merged.
            var resolved = new Dictionary<string, List<Resolution>>();

            void Offer(string norm, Resolution candidate)
            {
                if (!resolved.TryGetValue(norm, out var candidates))
                {
                    resolved[norm] = candidates = [];
                }
                candidates.Add(candidate);
            }

            foreach (var row in direct) Offer(row.Norm, new Resolution(row.Id, "part-number", null));
            foreach (var row in viaInterchange) Offer(row.Norm, new Resolution(row.Id, "interchange", row.Target));

            var rows = new List<object>(inputs.Count);
            var found = 0;
            var total = 0d;

            foreach (var row in inputs)
            {
                var input = row.PartNumber;

                BulkRow? product = null;
                Resolution? hit = null;

                if (resolved.TryGetValue(PartNumbers.Normalise(input), out var candidates))
                {
                    hit = Choose(candidates, row.Manufacturer, byId);
                    if (hit is not null) byId.TryGetValue(hit.Id, out product);
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
                truncated = request.Submitted > request.Cap,
                maxRows = request.Cap,
                foundCount = found,
                missingCount = rows.Count - found,
                total = Math.Round(total * 100, MidpointRounding.AwayFromZero) / 100,
                rows,
            });
        })
        // Enforced by the server before the body is read, not by the handler
        // after: the handler runs once the JSON has been parsed, and parsing
        // is the part a hundred-megabyte paste would cost. The row caps above
        // bound the WORK; this bounds the READING.
        .WithMetadata(new RequestSizeLimit(MaxBodyBytes));
    }

    /// <summary>
    /// How large a body this endpoint will read.
    /// </summary>
    /// <remarks>
    /// Minimal APIs honour <see cref="IRequestSizeLimitMetadata"/> on an
    /// endpoint, and the attribute that carries it lives in the MVC package
    /// this project does not reference. Six lines is cheaper than the
    /// dependency.
    /// </remarks>
    private sealed class RequestSizeLimit(long bytes) : IRequestSizeLimitMetadata
    {
        public long? MaxRequestBodySize => bytes;

        /// <summary>False: the limit is the point of declaring it.</summary>
        public bool DisableRequestSizeLimit => false;
    }

    /// <param name="MatchedVia">The cross-referenced number that led here, null on a direct hit.</param>
    private record Resolution(string Id, string MatchedOn, string? MatchedVia);

    /// <summary>One line of the pasted list.</summary>
    /// <param name="Manufacturer">
    /// The brand that line named, or null. Only the <c>rows</c> shape can
    /// carry one; <c>partNumbers</c> is a list of strings and always answers
    /// null here, which is the same as saying "no preference".
    /// </param>
    private record BulkInput(string PartNumber, string? Manufacturer);

    /// <param name="Submitted">How many lines arrived, before the cap.</param>
    /// <param name="Cap">The limit that applied, which the response echoes.</param>
    private record BulkRequest(IReadOnlyList<BulkInput> Rows, int Submitted, int Cap);

    /// <summary>
    /// Reads either shape.
    /// </summary>
    /// <remarks>
    /// <c>rows</c> wins when both are sent, for the reason the search's
    /// <c>offerType</c> does: a caller sending the newer shape meant it, and
    /// merging two answers to one question has no defensible result.
    ///
    /// A row that is a bare string inside <c>rows</c> is taken as a part
    /// number with no brand. It costs one line and it is what half the people
    /// pasting a list will send first.
    /// </remarks>
    private static Validated<BulkRequest> ReadRequest(JsonElement body)
    {
        if (JsonValues.Get(body, "rows") is { ValueKind: JsonValueKind.Array } rows)
        {
            var lines = rows.EnumerateArray()
                .Select(entry => entry.ValueKind == JsonValueKind.Object
                    ? new BulkInput(
                        JsonValues.AsString(JsonValues.Get(entry, "partNumber")).Trim(),
                        JsonValues.AsString(JsonValues.Get(entry, "manufacturer")).Trim() is { Length: > 0 } m
                            ? m
                            : null)
                    : new BulkInput(JsonValues.AsString(entry).Trim(), null))
                .Where(r => r.PartNumber.Length > 0)
                .Take(MaxRows)
                .ToList();

            return Validation.Ok(new BulkRequest(lines, rows.GetArrayLength(), MaxRows));
        }

        if (JsonValues.Get(body, "partNumbers") is { ValueKind: JsonValueKind.Array } list)
        {
            var lines = list.EnumerateArray()
                .Select(v => JsonValues.AsString(v).Trim())
                .Where(v => v.Length > 0)
                .Take(MaxPartNumbers)
                .Select(v => new BulkInput(v, null))
                .ToList();

            return Validation.Ok(new BulkRequest(lines, list.GetArrayLength(), MaxPartNumbers));
        }

        return Validation.Fail<BulkRequest>("partNumbers must be an array.");
    }

    /// <summary>
    /// Which of several parts carrying the same number the line meant.
    /// </summary>
    /// <remarks>
    /// The order is: the brand they named, then anything. Within each, a
    /// direct hit beats a cross-reference — which is the rule that was already
    /// here, applied after the brand rather than instead of it.
    ///
    /// An unrecognised brand falls through to "anything" rather than reporting
    /// the line as not carried. A customer whose spreadsheet says "Bosch Gmbh"
    /// against a number this catalogue does stock is better served the part
    /// than the empty row, and the row still says which brand was returned so
    /// they can see it was not the one they typed.
    ///
    /// Compared loosely, because the brand is whatever a spreadsheet had in
    /// it: case and the separators a name picks up on its way through Excel
    /// are ignored, the same way a part number's are.
    /// </remarks>
    private static Resolution? Choose(
        List<Resolution> candidates, string? manufacturer, Dictionary<string, BulkRow> byId)
    {
        if (manufacturer is not null)
        {
            var wanted = PartNumbers.Normalise(manufacturer);

            var preferred = candidates.FirstOrDefault(c =>
                byId.TryGetValue(c.Id, out var product)
                && PartNumbers.Normalise(product.ManufacturerName) == wanted);

            if (preferred is not null) return preferred;
        }

        return candidates.FirstOrDefault(c => byId.ContainsKey(c.Id));
    }
}

public record NormalisedMatch(string Id, string Norm);

public record InterchangeMatch(string Id, string Norm, string Target);

public record BulkRow(
    string Id, string PartNumber, string Name, double BasePrice, string? SupplierId,
    int StockDays, string? GoodsCategoryId, int? WeightGrams, string PartType,
    string ManufacturerName, string SystemName, string SystemSlug,
    double? ListPrice,
    /// <summary>That line's own margin — the narrowest rung. See IPriceable.</summary>
    double? ListRowMarkupPercent,
    /// <summary>The best offer's price and whose it was — see IPriceable.</summary>
    double? OfferPrice, string? OfferSupplierId,
    int? Available) : IPriceable;
