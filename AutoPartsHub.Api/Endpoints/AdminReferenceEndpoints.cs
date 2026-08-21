using AutoPartsHub.Api.Auth;
using AutoPartsHub.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsHub.Api.Endpoints;

/// <summary>
/// The reference data the admin edits: suppliers, sites, currencies, tiers,
/// markup rules and purchase price lists.
/// </summary>
/// <remarks>
/// Each list carries the counts its screen is read for — how many parts a
/// supplier sources, how many units a warehouse holds, how many accounts a
/// currency prices. They are counted in the database, in lateral joins, rather
/// than by loading the rows behind them and reducing in memory: the numbers
/// are the point of the list, and the rows behind them are never shown.
/// </remarks>
public static class AdminReferenceEndpoints
{
    public static void MapAdminReferenceEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /api/admin/suppliers — everyone we buy from, with how much each sources.
        app.MapGet("/api/admin/suppliers", async (
            HttpContext http, AdminGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            var suppliers = await db.Database.SqlQuery<AdminSupplierRow>($"""
                SELECT s."id" AS "Id", s."code" AS "Code", s."slug" AS "Slug", s."name" AS "Name",
                       s."description" AS "Description", s."reliability" AS "Reliability",
                       s."rating" AS "Rating", s."acceptsReturns" AS "AcceptsReturns",
                       s."country" AS "Country", s."guaranteeMonths" AS "GuaranteeMonths",
                       s."defaultStockDays" AS "DefaultStockDays",
                       s."purchaseCurrencyId" AS "PurchaseCurrencyId",
                       c."code" AS "PurchaseCurrencyCode",
                       p."count"::int AS "ProductCount"
                FROM "Supplier" s
                LEFT JOIN "Currency" c ON c."id" = s."purchaseCurrencyId"
                LEFT JOIN LATERAL (
                  SELECT COUNT(*) AS "count" FROM "Product" pr WHERE pr."supplierId" = s."id"
                ) p ON TRUE
                ORDER BY s."name" ASC
                """).ToListAsync(ct);

            // For the editor's currency select. Reference only on a supplier,
            // but it still has to be picked from the currencies that exist.
            return Results.Ok(new { suppliers, currencies = await CurrencyOptions(db, ct) });
        });

        // GET /api/admin/warehouses — every warehouse, with what it holds.
        //
        // Picking order, then code: the list answers "where would this ship
        // from", and that is the order the answer is decided in.
        app.MapGet("/api/admin/warehouses", async (
            HttpContext http, AdminGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            var warehouses = await db.Database.SqlQuery<AdminWarehouseRow>($"""
                SELECT w."id" AS "Id", w."code" AS "Code", w."name" AS "Name", w."city" AS "City",
                       w."address" AS "Address", w."active" AS "Active", w."priority" AS "Priority",
                       o."count"::int AS "OutletCount",
                       s."skus"::int AS "SkuCount",
                       COALESCE(s."quantity", 0)::int AS "TotalQuantity",
                       COALESCE(s."reserved", 0)::int AS "TotalReserved"
                FROM "Warehouse" w
                LEFT JOIN LATERAL (
                  SELECT COUNT(*) AS "count" FROM "RetailOutlet" ro WHERE ro."warehouseId" = w."id"
                ) o ON TRUE
                LEFT JOIN LATERAL (
                  SELECT COUNT(*) AS "skus", SUM(sl."quantity") AS "quantity",
                         SUM(sl."reserved") AS "reserved"
                  FROM "StockLevel" sl WHERE sl."warehouseId" = w."id"
                ) s ON TRUE
                ORDER BY w."priority" DESC, w."code" ASC
                """).ToListAsync(ct);

            return Results.Ok(new { warehouses });
        });

        // GET /api/admin/outlets — the counters, plus the warehouses they can
        // be served from. Open counters first, then by code: a closed outlet is
        // reference, not work.
        app.MapGet("/api/admin/outlets", async (
            HttpContext http, AdminGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            var outlets = await db.Database.SqlQuery<AdminOutletRow>($"""
                SELECT o."id" AS "Id", o."code" AS "Code", o."name" AS "Name", o."city" AS "City",
                       o."address" AS "Address", o."phone" AS "Phone",
                       o."warehouseId" AS "WarehouseId",
                       w."name" AS "WarehouseName", w."code" AS "WarehouseCode",
                       o."active" AS "Active"
                FROM "RetailOutlet" o
                LEFT JOIN "Warehouse" w ON w."id" = o."warehouseId"
                ORDER BY o."active" DESC, o."code" ASC
                """).ToListAsync(ct);

            var warehouses = await db.Database.SqlQuery<NamedOption>($"""
                SELECT "id" AS "Id", "code" || ' — ' || "name" AS "Name"
                FROM "Warehouse"
                ORDER BY "priority" DESC, "code" ASC
                """).ToListAsync(ct);

            return Results.Ok(new { outlets, warehouses });
        });

        // GET /api/admin/currencies — every currency, with how many accounts
        // use it. Base first, then alphabetical: the base is the one everything
        // else is measured against, so it belongs at the top rather than under E.
        app.MapGet("/api/admin/currencies", async (
            HttpContext http, AdminGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            var currencies = await db.Database.SqlQuery<AdminCurrencyRow>($"""
                SELECT c."id" AS "Id", c."code" AS "Code", c."name" AS "Name", c."symbol" AS "Symbol",
                       c."rate" AS "Rate", c."isBase" AS "IsBase", c."active" AS "Active",
                       n."count"::int AS "ClientCount"
                FROM "Currency" c
                LEFT JOIN LATERAL (
                  SELECT COUNT(*) AS "count" FROM "Client" cl WHERE cl."currencyId" = c."id"
                ) n ON TRUE
                ORDER BY c."isBase" DESC, c."code" ASC
                """).ToListAsync(ct);

            return Results.Ok(new { currencies });
        });

        // GET /api/admin/client-categories — tiers cheapest markup first,
        // which is the order they are reasoned about.
        app.MapGet("/api/admin/client-categories", async (
            HttpContext http, AdminGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            var categories = await db.Database.SqlQuery<AdminCategoryRow>($"""
                SELECT c."id" AS "Id", c."name" AS "Name", c."markupPercent" AS "MarkupPercent",
                       c."minOrderAmount" AS "MinOrderAmount", c."shelfLifeDays" AS "ShelfLifeDays",
                       n."count"::int AS "ClientCount"
                FROM "ClientCategory" c
                LEFT JOIN LATERAL (
                  SELECT COUNT(*) AS "count" FROM "Client" cl WHERE cl."categoryId" = c."id"
                ) n ON TRUE
                ORDER BY c."markupPercent" ASC
                """).ToListAsync(ct);

            return Results.Ok(new { categories });
        });

        // GET /api/admin/markup-rules — rules plus everything the builder's
        // selects need. Each rule names the tier and supplier it targets: the
        // list is read to see which rule wins, and one that says only
        // "category cm3x…" cannot be read at all.
        app.MapGet("/api/admin/markup-rules", async (
            HttpContext http, AdminGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            var rules = await db.Database.SqlQuery<AdminMarkupRuleRow>($"""
                SELECT r."id" AS "Id", r."label" AS "Label", r."priority" AS "Priority",
                       r."clientCategoryId" AS "ClientCategoryId",
                       cc."name" AS "ClientCategoryName",
                       r."supplierId" AS "SupplierId", s."name" AS "SupplierName",
                       r."manufacturerName" AS "ManufacturerName",
                       r."vehicleSystemSlug" AS "VehicleSystemSlug",
                       r."partNumberPrefix" AS "PartNumberPrefix",
                       r."purchasePriceFrom" AS "PurchasePriceFrom",
                       r."purchasePriceTo" AS "PurchasePriceTo",
                       r."type" AS "Type", r."value" AS "Value", r."active" AS "Active"
                FROM "MarkupRule" r
                LEFT JOIN "ClientCategory" cc ON cc."id" = r."clientCategoryId"
                LEFT JOIN "Supplier" s ON s."id" = r."supplierId"
                ORDER BY r."priority" DESC
                """).ToListAsync(ct);

            var categories = await db.ClientCategories.OrderBy(c => c.MarkupPercent)
                .Select(c => new { id = c.Id, name = c.Name }).AsNoTracking().ToListAsync(ct);
            var suppliers = await db.Suppliers
                .Select(s => new { id = s.Id, name = s.Name }).AsNoTracking().ToListAsync(ct);
            var systems = await db.VehicleSystems.OrderBy(v => v.Order)
                .Select(v => new { slug = v.Slug, name = v.Name }).AsNoTracking().ToListAsync(ct);

            return Results.Ok(new { rules, categories, suppliers, systems });
        });

        // GET /api/admin/price-lists — every list, active first then newest.
        //
        // ADMIN only, deliberately, like every write to them: these set what
        // every part costs to buy, which is the number the whole markup engine
        // multiplies up.
        app.MapGet("/api/admin/price-lists", async (
            HttpContext http, AdminGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            var lists = await PriceLists(db, null, ct);

            return Results.Ok(new { lists = lists.Select(Serialise) });
        });

        // GET /api/admin/price-lists/<id> — the list, with a sample of what is
        // in it. Enough to check a file landed the way it was meant to; not the
        // whole thing, which can be tens of thousands of rows.
        app.MapGet("/api/admin/price-lists/{id}", async (
            string id, HttpContext http, AdminGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            var list = (await PriceLists(db, id, ct)).FirstOrDefault();
            if (list is null) return Results.NotFound(new { error = "Price list not found." });

            const int ItemPage = 200;
            var items = await db.Database.SqlQuery<PriceListLineRow>($"""
                SELECT i."productId" AS "ProductId", p."partNumber" AS "PartNumber",
                       p."name" AS "Name", i."price" AS "Price",
                       i."sourcePrice" AS "SourcePrice", i."sourceCurrency" AS "SourceCurrency",
                       p."basePrice" AS "BasePrice"
                FROM "PriceListItem" i
                JOIN "Product" p ON p."id" = i."productId"
                WHERE i."priceListId" = {id}
                ORDER BY p."partNumber" ASC
                LIMIT {ItemPage}
                """).ToListAsync(ct);

            return Results.Ok(new { list = Serialise(list), shown = items.Count, items });
        });

        // GET /api/admin/clients — every account, plus the tiers they can be
        // moved to. A salesperson sees their own customers and nobody else's.
        app.MapGet("/api/admin/clients", async (
            HttpContext http, AdminGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireStaff(http);
            if (!g.Ok) return g.Response!;

            var scope = g.ScopeTo;

            var clients = await db.Database.SqlQuery<AdminClientRow>($"""
                SELECT c."id" AS "Id", c."name" AS "Name", c."email" AS "Email", c."role" AS "Role",
                       c."city" AS "City",
                       (c."passwordHash" IS NOT NULL) AS "HasLogin",
                       c."categoryId" AS "CategoryId", cat."name" AS "CategoryName",
                       c."discountPercent" AS "DiscountPercent",
                       c."currencyId" AS "CurrencyId", cur."code" AS "CurrencyCode",
                       c."salesManagerId" AS "SalesManagerId", m."name" AS "SalesManagerName"
                FROM "Client" c
                LEFT JOIN "ClientCategory" cat ON cat."id" = c."categoryId"
                LEFT JOIN "Currency" cur ON cur."id" = c."currencyId"
                LEFT JOIN "Client" m ON m."id" = c."salesManagerId"
                WHERE ({scope}::text IS NULL OR c."salesManagerId" = {scope})
                ORDER BY c."createdAt" DESC
                """).ToListAsync(ct);

            var categories = await db.ClientCategories.OrderBy(c => c.MarkupPercent)
                .Select(c => new { id = c.Id, name = c.Name }).AsNoTracking().ToListAsync(ct);

            // Its own query, not derived from the scoped list above: that one
            // is narrowed to a salesperson's own customers, which would leave
            // the dropdown listing whichever staff happened to be among them —
            // usually none.
            var salesManagers = await db.Clients.Where(c => c.Role == Roles.Sales).OrderBy(c => c.Name)
                .Select(c => new { id = c.Id, name = c.Name }).AsNoTracking().ToListAsync(ct);

            return Results.Ok(new
            {
                clients,
                categories,
                currencies = await CurrencyOptions(db, ct),
                salesManagers,
            });
        });
    }

    private static Task<List<NamedOption>> CurrencyOptions(AutoPartsContext db, CancellationToken ct) =>
        db.Database.SqlQuery<NamedOption>($"""
            SELECT "id" AS "Id", "code" || ' — ' || "name" AS "Name"
            FROM "Currency"
            WHERE "active" = TRUE
            ORDER BY "isBase" DESC, "code" ASC
            """).ToListAsync(ct);

    private static Task<List<PriceListRow>> PriceLists(AutoPartsContext db, string? id, CancellationToken ct) =>
        db.Database.SqlQuery<PriceListRow>($"""
            SELECT l."id" AS "Id", l."name" AS "Name", l."description" AS "Description",
                   l."active" AS "Active", l."sourceName" AS "SourceName",
                   n."count"::int AS "ItemCount",
                   l."createdAt" AS "CreatedAt", l."updatedAt" AS "UpdatedAt"
            FROM "PriceList" l
            LEFT JOIN LATERAL (
              SELECT COUNT(*) AS "count" FROM "PriceListItem" i WHERE i."priceListId" = l."id"
            ) n ON TRUE
            WHERE ({id}::text IS NULL OR l."id" = {id})
            ORDER BY l."active" DESC, l."createdAt" DESC
            """).ToListAsync(ct);

    private static object Serialise(PriceListRow l) => new
    {
        id = l.Id,
        name = l.Name,
        description = l.Description,
        active = l.Active,
        sourceName = l.SourceName,
        itemCount = l.ItemCount,
        createdAt = Timestamps.Iso(l.CreatedAt),
        updatedAt = Timestamps.Iso(l.UpdatedAt),
    };
}

public record NamedOption(string Id, string Name);

public record AdminSupplierRow(
    string Id, string Code, string Slug, string Name, string? Description, string Reliability,
    int? Rating, bool? AcceptsReturns, string? Country, int? GuaranteeMonths,
    int? DefaultStockDays, string? PurchaseCurrencyId, string? PurchaseCurrencyCode, int ProductCount);

public record AdminWarehouseRow(
    string Id, string Code, string Name, string? City, string? Address, bool Active, int Priority,
    int OutletCount, int SkuCount, int TotalQuantity, int TotalReserved);

public record AdminOutletRow(
    string Id, string Code, string Name, string? City, string? Address, string? Phone,
    string? WarehouseId, string? WarehouseName, string? WarehouseCode, bool Active);

public record AdminCurrencyRow(
    string Id, string Code, string Name, string Symbol, double Rate, bool IsBase, bool Active, int ClientCount);

public record AdminCategoryRow(
    string Id, string Name, double MarkupPercent, double MinOrderAmount, int ShelfLifeDays, int ClientCount);

public record AdminMarkupRuleRow(
    string Id, string Label, int Priority, string? ClientCategoryId, string? ClientCategoryName,
    string? SupplierId, string? SupplierName, string? ManufacturerName, string? VehicleSystemSlug,
    string? PartNumberPrefix, double? PurchasePriceFrom, double? PurchasePriceTo,
    string Type, double Value, bool Active);

public record PriceListRow(
    string Id, string Name, string? Description, bool Active, string? SourceName,
    int ItemCount, DateTime CreatedAt, DateTime UpdatedAt);

/// <param name="BasePrice">What the part costs without this list, so the change is visible.</param>
public record PriceListLineRow(
    string ProductId, string PartNumber, string Name, double Price,
    double? SourcePrice, string? SourceCurrency, double BasePrice);

public record AdminClientRow(
    string Id, string Name, string Email, string Role, string? City, bool HasLogin,
    string? CategoryId, string? CategoryName, double DiscountPercent,
    string? CurrencyId, string? CurrencyCode, string? SalesManagerId, string? SalesManagerName);
