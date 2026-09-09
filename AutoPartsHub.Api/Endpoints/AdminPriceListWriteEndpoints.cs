using System.Text.Json;
using AutoPartsHub.Api.Admin;
using AutoPartsHub.Api.Auth;
using AutoPartsHub.Api.Catalogue;
using AutoPartsHub.Api.Data;
using AutoPartsHub.Api.Pricing;
using AutoPartsHub.Domain.Catalogue;
using AutoPartsHub.Domain.Pricing;
using AutoPartsHub.Domain;
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
            JsonElement body, HttpContext http, AdminGate gate, AutoPartsContext db,
            ILoggerFactory loggers, CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            var details = PriceLists.ReadListDetails(body);
            if (!details.Ok) return Results.BadRequest(new { error = details.Error });

            var rowsField = JsonValues.Get(body, "rows");
            var rowsSent = rowsField is { ValueKind: JsonValueKind.Array } sent
                ? sent.GetArrayLength()
                : 0;

            // Writes the attempt down, and never fails the request for doing
            // so. An upload that stored forty thousand prices and then could
            // not write its own log entry is still an upload that stored forty
            // thousand prices. Losing the log is worth a line in the server
            // log; it is not worth telling an admin their prices did not land
            // when they did.
            async Task<string?> Log(
                string? priceListId, string outcome, int accepted, string? error,
                List<RejectedRow> rejected)
            {
                try
                {
                    return await RecordImportAsync(
                        db,
                        new ImportWrite(
                            priceListId,
                            details.Value!.Name,
                            details.Value.SourceName,
                            g.Session!.UserId,
                            g.Session.Name,
                            outcome,
                            rowsSent,
                            accepted,
                            rejected.Count,
                            error),
                        rejected,
                        ct);
                }
                catch (Exception cause)
                {
                    loggers.CreateLogger("PriceListImports")
                        .LogError(cause, "Could not record the price list import.");
                    return null;
                }
            }

            // Match on the normalised form, the way search and the bulk lookup
            // do, so a supplier's spacing does not decide whether their price
            // lands.
            // `cost` is what each part costs to buy TODAY: the active list's
            // figure where it covers the part, the part's own basePrice where
            // it does not — the same fallback the pricing engine applies. What
            // an upload is measured against has to be what it would actually
            // replace, not a stored number the catalogue may not be using.
            var products = await db.Database.SqlQuery<CataloguePriceRow>($"""
                SELECT p."id" AS "Id", p."partNumber" AS "PartNumber",
                       COALESCE(a."price", p."basePrice") AS "Cost"
                FROM "Product" p
                LEFT JOIN "PriceList" l ON l."active" = TRUE
                LEFT JOIN "PriceListItem" a ON a."priceListId" = l."id" AND a."productId" = p."id"
                """).ToListAsync(ct);

            // Exact equivalents only. A close or partial substitute is a
            // different part, and reading this part's cost off it would be
            // pricing one thing from the invoice for another.
            var interchanges = await db.Database.SqlQuery<InterchangeTargetRow>($"""
                SELECT "sourceId" AS "ProductId", "targetPartNo" AS "TargetPartNumber"
                FROM "Interchange"
                WHERE "exactMatch" = TRUE
                """).ToListAsync(ct);

            var currencies = await db.Currencies
                .Select(c => new { c.Code, c.Rate }).AsNoTracking().ToListAsync(ct);

            var productIdByPartNumber = new Dictionary<string, string>();
            var costNow = new Dictionary<string, double>();
            foreach (var p in products)
            {
                productIdByPartNumber[PartNumbers.Normalise(p.PartNumber)] = p.Id;
                costNow[p.Id] = p.Cost;
            }

            // Several of our parts can name the same equivalent, so this is a
            // list. The reader refuses an ambiguous one rather than picking
            // from it.
            var productIdsByInterchange = new Dictionary<string, List<string>>();
            foreach (var i in interchanges)
            {
                var key = PartNumbers.Normalise(i.TargetPartNumber);
                // A number that is already one of our own is not a
                // substitution — the direct match finds it first, and letting
                // it in here would only add a second route to the same answer.
                if (productIdByPartNumber.ContainsKey(key)) continue;
                if (productIdsByInterchange.TryGetValue(key, out var found))
                {
                    if (!found.Contains(i.ProductId)) found.Add(i.ProductId);
                }
                else
                {
                    productIdsByInterchange[key] = [i.ProductId];
                }
            }

            var ratesByCode = new Dictionary<string, ConversionRate>();
            foreach (var c in currencies)
            {
                ratesByCode[c.Code.ToUpperInvariant()] = new ConversionRate(c.Code, c.Rate);
            }

            var parsed = PriceLists.ReadPriceRows(
                rowsField, productIdByPartNumber, productIdsByInterchange, ratesByCode);

            if (!parsed.Ok)
            {
                var refusedId = await Log(null, "REFUSED", 0, parsed.Error, parsed.Rejected);

                // The id goes back with the refusal so the screen that reports
                // it can link straight to the lines, rather than sending the
                // admin to a history page to find the upload they are already
                // looking at.
                return Results.BadRequest(new { error = parsed.Error, importId = refusedId });
            }

            // What the file would do to the prices already in force, read
            // before anything is written because the answer can refuse the
            // whole upload. A real price update moves most parts a little; a
            // column read from the wrong place moves nearly everything by an
            // absurd multiple, and that is visible in the shape of the file
            // before anyone reads a line of it.
            var movement = PriceLists.ReadPriceMovement(parsed.Value!.Rows, costNow);
            var confirmed = JsonValues.Get(body, "confirmLargeChange") is { ValueKind: JsonValueKind.True };

            if (PriceLists.MovementIsAlarming(movement) && !confirmed)
            {
                var refusal = PriceLists.MovementRefusal(movement);
                var stoppedId = await Log(
                    null, "REFUSED", 0, refusal, parsed.Value.Rejected);

                // `movement` goes back with it. The refusal has to be arguable
                // — an admin who knows the move is real needs to see the same
                // numbers the guard saw before deciding to send it again.
                return Results.Json(
                    new { error = refusal, importId = stoppedId, movement },
                    statusCode: 409);
            }

            var list = await CreateAsync(db, details.Value!, parsed.Value.Rows, ct);

            var importId = await Log(
                list.Id, "STORED", parsed.Value.Rows.Count, null, parsed.Value.Rejected);

            return Results.Json(
                new
                {
                    list = Serialise(list),
                    importId,
                    accepted = parsed.Value.Rows.Count,
                    // What it will do when it is switched on. Reported on every
                    // upload, not only the alarming ones: "what does this file
                    // change" is the question an admin has before activating,
                    // and a count of accepted rows does not answer it.
                    movement,
                    // Capped in the response only; every rejection is counted,
                    // the first few are named so the admin can see the shape of
                    // what went wrong without the payload carrying a whole
                    // failed file back, and the rest are read through
                    // `importId` rather than being gone.
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

            // The margin everything on this file earns. Sent as null to take it
            // away again, which is why "was it sent" is a separate question
            // from what it holds — null and 0 are both meaningful here and
            // neither means "unchanged".
            var markupSent = JsonValues.Get(body, "markupPercent") is not null;
            var markup = PurchaseMarkups.Read(JsonValues.Get(body, "markupPercent"), "The list markup");
            if (!markup.Ok) return Results.BadRequest(new { error = markup.Error });

            await UpdateAsync(
                db, id, nameSent, name, descriptionSent, description, active,
                markupSent, markup.Value, ct);

            return Results.Ok(new { list = Serialise((await ById(db, id, ct))!) });
        });

        // PATCH /api/admin/price-lists/<id>/items/<productId> — one line's own
        // margin.
        //
        // Its own route rather than a field on the list PATCH. A list has one
        // name and forty thousand lines, and a body that could carry either
        // would have to be read twice — once as "rename this list" and once as
        // "reprice this part".
        //
        // Addressed by (list, part) because that is the pair the screen is
        // looking at and the pair the unique index is on. A line id would be
        // neither: the same part on the same list is a different row every time
        // the file is loaded again, so a link to one would rot on the next
        // upload.
        app.MapPatch("/api/admin/price-lists/{id}/items/{productId}", async (
            string id, string productId, JsonElement body, HttpContext http, AdminGate gate,
            AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            if (await ById(db, id, ct) is null)
            {
                return Results.NotFound(new { error = "Price list not found." });
            }

            // Absent is the one thing this route has nothing to do: a PATCH
            // whose body mentions no margin is not "clear it", it is a request
            // that forgot to say what it wanted. Null IS a request — it takes
            // the line's margin away.
            if (JsonValues.Get(body, "markupPercent") is null)
            {
                return Results.BadRequest(
                    new { error = "Send a markupPercent, or null to clear it." });
            }

            var markup = PurchaseMarkups.Read(JsonValues.Get(body, "markupPercent"), "The line markup");
            if (!markup.Ok) return Results.BadRequest(new { error = markup.Error });

            var written = await db.Database.ExecuteSqlAsync($"""
                UPDATE "PriceListItem"
                   SET "markupPercent" = {markup.Value}::double precision
                 WHERE "priceListId" = {id} AND "productId" = {productId}
                """, ct);

            if (written == 0)
            {
                // A part the list does not carry. Reported rather than shrugged
                // off: a silent success over a line that is not there reads as
                // saved, and the margin somebody typed would be gone the next
                // time the screen loaded.
                return Results.NotFound(new { error = "That part is not on this list." });
            }

            return Results.Ok(new { productId, markupPercent = markup.Value });
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

            // Its lines go with it: the cascade is on the foreign key. Its
            // conditions do not — nothing points at a condition value — so they
            // are swept by hand.
            await MarkupRules.ForgetValue(db, "priceList", id, ct);

            await db.Database.ExecuteSqlAsync($"""DELETE FROM "PriceList" WHERE "id" = {id}""", ct);

            return Results.Ok(new { ok = true });
        });
    }

    /// <summary>
    /// Writes down what an upload did.
    /// </summary>
    /// <remarks>
    /// Deliberately its own transaction rather than part of <see
    /// cref="CreateAsync"/>: a refused file never gets that far and still has
    /// to be recorded, so the log cannot be a step inside the write it is
    /// logging.
    ///
    /// At most <see cref="PriceLists.StoredRejections"/> lines are kept. The
    /// count on the import row is always exact; it is the transcript that
    /// stops, and the two numbers are stored separately so a screen can say
    /// which it is showing.
    /// </remarks>
    private static async Task<string> RecordImportAsync(
        AutoPartsContext db, ImportWrite input, List<RejectedRow> rejected, CancellationToken ct)
    {
        var id = Ids.New();
        var stored = rejected.Take(PriceLists.StoredRejections).ToList();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "PriceListImport" ("id", "priceListId", "listName", "sourceName",
                                           "uploadedById", "uploadedByName", "outcome",
                                           "rowsSent", "accepted", "rejected", "rejectedStored",
                                           "error")
            VALUES ({id}, {input.PriceListId}, {input.ListName}, {input.SourceName},
                    {input.UploadedById}, {input.UploadedByName}, {input.Outcome},
                    {input.RowsSent}, {input.Accepted}, {input.Rejected}, {stored.Count},
                    {input.Error})
            """, ct);

        for (var at = 0; at < stored.Count; at += InsertChunk)
        {
            var chunk = stored.GetRange(at, Math.Min(InsertChunk, stored.Count - at));

            var ids = chunk.Select(_ => Ids.New()).ToArray();
            var importIds = chunk.Select(_ => id).ToArray();
            var lines = chunk.Select(r => r.Line).ToArray();
            var partNumbers = chunk.Select(r => r.PartNumber).ToArray();
            var prices = chunk.Select(r => r.Price).ToArray();
            var currencies = chunk.Select(r => r.Currency).ToArray();
            var reasons = chunk.Select(r => r.Reason).ToArray();

            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "PriceListImportRow" ("id", "importId", "line", "partNumber",
                                                  "price", "currency", "reason")
                SELECT * FROM unnest(
                  {ids}::text[],
                  {importIds}::text[],
                  {lines}::int[],
                  {partNumbers}::text[],
                  {prices}::text[],
                  {currencies}::text[],
                  {reasons}::text[]
                )
                """, ct);
        }

        await transaction.CommitAsync(ct);

        return id;
    }

    /// <summary>
    /// Stores an upload: the list, then its lines, in one transaction.
    /// </summary>
    /// <remarks>
    /// The lines go in as seven arrays unnested into rows rather than one
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
            var sourcePartNumbers = chunk.Select(r => r.SourcePartNumber).ToArray();

            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "PriceListItem" ("id", "priceListId", "productId", "price",
                                             "sourcePrice", "sourceCurrency", "sourcePartNumber")
                SELECT * FROM unnest(
                  {ids}::text[],
                  {listIds}::text[],
                  {productIds}::text[],
                  {prices}::double precision[],
                  {sourcePrices}::double precision[],
                  {sourceCurrencies}::text[],
                  {sourcePartNumbers}::text[]
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
        bool descriptionSent, string? description, bool? active,
        bool markupSent, double? markupPercent, CancellationToken ct)
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
                   -- Null is a value here rather than an absence: it is how the
                   -- margin is taken away again, so the CASE asks whether the
                   -- field was sent, not whether it holds anything.
                   "markupPercent" = CASE WHEN {markupSent}
                                          THEN {markupPercent}::double precision
                                          ELSE "markupPercent" END,
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
                   l."markupPercent" AS "MarkupPercent",
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
        markupPercent = l.MarkupPercent,
        itemCount = l.ItemCount,
        createdAt = Timestamps.Iso(l.CreatedAt),
        updatedAt = Timestamps.Iso(l.UpdatedAt),
    };
}

/// <summary>A part to match an upload against, and what it costs to buy today.</summary>
/// <param name="Cost">
/// The active list's figure where it covers the part, the part's own
/// <c>basePrice</c> where it does not — the same fallback the pricing engine
/// applies, so an upload is measured against what it would actually replace.
/// </param>
public record CataloguePriceRow(string Id, string PartNumber, double Cost);

/// <summary>One cross-reference number, and the part of ours it is equivalent to.</summary>
public record InterchangeTargetRow(string ProductId, string TargetPartNumber);
