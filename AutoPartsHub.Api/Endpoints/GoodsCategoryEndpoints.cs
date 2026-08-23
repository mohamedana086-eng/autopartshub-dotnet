using System.Text.Json;
using AutoPartsHub.Api.Catalogue;
using AutoPartsHub.Api.Auth;
using AutoPartsHub.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsHub.Api.Endpoints;

/// <summary>
/// Managing the commercial classification the team writes itself.
/// </summary>
/// <remarks>
/// The pricing path does not come through here — it loads the handful of
/// categories that price something straight into the request context. This is
/// the other half: the list, and knowing what each one holds.
///
/// Raw SQL like everything else added since the scaffold, and shaped to answer
/// identically to the Node routes it mirrors.
/// </remarks>
public static class GoodsCategoryEndpoints
{
    public static void MapGoodsCategoryEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /api/admin/goods-categories — the list, with what each one holds.
        app.MapGet("/api/admin/goods-categories", async (
            HttpContext http, AdminGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            return Results.Ok(new { categories = await ListAsync(db, null, ct) });
        });

        // POST /api/admin/goods-categories
        app.MapPost("/api/admin/goods-categories", async (
            JsonElement body, HttpContext http, AdminGate gate, AutoPartsContext db,
            CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            var parsed = GoodsCategoryInput.Read(body);
            if (parsed.Error is not null) return Results.BadRequest(new { error = parsed.Error });
            var value = parsed.Value!;

            var clash = await ClashAsync(db, value.Slug, value.Name, null, ct);
            if (clash is not null) return ClashResponse(clash, value);

            var id = Ids.New();
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "GoodsCategory" ("id", "name", "slug", "description",
                                             "markupType", "markupValue", "sortOrder", "active")
                VALUES ({id}, {value.Name}, {value.Slug}, {value.Description},
                        {value.MarkupType}, {value.MarkupValue}, {value.SortOrder}, {value.Active})
                """, ct);

            return Results.Json(
                new { category = (await ListAsync(db, id, ct)).FirstOrDefault() },
                statusCode: 201);
        });

        // PATCH /api/admin/goods-categories/{id}
        app.MapPatch("/api/admin/goods-categories/{id}", async (
            string id, JsonElement body, HttpContext http, AdminGate gate, AutoPartsContext db,
            CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            var existing = (await ListAsync(db, id, ct)).FirstOrDefault();
            if (existing is null) return Results.NotFound(new { error = "No such category." });

            var parsed = GoodsCategoryInput.Read(body);
            if (parsed.Error is not null) return Results.BadRequest(new { error = parsed.Error });
            var value = parsed.Value!;

            var clash = await ClashAsync(db, value.Slug, value.Name, id, ct);
            if (clash is not null) return ClashResponse(clash, value);

            await db.Database.ExecuteSqlAsync($"""
                UPDATE "GoodsCategory"
                   SET "name" = {value.Name},
                       "slug" = {value.Slug},
                       "description" = {value.Description},
                       "markupType" = {value.MarkupType},
                       "markupValue" = {value.MarkupValue},
                       "sortOrder" = {value.SortOrder},
                       "active" = {value.Active}
                 WHERE "id" = {id}
                """, ct);

            return Results.Ok(new { category = (await ListAsync(db, id, ct)).FirstOrDefault() });
        });

        // DELETE /api/admin/goods-categories/{id}
        //
        // Nothing is destroyed but the category. Its parts fall back to
        // unclassified and any rule scoped to it widens to the whole
        // catalogue, both by SET NULL on the foreign keys — and both are price
        // changes, which is why they are counted back rather than left for
        // somebody to notice.
        app.MapDelete("/api/admin/goods-categories/{id}", async (
            string id, HttpContext http, AdminGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            var existing = (await ListAsync(db, id, ct)).FirstOrDefault();
            if (existing is null) return Results.NotFound(new { error = "No such category." });

            await db.Database.ExecuteSqlAsync(
                $"""DELETE FROM "GoodsCategory" WHERE "id" = {id}""", ct);

            return Results.Ok(new
            {
                removed = existing.Name,
                unclassified = existing.ProductCount,
                widenedRules = existing.RuleCount,
            });
        });
    }

    private static IResult ClashResponse(string clash, GoodsCategoryValues value) =>
        Results.Json(
            new
            {
                error = clash == "name"
                    ? $"A category called {value.Name} already exists."
                    : $"Another category already uses the address {value.Slug}.",
            },
            statusCode: 409);

    private static async Task<string?> ClashAsync(
        AutoPartsContext db, string slug, string name, string? exceptId, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<ClashRow>($"""
            SELECT "slug" AS "Slug", "name" AS "Name" FROM "GoodsCategory"
            WHERE ("slug" = {slug} OR lower("name") = lower({name}))
              AND ({exceptId}::text IS NULL OR "id" <> {exceptId})
            LIMIT 1
            """).ToListAsync(ct);

        var row = rows.FirstOrDefault();
        if (row is null) return null;
        return row.Slug == slug ? "slug" : "name";
    }

    private static Task<List<GoodsCategoryRow>> ListAsync(
        AutoPartsContext db, string? id, CancellationToken ct) =>
        db.Database.SqlQuery<GoodsCategoryRow>($"""
            SELECT g."id" AS "Id", g."name" AS "Name", g."slug" AS "Slug",
                   g."description" AS "Description",
                   g."markupType" AS "MarkupType", g."markupValue" AS "MarkupValue",
                   g."sortOrder" AS "SortOrder", g."active" AS "Active",
                   -- Counted in the query rather than by loading the parts.
                   -- The list wants the number, not the rows behind it.
                   COALESCE(p."n", 0)::int AS "ProductCount",
                   COALESCE(r."n", 0)::int AS "RuleCount"
            FROM "GoodsCategory" g
            LEFT JOIN LATERAL (
              SELECT COUNT(*) AS n FROM "Product" WHERE "goodsCategoryId" = g."id"
            ) p ON true
            LEFT JOIN LATERAL (
              SELECT COUNT(*) AS n FROM "MarkupRule" WHERE "goodsCategoryId" = g."id"
            ) r ON true
            WHERE ({id}::text IS NULL OR g."id" = {id})
            ORDER BY g."sortOrder" ASC, g."name" ASC
            """).ToListAsync(ct);

    private record ClashRow(string Slug, string Name);
}

public record GoodsCategoryRow(
    string Id,
    string Name,
    string Slug,
    string? Description,
    string? MarkupType,
    double? MarkupValue,
    int SortOrder,
    bool Active,
    /// <summary>How many parts are in it — whether it is worth having.</summary>
    int ProductCount,
    /// <summary>
    /// How many markup rules are scoped to it. Shown because deleting the
    /// category widens those rules to the whole catalogue, which is a price
    /// change the admin should see coming.
    /// </summary>
    int RuleCount);
