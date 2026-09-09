using AutoPartsHub.Api.Auth;
using AutoPartsHub.Api.Catalogue;
using AutoPartsHub.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsHub.Api.Endpoints;

/// <summary>
/// The reference lists the storefront is built from.
/// </summary>
/// <remarks>
/// These three take nothing from the request. Under Next that made them
/// candidates for being prerendered into the build, which is what went wrong
/// once already — a supplier list that stated part counts fixed on the day of
/// the deploy. There is no equivalent trap here: a minimal API handler runs
/// per request, and nothing caches it unless asked to.
/// </remarks>
public static class CatalogueEndpoints
{
    public static void MapCatalogueEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /api/systems — the vehicle system tree for the browse grid.
        app.MapGet("/api/systems", async (AutoPartsContext db) =>
        {
            var systems = await db.VehicleSystems
                .OrderBy(s => s.Order)
                .Select(s => new SystemDto(s.Id, s.Name, s.Slug, s.Icon))
                .AsNoTracking()
                .ToListAsync();

            return Results.Ok(new { systems });
        });

        // GET /api/suppliers — everyone we buy from, for the directory page.
        app.MapGet("/api/suppliers", async (
            HttpContext http, AutoPartsContext db, SessionTokens tokens) =>
        {
            // Nothing here is priced, so there is no account load to take the
            // role from and the signed cookie is read directly.
            var naming = SupplierNaming.For(http, tokens);

            var suppliers = await db.Suppliers
                // The public directory lists who is trading. One waiting for
                // approval has no page and no entry: the whole point of
                // arriving switched off is that nothing shows until an admin
                // says so.
                .Where(s => s.Active)
                // Ordered by the real name, not by whatever is published.
                // Anonymising is a rendering decision and must not reshuffle
                // the page: two callers see the same directory in the same
                // order, and only the labels differ.
                .OrderBy(s => s.Name)
                .Select(s => new SupplierRow(
                    s.Id, s.Code, s.Slug, s.Name, s.Description, s.Reliability,
                    s.Rating, s.AcceptsReturns, s.Country, s.GuaranteeMonths,
                    // Counted in the database. EF turns this into a correlated
                    // subquery, which is one statement rather than a list of
                    // products loaded per supplier and thrown away.
                    s.Products.Count))
                .AsNoTracking()
                .ToListAsync();

            return Results.Ok(new
            {
                // The naming is applied here rather than in the projection
                // above: it is a C# decision and EF has no translation for it.
                suppliers = suppliers.Select(s => new SupplierDto(
                    s.Id, s.Code, s.Slug, naming.Of(s.Name, s.Code), s.Description,
                    s.Reliability, s.Rating, s.AcceptsReturns, s.Country,
                    s.GuaranteeMonths, s.ProductCount)),
            });
        });
    }
}

/// <summary>A vehicle system, as the storefront reads it.</summary>
/// <remarks>
/// A record per response rather than returning the entity: an entity carries
/// its relations and its foreign keys, and serialising one sends whatever
/// happens to be loaded. Naming the fields makes the response a decision.
/// </remarks>
public record SystemDto(string Id, string Name, string Slug, string Icon);

/// <summary>
/// A directory row as the database gives it, before a name has been chosen.
/// </summary>
/// <remarks>
/// Separate from <see cref="SupplierDto"/> so that the row carrying the real
/// name and the record that goes on the wire are different types. Reusing one
/// would make "the name is already anonymised" a fact about where you are in
/// the method rather than about which type you are holding.
/// </remarks>
public record SupplierRow(
    string Id,
    string Code,
    string Slug,
    string Name,
    string? Description,
    string Reliability,
    int? Rating,
    bool? AcceptsReturns,
    string? Country,
    int? GuaranteeMonths,
    int ProductCount);

public record SupplierDto(
    string Id,
    string Code,
    string Slug,
    string Name,
    string? Description,
    string Reliability,
    int? Rating,
    bool? AcceptsReturns,
    string? Country,
    int? GuaranteeMonths,
    int ProductCount);
