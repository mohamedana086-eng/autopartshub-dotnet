using AutoPartsHub.Api.Catalogue;
using AutoPartsHub.Api.Data;
using AutoPartsHub.Api.Pricing;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsHub.Api.Endpoints;

public static class ProductEndpoints
{
    public static void MapProductEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /api/catalog/products/<id> — detail view, priced for the caller's tier.
        app.MapGet("/api/catalog/products/{id}", async (
            string id, AutoPartsContext db, SpecQueries specQueries, BarcodeQueries barcodeQueries,
            PricingContextLoader pricing, HttpContext http, CancellationToken ct) =>
        {
            var product = (await db.Database.SqlQuery<ProductDetailRow>($"""
                SELECT p."id" AS "Id", p."partNumber" AS "PartNumber", p."name" AS "Name",
                       p."description" AS "Description", p."stockDays" AS "StockDays",
                       p."basePrice" AS "BasePrice", p."supplierId" AS "SupplierId",
                       p."partType" AS "PartType",
                       p."packagingUnit" AS "PackagingUnit",
                       p."quantityPerPackage" AS "QuantityPerPackage",
                       p."goodsCategoryId" AS "GoodsCategoryId",
                       p."weightGrams" AS "WeightGrams",
                       m."name" AS "ManufacturerName",
                       v."name" AS "SystemName", v."slug" AS "SystemSlug",
                       pli."price" AS "ListPrice",
                       pli."markupPercent" AS "ListRowMarkupPercent",
                       bo."purchasePrice" AS "OfferPrice", bo."supplierId" AS "OfferSupplierId",
                       st."available" AS "Available",
                       s."slug" AS "SupplierSlug", s."name" AS "SupplierName",
                       -- Both names travel; SupplierNaming picks one.
                       s."code" AS "SupplierCode", s."rating" AS "SupplierRating"
                FROM "Product" p
                JOIN "Manufacturer" m ON m."id" = p."manufacturerId"
                JOIN "VehicleSystem" v ON v."id" = p."vehicleSystemId"
                -- The supplier whose offer WON, falling back to the column the part was
                -- first sourced from. Joined on the same value the pricing chain uses, so
                -- a row cannot name one supplier while being priced from another's offer.
                LEFT JOIN "BestOffer" bo ON bo."productId" = p."id"
                LEFT JOIN "Supplier" s ON s."id" = COALESCE(bo."supplierId", p."supplierId")
                LEFT JOIN "PriceListItem" pli
                  ON pli."productId" = p."id"
                 AND pli."priceListId" = (SELECT "id" FROM "PriceList" WHERE "active" LIMIT 1)
                LEFT JOIN LATERAL (
                  SELECT SUM(sl."quantity" - sl."reserved")::int AS "available"
                  FROM "StockLevel" sl
                  JOIN "Warehouse" w ON w."id" = sl."warehouseId"
                  WHERE sl."productId" = p."id" AND w."active" = true
                ) st ON true
                WHERE p."id" = {id}
                -- A switched-off supplier's parts are out of the catalogue, so
                -- this reads as "no such part" rather than showing a page
                -- nobody can buy from. Parts with no supplier are the
                -- catalogue's own and stay.
                -- A live offer from a live supplier, or no supplier relationship at all.
                AND (
                  bo."productId" IS NOT NULL
                  OR (p."supplierId" IS NULL AND NOT EXISTS (
                    SELECT 1 FROM "SupplierOffer" so WHERE so."productId" = p."id"
                  ))
                )
                """).ToListAsync(ct)).FirstOrDefault();

            if (product is null) return Results.NotFound(new { error = "Product not found" });

            var ctx = await pricing.LoadAsync(http, ct);
            var priced = ctx.Price(product);

            var images = await db.ProductImages
                .Where(i => i.ProductId == id)
                .OrderBy(i => i.SortOrder)
                .Select(i => new { url = i.Url, alt = i.Alt })
                .AsNoTracking()
                .ToListAsync(ct);

            var interchanges = await db.Interchanges
                .Where(i => i.SourceId == id)
                .Select(i => new
                {
                    id = i.Id,
                    partNumber = i.TargetPartNo,
                    manufacturer = i.TargetManufacturer,
                    exactMatch = i.ExactMatch,
                    isOEM = i.IsOem,
                })
                .AsNoTracking()
                .ToListAsync(ct);

            // All of them, not the three a result row shows. This is the page
            // the row was pointing at when it said there was more.
            var specs = (await specQueries.ForAsync([id], null, ct)).GetValueOrDefault(id) ?? [];
            // Every code this part answers to. The page is where a warehouse
            // looks one up, so all of them belong here.
            var barcodes = (await barcodeQueries.ForAsync([id], ct)).GetValueOrDefault(id) ?? [];

            return Results.Ok(new
            {
                tierName = ctx.TierName,
                isLoggedIn = ctx.IsLoggedIn,
                product = new
                {
                    id = product.Id,
                    partNumber = product.PartNumber,
                    name = product.Name,
                    description = product.Description,
                    manufacturer = product.ManufacturerName,
                    system = product.SystemName,
                    systemSlug = product.SystemSlug,
                    // What the customer would be buying. On the search row
                    // since the part-type filter was added, and missing here
                    // until now — so the storefront's own type declared it
                    // required on the detail response and read `undefined` at
                    // runtime. The badge answers "what am I buying", which is
                    // a question asked on the page you open to decide.
                    partType = product.PartType,
                    // How the part is packed, and therefore what quantities it
                    // sells in.
                    packagingUnit = product.PackagingUnit,
                    quantityPerPackage = product.QuantityPerPackage,
                    /* What one piece weighs, in grams. Null where nobody has weighed it. */
                    weightGrams = product.WeightGrams,
                    stockDays = product.StockDays,
                    price = priced?.FinalPrice ?? RequestPricing.PurchasePrice(product),
                    appliedRule = priced?.AppliedRule,
                    // Empty where nobody has added any, which today is every
                    // part. The alt falls back to the part's name where it is
                    // rendered, per the schema.
                    images,
                    // Every specification the part has, in its own display
                    // order. The search result row carries the first three;
                    // this is where the rest of them are, which is the whole
                    // reason the row can afford to show only three.
                    specs,
                    barcodes,
                    available = product.Available,
                    // Three fields, as the API this replaces sent from here —
                    // the search row's `reliability` and `acceptsReturns` are
                    // not on this query and adding them would widen a response
                    // that is compared field-for-field. Only the name is
                    // decided elsewhere, by the one thing allowed to decide it.
                    supplier = product.SupplierSlug is null ? null : new
                    {
                        slug = product.SupplierSlug,
                        name = SupplierNaming.For(ctx.ClientRole)
                            .OfMaybe(product.SupplierName, product.SupplierCode),
                        rating = product.SupplierRating,
                    },
                    interchanges,
                },
            });
        });
    }
}

/// <summary>One part, flat, with everything the detail page prices and shows.</summary>
public record ProductDetailRow(
    string Id,
    string PartNumber,
    string Name,
    string? Description,
    int StockDays,
    double BasePrice,
    string? SupplierId,
    /// <summary>oem | aftermarket | substitute — what the customer would be buying.</summary>
    string PartType,
    string PackagingUnit,
    int QuantityPerPackage,
    string? GoodsCategoryId,
    int? WeightGrams,
    string ManufacturerName,
    string SystemName,
    string SystemSlug,
    double? ListPrice,
    /// <summary>That line's own margin — the narrowest rung. See IPriceable.</summary>
    double? ListRowMarkupPercent,
    /// <summary>The best offer's price and whose it was — see IPriceable.</summary>
    double? OfferPrice,
    string? OfferSupplierId,
    int? Available,
    string? SupplierSlug,
    string? SupplierName,
    /// <summary>The opaque handle a customer sees instead of the name.</summary>
    string? SupplierCode,
    int? SupplierRating) : IPriceable;
