using System.Text.Json;
using AutoPartsHub.Api.Admin;
using AutoPartsHub.Api.Auth;
using AutoPartsHub.Api.Catalogue;
using AutoPartsHub.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsHub.Api.Endpoints;

/// <summary>
/// Creating, editing and deleting suppliers, warehouses and outlets.
/// </summary>
/// <remarks>
/// Every delete here refuses rather than cascades, and the refusal names what
/// is in the way. The distinction that decides which: a supplier's foreign
/// keys are ON DELETE SET NULL, so the database would allow the delete and
/// quietly unsource the parts — nothing but the check below stops it. A
/// warehouse's allocations are ON DELETE RESTRICT, so the database would stop
/// it, with an error no admin can read.
/// </remarks>
public static class AdminSiteWriteEndpoints
{
    public static void MapAdminSiteWriteEndpoints(this IEndpointRouteBuilder app)
    {
        /* -------------------------------------------------- suppliers --- */

        app.MapPost("/api/admin/suppliers", async (
            JsonElement body, HttpContext http, AdminGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            var input = Validators.ReadSupplier(body);
            if (!input.Ok) return Results.BadRequest(new { error = input.Error });
            var s = input.Value!;

            // Code and slug are both unique. Checked here so the admin is told
            // which one clashed and who holds it, rather than shown a violation
            // naming an index.
            var clash = await Clash(db, s.Code, s.Slug, null, ct);
            if (clash is not null) return Results.Json(new { error = ClashMessage(clash, s) }, statusCode: 409);

            var id = Ids.New();
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "Supplier" ("id", "name", "code", "slug", "description", "reliability",
                                        "rating", "acceptsReturns", "country", "guaranteeMonths",
                                        "defaultStockDays", "purchaseCurrencyId")
                VALUES ({id}, {s.Name}, {s.Code}, {s.Slug}, {s.Description}, {s.Reliability},
                        {s.Rating}, {s.AcceptsReturns}, {s.Country}, {s.GuaranteeMonths},
                        {s.DefaultStockDays}, {s.PurchaseCurrencyId})
                """, ct);

            return Results.Json(new { supplier = await SupplierById(db, id, ct) }, statusCode: 201);
        });

        // PATCH /api/admin/suppliers/<id>
        //
        // Two shapes on purpose. A body carrying nothing but the quick fields
        // sets just those — which is what the star control and the returns
        // toggle in the list send, and it means classifying a supplier cannot
        // accidentally rewrite their code or the url their page lives at. Any
        // other body is treated as a full edit and validated as one.
        app.MapPatch("/api/admin/suppliers/{id}", async (
            string id, JsonElement body, HttpContext http, AdminGate gate,
            AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            if (await SupplierById(db, id, ct) is null)
            {
                return Results.NotFound(new { error = "Supplier not found." });
            }

            var keys = body.ValueKind == JsonValueKind.Object
                ? body.EnumerateObject().Select(p => p.Name).ToList()
                : [];
            var quickOnly = keys.Count > 0 && keys.All(k => k is "rating" or "acceptsReturns");

            if (quickOnly)
            {
                var hasRating = keys.Contains("rating");
                var hasReturns = keys.Contains("acceptsReturns");

                int? rating = null;
                if (hasRating)
                {
                    var r = Validators.ReadRating(JsonValues.Get(body, "rating"));
                    if (!r.Ok) return Results.BadRequest(new { error = r.Error });
                    rating = r.Value;
                }

                bool? returns = null;
                if (hasReturns)
                {
                    var r = Validators.ReadTriState(JsonValues.Get(body, "acceptsReturns"), "Returns");
                    if (!r.Ok) return Results.BadRequest(new { error = r.Error });
                    returns = r.Value;
                }

                // Each field is written through a CASE on whether the caller
                // sent it. Null is a value in both — unrated is not zero, and
                // unknown return terms are not a refusal — so ignoring nulls
                // would make clearing a rating impossible.
                await db.Database.ExecuteSqlAsync($"""
                    UPDATE "Supplier"
                       SET "rating" = CASE WHEN {hasRating} THEN {rating}::int ELSE "rating" END,
                           "acceptsReturns" = CASE WHEN {hasReturns}
                                                   THEN {returns}::boolean
                                                   ELSE "acceptsReturns" END
                     WHERE "id" = {id}
                    """, ct);

                return Results.Ok(new { supplier = await SupplierById(db, id, ct) });
            }

            var input = Validators.ReadSupplier(body);
            if (!input.Ok) return Results.BadRequest(new { error = input.Error });
            var s = input.Value!;

            var clash = await Clash(db, s.Code, s.Slug, id, ct);
            if (clash is not null) return Results.Json(new { error = ClashMessage(clash, s) }, statusCode: 409);

            await db.Database.ExecuteSqlAsync($"""
                UPDATE "Supplier"
                   SET "name" = {s.Name}, "code" = {s.Code}, "slug" = {s.Slug},
                       "description" = {s.Description}, "reliability" = {s.Reliability},
                       "rating" = {s.Rating}, "acceptsReturns" = {s.AcceptsReturns},
                       "country" = {s.Country}, "guaranteeMonths" = {s.GuaranteeMonths},
                       "defaultStockDays" = {s.DefaultStockDays},
                       "purchaseCurrencyId" = {s.PurchaseCurrencyId}
                 WHERE "id" = {id}
                """, ct);

            return Results.Ok(new { supplier = await SupplierById(db, id, ct) });
        });

        app.MapDelete("/api/admin/suppliers/{id}", async (
            string id, HttpContext http, AdminGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            var supplier = await SupplierById(db, id, ct);
            if (supplier is null) return Results.NotFound(new { error = "Supplier not found." });

            // Product.supplierId and MarkupRule.supplierId both point here, and
            // both are ON DELETE SET NULL — so the database would allow this
            // and quietly unsource the parts and change what they cost.
            // Nothing stops it but this check.
            var products = await db.Products.CountAsync(p => p.SupplierId == id, ct);
            if (products > 0)
            {
                return Results.Json(new
                {
                    error = $"{supplier.Name} still sources {products} part{(products == 1 ? "" : "s")}. " +
                            "Move those to another supplier first.",
                }, statusCode: 409);
            }

            var rules = await db.MarkupRules.CountAsync(r => r.SupplierId == id, ct);
            if (rules > 0)
            {
                return Results.Json(new
                {
                    error = $"{supplier.Name} is used by {rules} markup rule{(rules == 1 ? "" : "s")}. " +
                            "Delete or retarget those first.",
                }, statusCode: 409);
            }

            await db.Database.ExecuteSqlAsync($"""DELETE FROM "Supplier" WHERE "id" = {id}""", ct);
            return Results.Ok(new { ok = true });
        });

        /* ------------------------------------------------- warehouses --- */

        app.MapPost("/api/admin/warehouses", async (
            JsonElement body, HttpContext http, AdminGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            var input = Validators.ReadWarehouse(body);
            if (!input.Ok) return Results.BadRequest(new { error = input.Error });
            var w = input.Value!;

            if (await db.Warehouses.AnyAsync(x => x.Code == w.Code, ct))
            {
                return Results.Json(new { error = $"{w.Code} is already a warehouse code." }, statusCode: 409);
            }

            var id = Ids.New();
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "Warehouse" ("id", "code", "name", "city", "address", "active", "priority")
                VALUES ({id}, {w.Code}, {w.Name}, {w.City}, {w.Address}, {w.Active}, {w.Priority})
                """, ct);

            return Results.Json(new { warehouse = await WarehouseById(db, id, ct) }, statusCode: 201);
        });

        app.MapPatch("/api/admin/warehouses/{id}", async (
            string id, JsonElement body, HttpContext http, AdminGate gate,
            AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            if (await WarehouseById(db, id, ct) is null)
            {
                return Results.NotFound(new { error = "Warehouse not found." });
            }

            var input = Validators.ReadWarehouse(body);
            if (!input.Ok) return Results.BadRequest(new { error = input.Error });
            var w = input.Value!;

            if (await db.Warehouses.AnyAsync(x => x.Code == w.Code && x.Id != id, ct))
            {
                return Results.Json(new { error = $"{w.Code} is already a warehouse code." }, statusCode: 409);
            }

            await db.Database.ExecuteSqlAsync($"""
                UPDATE "Warehouse"
                   SET "code" = {w.Code}, "name" = {w.Name}, "city" = {w.City},
                       "address" = {w.Address}, "active" = {w.Active}, "priority" = {w.Priority}
                 WHERE "id" = {id}
                """, ct);

            return Results.Ok(new { warehouse = await WarehouseById(db, id, ct) });
        });

        app.MapDelete("/api/admin/warehouses/{id}", async (
            string id, HttpContext http, AdminGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            var warehouse = await WarehouseById(db, id, ct);
            if (warehouse is null) return Results.NotFound(new { error = "Warehouse not found." });

            // The stock rows would cascade away with it, taking the count of
            // every part held here with them. Deactivating keeps the numbers
            // and stops the picking, which is what closing a site means.
            if (warehouse.TotalQuantity > 0)
            {
                return Results.Json(new
                {
                    error = $"{warehouse.Code} still holds {warehouse.TotalQuantity} " +
                            $"unit{(warehouse.TotalQuantity == 1 ? "" : "s")}. " +
                            "Move the stock out, or set the warehouse inactive instead.",
                }, statusCode: 409);
            }

            // Allocations are ON DELETE RESTRICT, and one outlives the shipment
            // that consumed it — so a warehouse can be empty and still be named
            // by old orders. Without this the delete reaches the database and
            // fails there, which is a 500 telling the admin nothing.
            var held = await db.OrderItemAllocations.CountAsync(a => a.WarehouseId == id, ct);
            if (held > 0)
            {
                return Results.Json(new
                {
                    error = $"{warehouse.Code} is named by {held} order line{(held == 1 ? "" : "s")} " +
                            "and cannot be deleted. Set it inactive instead.",
                }, statusCode: 409);
            }

            // Outlets survive on their own — the foreign key sets their
            // warehouse to null rather than deleting the shop counter.
            await db.Database.ExecuteSqlAsync($"""DELETE FROM "Warehouse" WHERE "id" = {id}""", ct);

            return Results.Ok(new { ok = true, orphanedOutlets = warehouse.OutletCount });
        });

        /* ---------------------------------------------------- outlets --- */

        app.MapPost("/api/admin/outlets", async (
            JsonElement body, HttpContext http, AdminGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            var input = Validators.ReadOutlet(body);
            if (!input.Ok) return Results.BadRequest(new { error = input.Error });
            var o = input.Value!;

            if (await db.RetailOutlets.AnyAsync(x => x.Code == o.Code, ct))
            {
                return Results.Json(new { error = $"{o.Code} is already an outlet code." }, statusCode: 409);
            }
            if (o.WarehouseId is not null && !await db.Warehouses.AnyAsync(w => w.Id == o.WarehouseId, ct))
            {
                return Results.BadRequest(new { error = "Unknown warehouse." });
            }

            var id = Ids.New();
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "RetailOutlet" ("id", "code", "name", "city", "address", "phone",
                                            "warehouseId", "active")
                VALUES ({id}, {o.Code}, {o.Name}, {o.City}, {o.Address}, {o.Phone},
                        {o.WarehouseId}, {o.Active})
                """, ct);

            return Results.Json(new { outlet = await OutletById(db, id, ct) }, statusCode: 201);
        });

        app.MapPatch("/api/admin/outlets/{id}", async (
            string id, JsonElement body, HttpContext http, AdminGate gate,
            AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            if (await OutletById(db, id, ct) is null)
            {
                return Results.NotFound(new { error = "Outlet not found." });
            }

            var input = Validators.ReadOutlet(body);
            if (!input.Ok) return Results.BadRequest(new { error = input.Error });
            var o = input.Value!;

            if (await db.RetailOutlets.AnyAsync(x => x.Code == o.Code && x.Id != id, ct))
            {
                return Results.Json(new { error = $"{o.Code} is already an outlet code." }, statusCode: 409);
            }
            if (o.WarehouseId is not null && !await db.Warehouses.AnyAsync(w => w.Id == o.WarehouseId, ct))
            {
                return Results.BadRequest(new { error = "Unknown warehouse." });
            }

            await db.Database.ExecuteSqlAsync($"""
                UPDATE "RetailOutlet"
                   SET "code" = {o.Code}, "name" = {o.Name}, "city" = {o.City},
                       "address" = {o.Address}, "phone" = {o.Phone},
                       "warehouseId" = {o.WarehouseId}, "active" = {o.Active}
                 WHERE "id" = {id}
                """, ct);

            return Results.Ok(new { outlet = await OutletById(db, id, ct) });
        });

        app.MapDelete("/api/admin/outlets/{id}", async (
            string id, HttpContext http, AdminGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            if (await OutletById(db, id, ct) is null)
            {
                return Results.NotFound(new { error = "Outlet not found." });
            }

            // Nothing references an outlet yet, so there is nothing to refuse
            // for. When orders learn to be collected from one, this needs the
            // same guard the product delete has.
            await db.Database.ExecuteSqlAsync($"""DELETE FROM "RetailOutlet" WHERE "id" = {id}""", ct);

            return Results.Ok(new { ok = true });
        });
    }

    private static async Task<SupplierClash?> Clash(
        AutoPartsContext db, string code, string slug, string? exceptId, CancellationToken ct) =>
        (await db.Database.SqlQuery<SupplierClash>($"""
            SELECT "id" AS "Id", "name" AS "Name", "code" AS "Code"
            FROM "Supplier"
            WHERE ("code" = {code} OR "slug" = {slug})
              AND ({exceptId}::text IS NULL OR "id" <> {exceptId})
            LIMIT 1
            """).ToListAsync(ct)).FirstOrDefault();

    private static string ClashMessage(SupplierClash clash, SupplierInput s) =>
        clash.Code == s.Code
            ? $"Code {s.Code} is already used by {clash.Name}."
            : $"The url /supplier/{s.Slug} is already used by {clash.Name}.";

    private static async Task<AdminSupplierRow?> SupplierById(
        AutoPartsContext db, string id, CancellationToken ct) =>
        (await db.Database.SqlQuery<AdminSupplierRow>($"""
            SELECT s."id" AS "Id", s."code" AS "Code", s."slug" AS "Slug", s."name" AS "Name",
                   s."description" AS "Description", s."reliability" AS "Reliability",
                   s."rating" AS "Rating", s."acceptsReturns" AS "AcceptsReturns",
                   s."country" AS "Country", s."guaranteeMonths" AS "GuaranteeMonths",
                   s."defaultStockDays" AS "DefaultStockDays",
                   s."purchaseCurrencyId" AS "PurchaseCurrencyId",
                   c."code" AS "PurchaseCurrencyCode",
                   s."active" AS "Active", to_char(s."approvedAt", 'YYYY-MM-DD"T"HH24:MI:SS.MS"Z"') AS "ApprovedAt",
                   p."count"::int AS "ProductCount"
            FROM "Supplier" s
            LEFT JOIN "Currency" c ON c."id" = s."purchaseCurrencyId"
            LEFT JOIN LATERAL (
              SELECT COUNT(*) AS "count" FROM "Product" pr WHERE pr."supplierId" = s."id"
            ) p ON TRUE
            WHERE s."id" = {id}
            """).ToListAsync(ct)).FirstOrDefault();

    private static async Task<AdminWarehouseRow?> WarehouseById(
        AutoPartsContext db, string id, CancellationToken ct) =>
        (await db.Database.SqlQuery<AdminWarehouseRow>($"""
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
            WHERE w."id" = {id}
            """).ToListAsync(ct)).FirstOrDefault();

    private static async Task<AdminOutletRow?> OutletById(
        AutoPartsContext db, string id, CancellationToken ct) =>
        (await db.Database.SqlQuery<AdminOutletRow>($"""
            SELECT o."id" AS "Id", o."code" AS "Code", o."name" AS "Name", o."city" AS "City",
                   o."address" AS "Address", o."phone" AS "Phone",
                   o."warehouseId" AS "WarehouseId",
                   w."name" AS "WarehouseName", w."code" AS "WarehouseCode",
                   o."active" AS "Active"
            FROM "RetailOutlet" o
            LEFT JOIN "Warehouse" w ON w."id" = o."warehouseId"
            WHERE o."id" = {id}
            """).ToListAsync(ct)).FirstOrDefault();

    private record SupplierClash(string Id, string Name, string Code);
}
