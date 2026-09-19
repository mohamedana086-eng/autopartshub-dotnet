using System.Text.Json;
using AutoPartsHub.Api.Admin;
using AutoPartsHub.Api.Auth;
using AutoPartsHub.Api.Catalogue;
using AutoPartsHub.Api.Data;
using AutoPartsHub.Domain.Catalogue;
using AutoPartsHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsHub.Api.Endpoints;

/// <summary>
/// The two writes behind the admin desk: what an account is entitled to, and
/// telling an account something.
/// </summary>
public static class AdminDeskWriteEndpoints
{
    private static readonly string[] Roles = ["ADMIN", "SALES", "B2B", "RETAIL"];

    private static readonly string[] NotificationTypes = ["system", "order", "stock", "account"];

    public static void MapAdminDeskWriteEndpoints(this IEndpointRouteBuilder app)
    {
        // PATCH /api/admin/clients/<id>
        // { role, categoryId, discountPercent, currencyId, salesManagerId }
        app.MapPatch("/api/admin/clients/{id}", async (
            string id, JsonElement body, HttpContext http, AdminGate gate,
            AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            var role = JsonValues.AsString(JsonValues.Get(body, "role"));
            if (!Roles.Contains(role)) return Results.BadRequest(new { error = "Unknown role." });

            var categoryId = Truthy(body, "categoryId");
            if (categoryId is not null && !await db.ClientCategories.AnyAsync(c => c.Id == categoryId, ct))
            {
                return Results.BadRequest(new { error = "Unknown pricing tier." });
            }

            var existing = await ClientById(db, id, ct);
            if (existing is null) return Results.NotFound(new { error = "Client not found." });

            // Percent off the marked-up price — see the order of operations in
            // the pricing engine. Rejected outside 0–100 here rather than
            // clamped, because an admin typing 150 meant something and should
            // be told, where the engine clamps defensively against data that
            // is already stored.
            var sent = JsonValues.Get(body, "discountPercent");
            var discountPercent = sent is null ? existing.DiscountPercent : JsonValues.AsNumber(sent);
            if (discountPercent is not { } discount
                || double.IsNaN(discount) || double.IsInfinity(discount)
                || discount < 0 || discount > 100)
            {
                return Results.BadRequest(new { error = "Discount must be between 0 and 100 percent." });
            }

            var currencyId = Truthy(body, "currencyId");
            if (currencyId is not null)
            {
                var currency = await db.Currencies.Where(c => c.Id == currencyId)
                    .Select(c => new { c.Code, c.Active }).AsNoTracking().FirstOrDefaultAsync(ct);
                if (currency is null) return Results.BadRequest(new { error = "Unknown currency." });
                if (!currency.Active)
                {
                    return Results.BadRequest(new
                    {
                        error = $"{currency.Code} is not active. Activate it before quoting anyone in it.",
                    });
                }
            }

            var salesManagerId = Truthy(body, "salesManagerId");
            if (salesManagerId is not null)
            {
                // An account cannot look after itself, and only SALES staff
                // can own a customer — otherwise a customer could be assigned
                // as another's manager and would gain their orders once SALES
                // scoping lands.
                if (salesManagerId == id)
                {
                    return Results.BadRequest(new { error = "An account cannot be its own sales manager." });
                }
                var manager = await db.Clients.Where(c => c.Id == salesManagerId)
                    .Select(c => new { c.Role, c.Name }).AsNoTracking().FirstOrDefaultAsync(ct);
                if (manager is null) return Results.BadRequest(new { error = "Unknown sales manager." });
                if (manager.Role != "SALES")
                {
                    return Results.BadRequest(new { error = $"{manager.Name} is not a sales account." });
                }
            }

            await db.Database.ExecuteSqlAsync($"""
                UPDATE "Client"
                   SET "role" = {role}, "categoryId" = {categoryId},
                       "discountPercent" = {discount}, "currencyId" = {currencyId},
                       "salesManagerId" = {salesManagerId}
                 WHERE "id" = {id}
                """, ct);

            return Results.Ok(new { client = await ClientById(db, id, ct) });
        });

        // POST /api/admin/notifications — send one to an account.
        //
        // Open to SALES, narrowed to their own customers. Telling a customer
        // their part is in is the same job as looking after that customer, and
        // having to ask an admin to type it made the account a viewer with a
        // title.
        app.MapPost("/api/admin/notifications", async (
            JsonElement body, HttpContext http, AdminGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireOperator(http);
            if (!g.Ok) return g.Response!;

            var scope = g.ScopeTo;

            var clientId = JsonValues.AsString(JsonValues.Get(body, "clientId")).Trim();
            var title = JsonValues.AsString(JsonValues.Get(body, "title")).Trim();
            var rawType = JsonValues.Get(body, "type") is { } t
                ? JsonValues.AsString(t).Trim()
                : "system";
            var messageBody = JsonValues.AsString(JsonValues.Get(body, "body")).Trim();
            var link = JsonValues.AsString(JsonValues.Get(body, "link")).Trim();

            if (clientId.Length == 0) return Results.BadRequest(new { error = "Pick who it goes to." });
            if (title.Length == 0) return Results.BadRequest(new { error = "A title is required." });
            if (title.Length > 200)
            {
                return Results.BadRequest(new { error = "Keep the title under 200 characters." });
            }

            // Narrowed here the same way roles are.
            var type = NotificationTypes.Contains(rawType) ? rawType : "system";

            // Site-relative only. A notification is rendered as a link the
            // recipient clicks, and an off-site destination typed into an
            // admin box is the shape of a phishing link even when nobody meant
            // it that way. `//evil.example` is off-site by every browser's
            // reading, so a bare startsWith('/') is not the check.
            if (link.Length > 0 && !SiteLink.IsSitePath(link))
            {
                return Results.BadRequest(new
                {
                    error = "A link must be a path on this site, starting with /.",
                });
            }

            var id = Ids.New();

            // The recipient is checked by writing to them, not before writing
            // to them. The id comes back from a list a browser sent, so "is
            // this one of mine" and "write to this one" have to be the same
            // statement — checked separately they are one forgotten return
            // apart, and a reassignment in between would land the message with
            // somebody else's customer.
            //
            // INSERT … SELECT is what makes that possible: no row to select
            // means no row inserted.
            var written = await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "Notification" ("id", "clientId", "type", "title", "body", "link")
                SELECT {id}, c."id", {type}, {title},
                       {(messageBody.Length > 0 ? messageBody : null)},
                       {(link.Length > 0 ? link : null)}
                FROM "Client" c
                WHERE c."id" = {clientId}
                  AND ({scope} IS NULL OR c."salesManagerId" = {scope})
                """, ct);

            // Nothing written: the account does not exist, or is not one of
            // this salesperson's. Both answer the same way — to them another
            // manager's customer does not exist, and a distinct refusal would
            // turn this endpoint into a way to enumerate the customer list.
            if (written == 0) return Results.BadRequest(new { error = "Unknown account." });

            var n = (await db.Database.SqlQuery<AdminNotificationRow>($"""
                SELECT n."id" AS "Id", n."clientId" AS "ClientId", c."name" AS "ClientName",
                       n."type" AS "Type", n."title" AS "Title", n."body" AS "Body",
                       n."link" AS "Link", n."readAt" AS "ReadAt", n."createdAt" AS "CreatedAt"
                FROM "Notification" n
                JOIN "Client" c ON c."id" = n."clientId"
                WHERE n."id" = {id}
                """).ToListAsync(ct)).Single();

            return Results.Json(
                new
                {
                    notification = new
                    {
                        id = n.Id,
                        clientId = n.ClientId,
                        clientName = n.ClientName,
                        type = n.Type,
                        title = n.Title,
                        body = n.Body,
                        link = n.Link,
                        readAt = Timestamps.Iso(n.ReadAt),
                        createdAt = Timestamps.Iso(n.CreatedAt),
                    },
                },
                statusCode: 201);
        });
    }

    /// <summary>
    /// <c>body.x ? String(body.x) : null</c> — an id, or nothing.
    /// </summary>
    /// <remarks>
    /// The empty string is how a select with nothing chosen arrives, and it
    /// means "clear this", so it has to read as absent rather than as an id
    /// that will not be found. Zero and false go the same way for the same
    /// reason: they are what an empty control sends, not something to look up.
    /// </remarks>
    private static string? Truthy(JsonElement body, string key)
    {
        var raw = JsonValues.Get(body, key);
        if (raw is not { } e) return null;

        var falsy = e.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.False => true,
            JsonValueKind.String => e.GetString()!.Length == 0,
            JsonValueKind.Number => e.GetDouble() == 0,
            _ => false,
        };

        return falsy ? null : JsonValues.AsString(e);
    }

    private static async Task<AdminClientRow?> ClientById(
        AutoPartsContext db, string id, CancellationToken ct) =>
        (await db.Database.SqlQuery<AdminClientRow>($"""
            SELECT c."id" AS "Id", c."name" AS "Name", c."email" AS "Email", c."role" AS "Role",
                   c."city" AS "City",
                   CAST(CASE WHEN c."passwordHash" IS NOT NULL THEN 1 ELSE 0 END AS bit) AS "HasLogin",
                   c."categoryId" AS "CategoryId", cat."name" AS "CategoryName",
                   c."discountPercent" AS "DiscountPercent",
                   c."currencyId" AS "CurrencyId", cur."code" AS "CurrencyCode",
                   c."salesManagerId" AS "SalesManagerId", m."name" AS "SalesManagerName"
            FROM "Client" c
            LEFT JOIN "ClientCategory" cat ON cat."id" = c."categoryId"
            LEFT JOIN "Currency" cur ON cur."id" = c."currencyId"
            LEFT JOIN "Client" m ON m."id" = c."salesManagerId"
            WHERE c."id" = {id}
            """).ToListAsync(ct)).FirstOrDefault();
}

public record AdminNotificationRow(
    string Id, string ClientId, string ClientName, string Type, string Title,
    string? Body, string? Link, DateTime? ReadAt, DateTime CreatedAt);
