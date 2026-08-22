using System.Text.Json;
using AutoPartsHub.Api.Admin;
using AutoPartsHub.Api.Auth;
using AutoPartsHub.Api.Catalogue;
using AutoPartsHub.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsHub.Api.Endpoints;

/// <summary>
/// Uploading, renaming and removing purchase price lists.
/// </summary>
/// <remarks>
/// ADMIN only, deliberately — not staff. These set what every part costs to
/// buy, which is the number the whole markup engine multiplies up. A
/// salesperson seeing their own customers is one thing; changing the cost
/// basis of the catalogue is another.
/// </remarks>
public static class AdminPriceListWriteEndpoints
{
    /// <summary>Lines per insert. An upload is one statement per chunk, not one per row.</summary>
    private const int InsertChunk = 5000;

    public static void MapAdminPriceListWriteEndpoints(this IEndpointRouteBuilder app)
    {
        // POST /api/admin/price-lists — upload one.
        //
        // It arrives inactive whatever it says. Uploading and switching the
        // catalogue onto a new cost basis are two decisions, and running them
        // together means a mistyped column changes every price before anyone
        // has looked at the result. The response reports what matched and what
        // did not; activating is a second, deliberate request.
        app.MapPost("/api/admin/price-lists", async (
            JsonElement body, HttpContext http, AdminGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            var details = PriceLists.ReadListDetails(body);
            if (!details.Ok) return Results.BadRequest(new { error = details.Error });

            // Match on the normalised form, the way search and the bulk lookup
            // do, so a supplier's spacing does not decide whether their price
            // lands.
            var products = await db.Products
                .Select(p => new { p.Id, p.PartNumber }).AsNoTracking().ToListAsync(ct);
            var currencies = await db.Currencies
                .Select(c => new { c.Code, c.Rate }).AsNoTracking().ToListAsync(ct);

            var productIdByPartNumber = new Dictionary<string, string>();
            foreach (var p in products) productIdByPartNumber[PartNumbers.Normalise(p.PartNumber)] = p.Id;

            var ratesByCode = new Dictionary<string, ConversionRate>();
            foreach (var c in currencies)
            {
                ratesByCode[c.Code.ToUpperInvariant()] = new ConversionRate(c.Code, c.Rate);
            }

            var parsed = PriceLists.ReadPriceRows(
                JsonValues.Get(body, "rows"), productIdByPartNumber, ratesByCode);
            if (!parsed.Ok) return Results.BadRequest(new { error = parsed.Error });

            var list = await CreateAsync(db, details.Value!, parsed.Value!.Rows, ct);

            return Results.Json(
                new
                {
                    list = Serialise(list),
                    accepted = parsed.Value.Rows.Count,
                    // Capped in the response only; every rejection is counted,
                    // and the first few are named so the admin can see the
                    // shape of what went wrong without the payload carrying a
                    // whole failed file back.
                    rejectedCount = parsed.Value.Rejected.Count,
                    rejected = parsed.Value.Rejected.Take(50),
                },
                statusCode: 201);
        });

        // PATCH /api/admin/price-lists/<id> — rename it, or switch it on and off.
        //
        // Activating is the interesting half. At most one list may be active
        // and the database enforces that with a partial unique index, so the
        // previous one has to be stood down in the same transaction —
        // otherwise the write fails, which is the constraint doing its job but
        // not an error anyone should have to see. Switching a list on
        // therefore switches the other off, which is exactly what "activate
        // this one instead" means.
        app.MapPatch("/api/admin/price-lists/{id}", async (
            string id, JsonElement body, HttpContext http, AdminGate gate,
            AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            if (await ById(db, id, ct) is null)
            {
                return Results.NotFound(new { error = "Price list not found." });
            }

            string? name = null;
            var nameSent = JsonValues.Get(body, "name") is not null;
            if (nameSent)
            {
                name = JsonValues.AsString(JsonValues.Get(body, "name")).Trim();
                if (name.Length == 0) return Results.BadRequest(new { error = "Give the list a name." });
                if (name.Length > 120)
                {
                    return Results.BadRequest(new { error = "Keep the name under 120 characters." });
                }
            }

            string? description = null;
            var descriptionSent = JsonValues.Get(body, "description") is not null;
            if (descriptionSent)
            {
                description = JsonValues.AsString(JsonValues.Get(body, "description")).Trim();
                if (description.Length == 0) description = null;
            }

            var activeRaw = JsonValues.Get(body, "active");
            bool? active = activeRaw is null ? null : activeRaw.Value.ValueKind == JsonValueKind.True;

            await UpdateAsync(db, id, nameSent, name, descriptionSent, description, active, ct);

            return Results.Ok(new { list = Serialise((await ById(db, id, ct))!) });
        });

        // DELETE /api/admin/price-lists/<id>
        //
        // The active list cannot be deleted while it is active. Deleting it
        // would reprice the whole catalogue back to `basePrice` as a side
        // effect of what reads like housekeeping — so standing it down has to
        // be its own deliberate act first, and then the consequence is already
        // on screen.
        app.MapDelete("/api/admin/price-lists/{id}", async (
            string id, HttpContext http, AdminGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            var existing = await ById(db, id, ct);
            if (existing is null) return Results.NotFound(new { error = "Price list not found." });

            if (existing.Active)
            {
                return Results.Json(
                    new
                    {
                        error = "That list is the one setting prices right now. Switch it off first — "
                              + "every part it covers goes back to its own price.",
                    },
                    statusCode: 409);
            }

            // Its lines go with it: the cascade is on the foreign key.
            await db.Database.ExecuteSqlAsync($"""DELETE FROM "PriceList" WHERE "id" = {id}""", ct);

            return Results.Ok(new { ok = true });
        });
    }

    /// <summary>
    /// Stores an upload: the list, then its lines, in one transaction.
    /// </summary>
    /// <remarks>
    /// The lines go in as six arrays unnested into rows rather than one
    /// statement per line — a supplier file is tens of thousands of prices,
    /// and that many round trips is the difference between a request and a
    /// timeout.
    ///
    /// <c>updatedAt</c> is set here because the column is NOT NULL with no
    /// default. The ORM the other API dropped filled it in on the way past;
    /// nothing else does.
    /// </remarks>
    private static async Task<PriceListRow> CreateAsync(
        AutoPartsContext db, ListDetails details, List<PricedRow> rows, CancellationToken ct)
    {
        var id = Ids.New();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "PriceList" ("id", "name", "description", "sourceName", "active", "updatedAt")
            VALUES ({id}, {details.Name}, {details.Description}, {details.SourceName},
                    FALSE, CURRENT_TIMESTAMP)
            """, ct);

        for (var at = 0; at < rows.Count; at += InsertChunk)
        {
            var chunk = rows.GetRange(at, Math.Min(InsertChunk, rows.Count - at));

            var ids = chunk.Select(_ => Ids.New()).ToArray();
            var listIds = chunk.Select(_ => id).ToArray();
            var productIds = chunk.Select(r => r.ProductId).ToArray();
            var prices = chunk.Select(r => r.Price).ToArray();
            var sourcePrices = chunk.Select(r => r.SourcePrice).ToArray();
            var sourceCurrencies = chunk.Select(r => r.SourceCurrency).ToArray();

            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "PriceListItem" ("id", "priceListId", "productId", "price",
                                             "sourcePrice", "sourceCurrency")
                SELECT * FROM unnest(
                  {ids}::text[],
                  {listIds}::text[],
                  {productIds}::text[],
                  {prices}::double precision[],
                  {sourcePrices}::double precision[],
                  {sourceCurrencies}::text[]
                )
                """, ct);
        }

        await transaction.CommitAsync(ct);

        return (await ById(db, id, ct))!;
    }

    /// <summary>
    /// Renames a list, switches it on or off, or both.
    /// </summary>
    /// <remarks>
    /// Each field is written through a CASE on whether the caller sent it,
    /// rather than by ignoring nulls: a description can be cleared, and null
    /// is the way that is said.
    /// </remarks>
    private static async Task UpdateAsync(
        AutoPartsContext db, string id, bool nameSent, string? name,
        bool descriptionSent, string? description, bool? active, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        if (active == true)
        {
            await db.Database.ExecuteSqlAsync($"""
                UPDATE "PriceList" SET "active" = FALSE WHERE "active" = TRUE AND "id" <> {id}
                """, ct);
        }

        await db.Database.ExecuteSqlAsync($"""
            UPDATE "PriceList"
               SET "name" = CASE WHEN {nameSent} THEN {name}::text ELSE "name" END,
                   "description" = CASE WHEN {descriptionSent} THEN {description}::text
                                        ELSE "description" END,
                   "active" = CASE WHEN {active is not null} THEN {active}::boolean
                                   ELSE "active" END,
                   "updatedAt" = CURRENT_TIMESTAMP
             WHERE "id" = {id}
            """, ct);

        await transaction.CommitAsync(ct);
    }

    /// <summary>Active first, then newest — the one setting prices is the one being read.</summary>
    private static async Task<PriceListRow?> ById(
        AutoPartsContext db, string id, CancellationToken ct) =>
        (await db.Database.SqlQuery<PriceListRow>($"""
            SELECT l."id" AS "Id", l."name" AS "Name", l."description" AS "Description",
                   l."active" AS "Active", l."sourceName" AS "SourceName",
                   n."count"::int AS "ItemCount",
                   l."createdAt" AS "CreatedAt", l."updatedAt" AS "UpdatedAt"
            FROM "PriceList" l
            LEFT JOIN LATERAL (
              SELECT COUNT(*) AS "count" FROM "PriceListItem" i WHERE i."priceListId" = l."id"
            ) n ON TRUE
            WHERE l."id" = {id}
            """).ToListAsync(ct)).FirstOrDefault();

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
