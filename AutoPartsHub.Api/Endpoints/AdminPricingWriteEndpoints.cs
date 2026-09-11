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
/// Writing what the catalogue is priced with: currencies, client tiers and
/// markup rules.
/// </summary>
/// <remarks>
/// The base currency is the one thing here that cannot be edited freely. It is
/// the unit every other rate is quoted against, so its rate is 1 by definition
/// and deactivating it would leave every price denominated in something the
/// catalogue no longer carries.
/// </remarks>
public static class AdminPricingWriteEndpoints
{
    public static void MapAdminPricingWriteEndpoints(this IEndpointRouteBuilder app)
    {
        /* ------------------------------------------------- currencies --- */

        app.MapPost("/api/admin/currencies", async (
            JsonElement body, HttpContext http, AdminGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            var input = Validators.ReadCurrency(body);
            if (!input.Ok) return Results.BadRequest(new { error = input.Error });
            var c = input.Value!;

            if (await db.Currencies.AnyAsync(x => x.Code == c.Code, ct))
            {
                return Results.Json(new { error = $"{c.Code} is already on the list." }, statusCode: 409);
            }

            // Never created as base: which currency prices are denominated in
            // is a property of the catalogue, not something a create form gets
            // to assert.
            var id = Ids.New();
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "Currency" ("id", "code", "name", "symbol", "rate", "isBase", "active")
                VALUES ({id}, {c.Code}, {c.Name}, {c.Symbol}, {c.Rate}, FALSE, {c.Active})
                """, ct);

            return Results.Json(new { currency = await CurrencyById(db, id, ct) }, statusCode: 201);
        });

        app.MapPatch("/api/admin/currencies/{id}", async (
            string id, JsonElement body, HttpContext http, AdminGate gate,
            AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            var existing = await CurrencyById(db, id, ct);
            if (existing is null) return Results.NotFound(new { error = "Currency not found." });

            var input = Validators.ReadCurrency(body);
            if (!input.Ok) return Results.BadRequest(new { error = input.Error });
            var c = input.Value!;

            // The base is the unit everything else is quoted against, so its
            // rate is 1 by definition. Editing it would rescale the entire
            // catalogue without changing a single stored price.
            if (existing.IsBase && c.Rate != 1)
            {
                return Results.Json(
                    new { error = $"{existing.Code} is the base currency. Its rate is always 1." },
                    statusCode: 409);
            }
            if (existing.IsBase && !c.Active)
            {
                return Results.Json(
                    new { error = $"{existing.Code} is the base currency and cannot be deactivated." },
                    statusCode: 409);
            }

            if (await db.Currencies.AnyAsync(x => x.Code == c.Code && x.Id != id, ct))
            {
                return Results.Json(new { error = $"{c.Code} is already on the list." }, statusCode: 409);
            }

            await db.Database.ExecuteSqlAsync($"""
                UPDATE "Currency"
                   SET "code" = {c.Code}, "name" = {c.Name}, "symbol" = {c.Symbol},
                       "rate" = {c.Rate}, "active" = {c.Active}
                 WHERE "id" = {id}
                """, ct);

            return Results.Ok(new { currency = await CurrencyById(db, id, ct) });
        });

        app.MapDelete("/api/admin/currencies/{id}", async (
            string id, HttpContext http, AdminGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            var currency = await CurrencyById(db, id, ct);
            if (currency is null) return Results.NotFound(new { error = "Currency not found." });

            if (currency.IsBase)
            {
                return Results.Json(new
                {
                    error = $"{currency.Code} is the base currency — every price is denominated in it.",
                }, statusCode: 409);
            }

            // Accounts referencing it would silently fall back to the base and
            // be quoted different numbers than yesterday. Say so instead.
            if (currency.ClientCount > 0)
            {
                return Results.Json(new
                {
                    error = $"{currency.Code} is used by {currency.ClientCount} " +
                            $"account{(currency.ClientCount == 1 ? "" : "s")}. " +
                            "Move them to another currency first.",
                }, statusCode: 409);
            }

            // Conditions name a currency by its CODE, because that is what a
            // request knows it is being quoted in — so the code has to be read
            // before the row holding it goes.
            var code = await db.Currencies.Where(c => c.Id == id).Select(c => c.Code)
                .FirstOrDefaultAsync(ct);
            if (code is not null) await MarkupRules.ForgetValue(db, "currency", code, ct);

            // Past orders keep their own copy of the code and rate, so deleting
            // a currency no account uses cannot disturb order history.
            await db.Database.ExecuteSqlAsync($"""DELETE FROM "Currency" WHERE "id" = {id}""", ct);
            return Results.Ok(new { ok = true });
        });

        /* ----------------------------------------------- client tiers --- */

        app.MapPost("/api/admin/client-categories", async (
            JsonElement body, HttpContext http, AdminGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            var name = JsonValues.AsString(JsonValues.Get(body, "name")).Trim();
            if (name.Length == 0) return Results.BadRequest(new { error = "Name is required." });

            var markup = JsonValues.AsNumber(JsonValues.Get(body, "markupPercent")) ?? double.NaN;
            var minOrder = JsonValues.AsNumber(JsonValues.Get(body, "minOrderAmount")) ?? double.NaN;
            var shelfRaw = JsonValues.Get(body, "shelfLifeDays");
            var shelf = shelfRaw is null ? 1 : JsonValues.AsNumber(shelfRaw) ?? double.NaN;

            if (double.IsNaN(markup) || double.IsInfinity(markup)
                || double.IsNaN(minOrder) || double.IsInfinity(minOrder)
                || double.IsNaN(shelf) || double.IsInfinity(shelf))
            {
                return Results.BadRequest(
                    new { error = "Markup, minimum order and shelf life must be numbers." });
            }

            var id = Ids.New();
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "ClientCategory" ("id", "name", "markupPercent", "minOrderAmount", "shelfLifeDays")
                VALUES ({id}, {name}, {markup}, {minOrder}, {(int)shelf})
                """, ct);

            return Results.Json(new
            {
                category = new
                {
                    id,
                    name,
                    markupPercent = markup,
                    minOrderAmount = minOrder,
                    shelfLifeDays = (int)shelf,
                    clientCount = 0,
                },
            }, statusCode: 201);
        });

        app.MapDelete("/api/admin/client-categories/{id}", async (
            string id, HttpContext http, AdminGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            var name = await db.ClientCategories.Where(c => c.Id == id).Select(c => c.Name)
                .FirstOrDefaultAsync(ct);
            if (name is null) return Results.NotFound(new { error = "Category not found." });

            // Client.categoryId references this row, so deleting it out from
            // under a client fails at the database — say why instead of
            // surfacing a foreign-key error. A pricing-tier condition on a
            // markup rule has no key to fail on at all, which is the better
            // reason to check it: the rule would silently go on naming a tier
            // that no longer exists.
            var clients = await db.Clients.CountAsync(c => c.CategoryId == id, ct);
            if (clients > 0)
            {
                return Results.Json(new
                {
                    error = $"{name} still has {clients} client{(clients == 1 ? "" : "s")}. " +
                            "Move them to another tier first.",
                }, statusCode: 409);
            }

            var rules = await db.MarkupRuleConditions
                .CountAsync(c => c.Dimension == "clientCategory" && c.Value == id, ct);
            if (rules > 0)
            {
                return Results.Json(new
                {
                    error = $"{name} is used by {rules} markup rule{(rules == 1 ? "" : "s")}. " +
                            "Delete or retarget those first.",
                }, statusCode: 409);
            }

            await db.Database.ExecuteSqlAsync($"""DELETE FROM "ClientCategory" WHERE "id" = {id}""", ct);
            return Results.Ok(new { ok = true });
        });

        /* ----------------------------------------------- markup rules --- */

        app.MapPost("/api/admin/markup-rules", async (
            JsonElement body, HttpContext http, AdminGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            var (rule, error) = MarkupRules.ParseBody(body);
            if (error is not null) return Results.BadRequest(new { error });

            var id = await MarkupRules.Create(db, rule!, ct);

            return Results.Json(
                new { rule = await MarkupRules.ById(db, id, ct) }, statusCode: 201);
        });

        // POST /api/admin/markup-rules/ladder — a whole ladder of margin bands
        // at once.
        //
        // What it writes is ordinary markup rules, one per band. There is no
        // ladder table, no ladder id and nothing marking these rules as
        // belonging together: the moment they exist they are rules like any
        // other, editable and deletable one at a time. That is deliberate. A
        // ladder is a way of SAYING something, not a second kind of thing for
        // the engine to know about — and a rule that belonged to a ladder would
        // be a rule somebody could not safely edit.
        //
        // It adds rather than replaces. A ladder that swept away the rules it
        // thought were its own would eventually sweep away one an admin had
        // edited by hand for a reason nobody wrote down.
        app.MapPost("/api/admin/markup-rules/ladder", async (
            JsonElement body, HttpContext http, AdminGate gate, AutoPartsContext db,
            CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            // The refusals are the point of this endpoint: a gap or an overlap
            // between bands is invisible on a screen that writes one rule at a
            // time, and shows up months later as one part in a thousand priced
            // from the tier default.
            var read = MarkupLadders.Read(body);
            if (!read.Ok) return Results.BadRequest(new { error = read.Error });

            var ladder = read.Value!;

            var ids = await MarkupRules.CreateLadder(
                db,
                [.. ladder.Rungs.Select(rung => new MarkupRules.RuleWrite(
                    MarkupLadders.RungLabel(ladder.Label, rung),
                    0,
                    [],
                    rung.From,
                    rung.To,
                    ladder.Type,
                    rung.Value,
                    ladder.MinAmount,
                    null,
                    null))],
                ct);

            var rules = new List<object?>();
            foreach (var id in ids) rules.Add(await MarkupRules.ById(db, id, ct));

            return Results.Json(new { rules }, statusCode: 201);
        });

        // PUT /api/admin/markup-rules/<id> — the whole rule, conditions and all.
        //
        // A rewrite rather than a patch because the conditions are a set:
        // sending "these two suppliers" has to be able to mean the rule now
        // names two and no longer names the third, and a merge could not say
        // that.
        app.MapPut("/api/admin/markup-rules/{id}", async (
            string id, JsonElement body, HttpContext http, AdminGate gate,
            AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            var (rule, error) = MarkupRules.ParseBody(body);
            if (error is not null) return Results.BadRequest(new { error });

            if (!await db.MarkupRules.AnyAsync(r => r.Id == id, ct))
            {
                return Results.NotFound(new { error = "Rule not found." });
            }

            await MarkupRules.Update(db, id, rule!, ct);

            return Results.Ok(new { rule = await MarkupRules.ById(db, id, ct) });
        });

        // PATCH /api/admin/markup-rules/<id> { active }
        app.MapPatch("/api/admin/markup-rules/{id}", async (
            string id, JsonElement body, HttpContext http, AdminGate gate,
            AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            var raw = JsonValues.Get(body, "active");
            if (raw is not { ValueKind: JsonValueKind.True or JsonValueKind.False })
            {
                return Results.BadRequest(new { error = "active must be true or false." });
            }
            var active = raw.Value.ValueKind == JsonValueKind.True;

            if (!await db.MarkupRules.AnyAsync(r => r.Id == id, ct))
            {
                return Results.NotFound(new { error = "Rule not found." });
            }

            await db.Database.ExecuteSqlAsync(
                $"""UPDATE "MarkupRule" SET "active" = {active} WHERE "id" = {id}""", ct);

            return Results.Ok(new { id, active });
        });

        app.MapDelete("/api/admin/markup-rules/{id}", async (
            string id, HttpContext http, AdminGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            if (!await db.MarkupRules.AnyAsync(r => r.Id == id, ct))
            {
                return Results.NotFound(new { error = "Rule not found." });
            }

            await db.Database.ExecuteSqlAsync($"""DELETE FROM "MarkupRule" WHERE "id" = {id}""", ct);
            return Results.Ok(new { ok = true });
        });
    }

    private static async Task<AdminCurrencyRow?> CurrencyById(
        AutoPartsContext db, string id, CancellationToken ct) =>
        (await db.Database.SqlQuery<AdminCurrencyRow>($"""
            SELECT c."id" AS "Id", c."code" AS "Code", c."name" AS "Name", c."symbol" AS "Symbol",
                   c."rate" AS "Rate", c."isBase" AS "IsBase", c."active" AS "Active",
                   n."count" AS "ClientCount"
            FROM "Currency" c
            OUTER APPLY (
              SELECT COUNT(*) AS "count" FROM "Client" cl WHERE cl."currencyId" = c."id"
            ) n
            WHERE c."id" = {id}
            """).ToListAsync(ct)).FirstOrDefault();

}
