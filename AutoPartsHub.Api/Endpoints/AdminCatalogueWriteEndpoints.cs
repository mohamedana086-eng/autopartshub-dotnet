using System.Text.Json;
using AutoPartsHub.Api.Admin;
using AutoPartsHub.Api.Auth;
using AutoPartsHub.Api.Catalogue;
using AutoPartsHub.Api.Data;
using AutoPartsHub.Domain.Catalogue;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsHub.Api.Endpoints;

/// <summary>
/// The catalogue as the admin edits it: parts, their pictures and their counts.
/// </summary>
/// <remarks>
/// The list aggregates in the database — one thumbnail and two stock totals
/// per part — where asking for every picture and every shelf would be three
/// hundred rows fanned out into thousands to produce six hundred numbers.
/// </remarks>
public static class AdminCatalogueWriteEndpoints
{
    /// <summary>
    /// Every column the product list and the product detail return.
    /// </summary>
    /// <remarks>
    /// Written out twice below rather than shared in a constant: EF binds
    /// every hole in an interpolated query, including one holding SQL, so a
    /// shared fragment arrives as a parameter and Postgres answers "syntax
    /// error at or near $1". Same reason as the catalogue search.
    /// </remarks>
    public static void MapAdminCatalogueWriteEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /api/admin/products?q= — catalogue rows plus what the editor's
        // selects need.
        app.MapGet("/api/admin/products", async (
            string? q, HttpContext http, AdminGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            // Null when nothing was typed, so the filter turns itself off.
            var term = (q ?? "").Trim() is { Length: > 0 } t ? $"%{t}%" : null;

            var products = await db.Database.SqlQuery<AdminProductRow>($"""
                SELECT p."id" AS "Id", p."partNumber" AS "PartNumber", p."name" AS "Name",
                       p."description" AS "Description", p."basePrice" AS "BasePrice",
                       p."stockDays" AS "StockDays",
                       p."partType" AS "PartType",
                       p."goodsCategoryId" AS "GoodsCategoryId",
                       g."name" AS "GoodsCategoryName",
                       p."manufacturerId" AS "ManufacturerId", m."name" AS "ManufacturerName",
                       p."vehicleSystemId" AS "VehicleSystemId", v."name" AS "SystemName",
                       p."supplierId" AS "SupplierId", s."name" AS "SupplierName",
                       (SELECT COUNT(*) FROM "Interchange" i WHERE i."sourceId" = p."id") AS "InterchangeCount",
                       (SELECT COUNT(*) FROM "ProductImage" pi WHERE pi."productId" = p."id") AS "ImageCount",
                       img."url" AS "PrimaryImageUrl",
                       st."stockOnHand" AS "StockOnHand", st."stockAvailable" AS "StockAvailable"
                FROM "Product" p
                JOIN "Manufacturer" m ON m."id" = p."manufacturerId"
                JOIN "VehicleSystem" v ON v."id" = p."vehicleSystemId"
                LEFT JOIN "Supplier" s ON s."id" = p."supplierId"
                LEFT JOIN "GoodsCategory" g ON g."id" = p."goodsCategoryId"
                OUTER APPLY (
                  SELECT pi."url" FROM "ProductImage" pi
                  WHERE pi."productId" = p."id" ORDER BY pi."sortOrder" ASC OFFSET 0 ROWS FETCH NEXT 1 ROWS ONLY
                ) img
                OUTER APPLY (
                  SELECT SUM(sl."quantity") AS "stockOnHand",
                         SUM(sl."quantity" - sl."reserved") AS "stockAvailable"
                  FROM "StockLevel" sl WHERE sl."productId" = p."id"
                ) st
                -- Brand and system are searched deliberately: an admin typing
                -- "brembo" means the brand, and leaving it out made the filter
                -- answer nothing for it while the storefront found the parts.
                WHERE ({term} IS NULL
                   OR p."partNumber" LIKE {term}
                   OR p."name" LIKE {term}
                   OR m."name" LIKE {term}
                   OR v."name" LIKE {term})
                ORDER BY v."order" ASC, p."partNumber" ASC
                OFFSET 0 ROWS FETCH NEXT 300 ROWS ONLY
                """).ToListAsync(ct);

            var manufacturers = await db.Manufacturers.OrderBy(m => m.Name)
                .Select(m => new { id = m.Id, name = m.Name }).AsNoTracking().ToListAsync(ct);
            var systems = await db.VehicleSystems.OrderBy(v => v.Order)
                .Select(v => new { id = v.Id, name = v.Name }).AsNoTracking().ToListAsync(ct);
            var suppliers = await db.Suppliers.OrderBy(s => s.Name)
                .Select(s => new { id = s.Id, name = s.Name }).AsNoTracking().ToListAsync(ct);

            // The stock editor needs somewhere to put a count even when no part
            // is held anywhere yet, so the warehouse list travels with the
            // catalogue rather than being fetched the first time a row opens.
            var warehouses = await db.Warehouses.Where(w => w.Active)
                .OrderByDescending(w => w.Priority).ThenBy(w => w.Code)
                .Select(w => new { id = w.Id, name = w.Code + " — " + w.Name })
                .AsNoTracking().ToListAsync(ct);

            // Active only. A switched-off category still holds its parts and
            // still prices nothing, but offering it in a dropdown would invite
            // filing a new part into a category the shop has retired.
            var goodsCategories = await db.Database.SqlQuery<NamedRow>($"""
                SELECT "id" AS "Id", "name" AS "Name" FROM "GoodsCategory"
                WHERE "active" = 1 ORDER BY "sortOrder" ASC, "name" ASC
                """).ToListAsync(ct);

            return Results.Ok(new { products, manufacturers, systems, suppliers, warehouses,
                goodsCategories = goodsCategories.Select(g => new { id = g.Id, name = g.Name }) });
        });

        app.MapPost("/api/admin/products", async (
            JsonElement body, HttpContext http, AdminGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            var parsed = Validators.ReadProduct(body);
            if (!parsed.Ok) return Results.BadRequest(new { error = parsed.Error });
            var p = parsed.Value!;

            var refs = await References(db, p, ct);
            if (refs.Refusal is not null) return refs.Refusal;

            if (await db.Products.AnyAsync(x => x.PartNumber == p.PartNumber, ct))
            {
                return Results.Json(new
                {
                    error = $"Part number {p.PartNumber} is already in the catalogue.",
                }, statusCode: 409);
            }

            // A blank lead time inherits the supplier's default, then the
            // schema's. This is the only place the supplier default is
            // consulted: once a part carries a number it is the part's own, and
            // a later change to the supplier's default must not rewrite it.
            var stockDays = p.StockDays ?? refs.SupplierStockDays ?? 1;

            var id = Ids.New();
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "Product" ("id", "partNumber", "name", "description", "manufacturerId",
                                       "vehicleSystemId", "supplierId", "basePrice", "stockDays", "partType",
                                       "goodsCategoryId")
                VALUES ({id}, {p.PartNumber}, {p.Name}, {p.Description}, {p.ManufacturerId},
                        {p.VehicleSystemId}, {p.SupplierId}, {p.BasePrice}, {stockDays},
                        {p.PartType}, {p.GoodsCategoryId})
                """, ct);

            return Results.Json(new { product = await ProductById(db, id, ct) }, statusCode: 201);
        });

        app.MapPatch("/api/admin/products/{id}", async (
            string id, JsonElement body, HttpContext http, AdminGate gate,
            AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            var parsed = Validators.ReadProduct(body);
            if (!parsed.Ok) return Results.BadRequest(new { error = parsed.Error });
            var p = parsed.Value!;

            var existing = await ProductById(db, id, ct);
            if (existing is null) return Results.NotFound(new { error = "Product not found." });

            var refs = await References(db, p, ct);
            if (refs.Refusal is not null) return refs.Refusal;

            var clash = await db.Products.Where(x => x.PartNumber == p.PartNumber)
                .Select(x => x.Id).FirstOrDefaultAsync(ct);
            if (clash is not null && clash != id)
            {
                return Results.Json(new
                {
                    error = $"Part number {p.PartNumber} belongs to another product.",
                }, statusCode: 409);
            }

            // On an edit a blank lead time keeps what the part already had,
            // rather than reaching for the supplier's default: the number on an
            // existing part was put there deliberately, and clearing a field is
            // not a request to change it.
            var stockDays = p.StockDays ?? existing.StockDays;

            await db.Database.ExecuteSqlAsync($"""
                UPDATE "Product"
                   SET "partNumber" = {p.PartNumber}, "name" = {p.Name},
                       "description" = {p.Description}, "manufacturerId" = {p.ManufacturerId},
                       "vehicleSystemId" = {p.VehicleSystemId}, "supplierId" = {p.SupplierId},
                       "basePrice" = {p.BasePrice}, "stockDays" = {stockDays},
                       "partType" = {p.PartType},
                       "goodsCategoryId" = {p.GoodsCategoryId}
                 WHERE "id" = {id}
                """, ct);

            return Results.Ok(new { product = await ProductById(db, id, ct) });
        });

        app.MapDelete("/api/admin/products/{id}", async (
            string id, HttpContext http, AdminGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            var product = await ProductById(db, id, ct);
            if (product is null) return Results.NotFound(new { error = "Product not found." });

            // OrderItem references the product, so removing one that has been
            // ordered would fail at the database and would rewrite order
            // history besides.
            var orders = await db.OrderItems.CountAsync(i => i.ProductId == id, ct);
            if (orders > 0)
            {
                return Results.Json(new
                {
                    error = $"{product.PartNumber} appears on {orders} order line{(orders == 1 ? "" : "s")} " +
                            "and cannot be deleted.",
                }, statusCode: 409);
            }

            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            // Interchange and Fitment are ON DELETE RESTRICT, so the part
            // cannot go while they point at it. Both belong to the part rather
            // than record anything independent of it, so they go with it —
            // unlike an order line, which is why that one is refused above.
            // Pictures, stock rows and basket lines cascade on their own.
            await db.Database.ExecuteSqlAsync($"""DELETE FROM "Interchange" WHERE "sourceId" = {id}""", ct);
            await db.Database.ExecuteSqlAsync($"""DELETE FROM "Fitment" WHERE "productId" = {id}""", ct);
            await db.Database.ExecuteSqlAsync($"""DELETE FROM "Product" WHERE "id" = {id}""", ct);

            await transaction.CommitAsync(ct);
            return Results.Ok(new { ok = true });
        });

        /* ---------------------------------------------------- pictures --- */

        app.MapGet("/api/admin/products/{id}/images", async (
            string id, HttpContext http, AdminGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            if (!await db.Products.AnyAsync(p => p.Id == id, ct))
            {
                return Results.NotFound(new { error = "Product not found." });
            }

            return Results.Ok(new { images = await Images(db, id, ct) });
        });

        app.MapPut("/api/admin/products/{id}/images", async (
            string id, JsonElement body, HttpContext http, AdminGate gate,
            AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            if (!await db.Products.AnyAsync(p => p.Id == id, ct))
            {
                return Results.NotFound(new { error = "Product not found." });
            }

            var parsed = Validators.ReadImages(body);
            if (!parsed.Ok) return Results.BadRequest(new { error = parsed.Error });

            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            // Replaced wholesale, because order is what says which picture
            // leads and a partial write would leave two claiming to be first.
            await db.Database.ExecuteSqlAsync($"""DELETE FROM "ProductImage" WHERE "productId" = {id}""", ct);

            var rows = parsed.Value!;
            for (var i = 0; i < rows.Count; i++)
            {
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO "ProductImage" ("id", "productId", "url", "alt", "sortOrder")
                    VALUES ({Ids.New()}, {id}, {rows[i].Url}, {rows[i].Alt}, {i})
                    """, ct);
            }

            await transaction.CommitAsync(ct);
            return Results.Ok(new { images = await Images(db, id, ct) });
        });

        /* ------------------------------------------------------- stock --- */

        app.MapGet("/api/admin/products/{id}/stock", async (
            string id, HttpContext http, AdminGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            if (!await db.Products.AnyAsync(p => p.Id == id, ct))
            {
                return Results.NotFound(new { error = "Product not found." });
            }

            return Results.Ok(new { levels = await StockLevels(db, id, ct) });
        });

        app.MapPut("/api/admin/products/{id}/stock", async (
            string id, JsonElement body, HttpContext http, AdminGate gate,
            AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            if (!await db.Products.AnyAsync(p => p.Id == id, ct))
            {
                return Results.NotFound(new { error = "Product not found." });
            }

            var parsed = Validators.ReadStockRows(body);
            if (!parsed.Ok) return Results.BadRequest(new { error = parsed.Error });
            var rows = parsed.Value!;

            var warehouseIds = rows.Select(r => r.WarehouseId).ToArray();
            var known = await db.Warehouses.Where(w => warehouseIds.Contains(w.Id))
                .Select(w => w.Id).ToListAsync(ct);
            if (known.Count != warehouseIds.Length)
            {
                return Results.BadRequest(new { error = "Unknown warehouse." });
            }

            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            await db.Database.ExecuteSqlAsync($"""
                DELETE FROM "StockLevel"
                WHERE "productId" = {id} AND NOT EXISTS (SELECT 1 FROM OPENJSON({SqlList.Of(warehouseIds)}) WHERE value COLLATE DATABASE_DEFAULT = "warehouseId")
                """, ct);

            foreach (var row in rows)
            {
                // MERGE with HOLDLOCK, which is SQL Server's ON CONFLICT — see
                // the note in SearchMisses for why the lock is not optional.
                // PostgreSQL's EXCLUDED is `source` here.
                await db.Database.ExecuteSqlAsync($"""
                    MERGE "StockLevel" WITH (HOLDLOCK) AS target
                    USING (VALUES ({id}, {row.WarehouseId}, {row.Quantity},
                                   {row.Reserved}, {row.BinLocation}))
                       AS source("productId", "warehouseId", "quantity", "reserved", "binLocation")
                      ON target."productId" = source."productId"
                     AND target."warehouseId" = source."warehouseId"
                    WHEN MATCHED THEN
                      UPDATE SET "quantity" = source."quantity",
                                 "reserved" = source."reserved",
                                 "binLocation" = source."binLocation",
                                 "updatedAt" = CURRENT_TIMESTAMP
                    WHEN NOT MATCHED THEN
                      INSERT ("id", "productId", "warehouseId", "quantity",
                              "reserved", "binLocation", "updatedAt")
                      VALUES ({Ids.New()}, source."productId", source."warehouseId",
                              source."quantity", source."reserved", source."binLocation",
                              CURRENT_TIMESTAMP);
                    """, ct);
            }

            await transaction.CommitAsync(ct);
            return Results.Ok(new { levels = await StockLevels(db, id, ct) });
        });
    }

    private static async Task<(IResult? Refusal, int? SupplierStockDays)> References(
        AutoPartsContext db, ProductInput p, CancellationToken ct)
    {
        if (!await db.Manufacturers.AnyAsync(m => m.Id == p.ManufacturerId, ct))
        {
            return (Results.BadRequest(new { error = "Unknown manufacturer." }), null);
        }
        if (!await db.VehicleSystems.AnyAsync(v => v.Id == p.VehicleSystemId, ct))
        {
            return (Results.BadRequest(new { error = "Unknown vehicle system." }), null);
        }
        if (p.SupplierId is null) return (null, null);

        var supplier = await db.Suppliers.Where(s => s.Id == p.SupplierId)
            .Select(s => new { s.DefaultStockDays }).FirstOrDefaultAsync(ct);
        if (supplier is null) return (Results.BadRequest(new { error = "Unknown supplier." }), null);

        return (null, supplier.DefaultStockDays);
    }

    private static async Task<AdminProductRow?> ProductById(
        AutoPartsContext db, string id, CancellationToken ct) =>
        (await db.Database.SqlQuery<AdminProductRow>($"""
            SELECT p."id" AS "Id", p."partNumber" AS "PartNumber", p."name" AS "Name",
                   p."description" AS "Description", p."basePrice" AS "BasePrice",
                   p."stockDays" AS "StockDays",
                   p."partType" AS "PartType",
                   p."goodsCategoryId" AS "GoodsCategoryId",
                   g."name" AS "GoodsCategoryName",
                   p."manufacturerId" AS "ManufacturerId", m."name" AS "ManufacturerName",
                   p."vehicleSystemId" AS "VehicleSystemId", v."name" AS "SystemName",
                   p."supplierId" AS "SupplierId", s."name" AS "SupplierName",
                   (SELECT COUNT(*) FROM "Interchange" i WHERE i."sourceId" = p."id") AS "InterchangeCount",
                   (SELECT COUNT(*) FROM "ProductImage" pi WHERE pi."productId" = p."id") AS "ImageCount",
                   img."url" AS "PrimaryImageUrl",
                   st."stockOnHand" AS "StockOnHand", st."stockAvailable" AS "StockAvailable"
            FROM "Product" p
            JOIN "Manufacturer" m ON m."id" = p."manufacturerId"
            JOIN "VehicleSystem" v ON v."id" = p."vehicleSystemId"
            LEFT JOIN "Supplier" s ON s."id" = p."supplierId"
            LEFT JOIN "GoodsCategory" g ON g."id" = p."goodsCategoryId"
            OUTER APPLY (
              SELECT pi."url" FROM "ProductImage" pi
              WHERE pi."productId" = p."id" ORDER BY pi."sortOrder" ASC OFFSET 0 ROWS FETCH NEXT 1 ROWS ONLY
            ) img
            OUTER APPLY (
              SELECT SUM(sl."quantity") AS "stockOnHand",
                     SUM(sl."quantity" - sl."reserved") AS "stockAvailable"
              FROM "StockLevel" sl WHERE sl."productId" = p."id"
            ) st
            WHERE p."id" = {id}
            """).ToListAsync(ct)).FirstOrDefault();

    private static async Task<List<object>> Images(AutoPartsContext db, string id, CancellationToken ct) =>
        (await db.ProductImages.Where(i => i.ProductId == id).OrderBy(i => i.SortOrder)
            .Select(i => new { i.Id, i.Url, i.Alt, i.SortOrder })
            .AsNoTracking().ToListAsync(ct))
        .Select(i => (object)new { id = i.Id, url = i.Url, alt = i.Alt, sortOrder = i.SortOrder })
        .ToList();

    private static async Task<List<object>> StockLevels(AutoPartsContext db, string id, CancellationToken ct) =>
        (await db.Database.SqlQuery<StockLevelRow>($"""
            SELECT s."id" AS "Id", s."warehouseId" AS "WarehouseId",
                   w."name" AS "WarehouseName", w."code" AS "WarehouseCode",
                   s."quantity" AS "Quantity", s."reserved" AS "Reserved",
                   s."binLocation" AS "BinLocation", s."updatedAt" AS "UpdatedAt"
            FROM "StockLevel" s
            JOIN "Warehouse" w ON w."id" = s."warehouseId"
            WHERE s."productId" = {id}
            ORDER BY w."priority" DESC, w."code" ASC
            """).ToListAsync(ct))
        .Select(s => (object)new
        {
            id = s.Id,
            warehouseId = s.WarehouseId,
            warehouseName = s.WarehouseName,
            warehouseCode = s.WarehouseCode,
            quantity = s.Quantity,
            reserved = s.Reserved,
            /* What can still be sold. Derived, never stored — see the schema. */
            available = s.Quantity - s.Reserved,
            binLocation = s.BinLocation,
            updatedAt = Timestamps.Iso(s.UpdatedAt),
        })
        .ToList();

    private record StockLevelRow(
        string Id, string WarehouseId, string WarehouseName, string WarehouseCode,
        int Quantity, int Reserved, string? BinLocation, DateTime UpdatedAt);
}

/// <param name="StockOnHand">
/// Null where nobody has counted the part in, which is not the same as none
/// left. SUM over no shelves is null, and the distinction survives the query
/// rather than being reconstructed afterwards.
/// </param>
/// <param name="PartType">oem | aftermarket | substitute — what the customer would be buying.</param>
public record AdminProductRow(
    string Id, string PartNumber, string Name, string? Description, double BasePrice, int StockDays,
    string PartType,
    string ManufacturerId, string ManufacturerName, string VehicleSystemId, string SystemName,
    string? SupplierId, string? SupplierName,
    /// <summary>The commercial category, and its name so the row can be read.</summary>
    string? GoodsCategoryId, string? GoodsCategoryName,
    int InterchangeCount, int ImageCount, string? PrimaryImageUrl,
    int? StockOnHand, int? StockAvailable);
