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
            string id, AutoPartsContext db, PricingContextLoader pricing, HttpContext http, CancellationToken ct) =>
        {
            var product = (await db.Database.SqlQuery<ProductDetailRow>($"""
                SELECT p."id" AS "Id", p."partNumber" AS "PartNumber", p."name" AS "Name",
                       p."description" AS "Description", p."stockDays" AS "StockDays",
                       p."basePrice" AS "BasePrice", p."supplierId" AS "SupplierId",
                       p."partType" AS "PartType",
                       m."name" AS "ManufacturerName",
                       v."name" AS "SystemName", v."slug" AS "SystemSlug",
                       pli."price" AS "ListPrice",
                       st."available" AS "Available",
                       s."slug" AS "SupplierSlug", s."name" AS "SupplierName", s."rating" AS "SupplierRating"
                FROM "Product" p
                JOIN "Manufacturer" m ON m."id" = p."manufacturerId"
                JOIN "VehicleSystem" v ON v."id" = p."vehicleSystemId"
                LEFT JOIN "Supplier" s ON s."id" = p."supplierId"
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
                AND (p."supplierId" IS NULL OR s."active")
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
                    stockDays = product.StockDays,
                    price = priced?.FinalPrice ?? RequestPricing.PurchasePrice(product),
                    appliedRule = priced?.AppliedRule,
                    // Empty where nobody has added any, which today is every
                    // part. The alt falls back to the part's name where it is
                    // rendered, per the schema.
                    images,
                    available = product.Available,
                    supplier = product.SupplierSlug is null ? null : new
                    {
                        slug = product.SupplierSlug,
                        name = product.SupplierName!,
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
    string ManufacturerName,
    string SystemName,
    string SystemSlug,
    double? ListPrice,
    int? Available,
    string? SupplierSlug,
    string? SupplierName,
    int? SupplierRating) : IPriceable;
