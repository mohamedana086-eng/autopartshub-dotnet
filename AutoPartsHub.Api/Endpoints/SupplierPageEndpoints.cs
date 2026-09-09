using AutoPartsHub.Api.Auth;
using AutoPartsHub.Api.Catalogue;
using AutoPartsHub.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsHub.Api.Endpoints;

/// <summary>
/// One supplier's page: who they are, and the shape of their range.
/// </summary>
/// <remarks>
/// The breakdowns are counted in the database rather than by walking the whole
/// product list in memory — the page never wants the parts themselves, only
/// how many fall where. The parts come from
/// <c>/api/catalog/search?supplier=&lt;slug&gt;</c>, which already prices,
/// filters and sorts them.
/// </remarks>
public static class SupplierPageEndpoints
{
    public static void MapSupplierPageEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/suppliers/{slug}", async (
            string slug, HttpContext http, AutoPartsContext db, SessionTokens tokens,
            CancellationToken ct) =>
        {
            // Nothing here is priced, so the role comes from the signed cookie
            // rather than from an account load this page has no other use for.
            var naming = SupplierNaming.For(http, tokens);

            var supplier = (await db.Database.SqlQuery<SupplierPageRow>($"""
                SELECT s."id" AS "Id", s."code" AS "Code", s."slug" AS "Slug", s."name" AS "Name",
                       s."description" AS "Description", s."reliability" AS "Reliability",
                       s."rating" AS "Rating", s."acceptsReturns" AS "AcceptsReturns",
                       s."country" AS "Country", s."guaranteeMonths" AS "GuaranteeMonths",
                       COUNT(p."id")::int AS "ProductCount",
                       MIN(p."stockDays")::int AS "FastestDelivery"
                FROM "Supplier" s
                LEFT JOIN "Product" p ON p."supplierId" = s."id"
                WHERE s."slug" = {slug}
                  -- Not found rather than empty: a supplier waiting for
                  -- approval should not have a public page that says who they
                  -- are and lists nothing.
                  AND s."active"
                GROUP BY s."id"
                """).ToListAsync(ct)).FirstOrDefault();

            if (supplier is null) return Results.NotFound(new { error = "No such supplier." });

            // Ordered by count and then by name, in the database, so ties break
            // the same way here as they do on the other API — a .NET string
            // sort and a Postgres collation do not agree about case.
            var systems = await db.Database.SqlQuery<SystemCountRow>($"""
                SELECT v."slug" AS "Slug", v."name" AS "Name", COUNT(*)::int AS "Count"
                FROM "Product" p
                JOIN "VehicleSystem" v ON v."id" = p."vehicleSystemId"
                WHERE p."supplierId" = {supplier.Id}
                GROUP BY v."slug", v."name"
                ORDER BY "Count" DESC, v."name" ASC
                """).ToListAsync(ct);

            var brands = await db.Database.SqlQuery<BrandCountRow>($"""
                SELECT m."name" AS "Name", COUNT(*)::int AS "Count"
                FROM "Product" p
                JOIN "Manufacturer" m ON m."id" = p."manufacturerId"
                WHERE p."supplierId" = {supplier.Id}
                GROUP BY m."name"
                ORDER BY "Count" DESC, m."name" ASC
                """).ToListAsync(ct);

            // Spread flat, with the breakdowns appended — the field order is
            // the other API's, because the two are compared as text.
            return Results.Ok(new
            {
                supplier = new
                {
                    id = supplier.Id,
                    code = supplier.Code,
                    slug = supplier.Slug,
                    name = naming.Of(supplier.Name, supplier.Code),
                    description = supplier.Description,
                    reliability = supplier.Reliability,
                    rating = supplier.Rating,
                    acceptsReturns = supplier.AcceptsReturns,
                    country = supplier.Country,
                    guaranteeMonths = supplier.GuaranteeMonths,
                    productCount = supplier.ProductCount,
                    fastestDelivery = supplier.FastestDelivery,
                    systems = systems.Select(s => new { slug = s.Slug, name = s.Name, count = s.Count }),
                    brands = brands.Select(b => new { name = b.Name, count = b.Count }),
                },
            });
        });
    }
}

/// <param name="FastestDelivery">The shortest lead time on anything they carry. Null when they carry nothing.</param>
public record SupplierPageRow(
    string Id, string Code, string Slug, string Name, string? Description, string Reliability,
    int? Rating, bool? AcceptsReturns, string? Country, int? GuaranteeMonths,
    int ProductCount, int? FastestDelivery);

public record SystemCountRow(string Slug, string Name, int Count);

public record BrandCountRow(string Name, int Count);
