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
/// A supplier signing themselves up, and an admin letting them in.
/// </summary>
/// <remarks>
/// The account and the supplier record are one act, so they are one
/// transaction: an account with no supplier behind it could sign in and see an
/// empty version of a screen that makes no sense, and a supplier with no
/// account is a row nobody can reach.
///
/// They arrive switched off, through <c>Supplier.active</c> — the same column
/// Warehouse, RetailOutlet, Currency, MarkupRule and PriceList carry, meaning
/// the same thing. There is no separate approval state and no workflow table.
/// The point is that a new supplier can write their whole catalogue and load
/// their prices while none of it is for sale, and then go live in one move.
/// </remarks>
public static class SupplierSignupEndpoints
{
    private const int MinPassword = 6;
    private const int MaxCode = 12;

    public static void MapSupplierSignupEndpoints(this IEndpointRouteBuilder app)
    {
        // POST /api/auth/register-supplier
        // { company, code, email, password, contactName, country, description }
        //
        // Sign-up belongs under /api/auth with the other two — a supplier
        // registering is registering, and the noun in the old path made it
        // look like a write to the supplier list, which is a different thing
        // an admin does.
        //
        // It is also still served at /api/suppliers/register, where the
        // storefront posts today. The API and the Angular app are separate
        // deployments, so a route moves in three steps — serve both, move the
        // caller, drop the old one — and this is the first. CONTRACTS.md lists
        // it among the differences to settle.
        app.MapPost("/api/auth/register-supplier", RegisterSupplier);
        app.MapPost("/api/suppliers/register", RegisterSupplier);

        static async Task<IResult> RegisterSupplier(
            JsonElement body, AutoPartsContext db, SessionTokens tokens,
            HttpContext http, IHostEnvironment env, CancellationToken ct)
        {
            string Text(string key) => JsonValues.AsString(JsonValues.Get(body, key)).Trim();

            var company = Text("company");
            var email = Text("email").ToLowerInvariant();
            var password = JsonValues.AsString(JsonValues.Get(body, "password"));
            var code = Text("code").ToUpperInvariant();
            var contactName = Text("contactName") is { Length: > 0 } n ? n : company;
            var country = Text("country") is { Length: > 0 } c ? c : null;
            var description = Text("description") is { Length: > 0 } d ? d : null;

            if (company.Length == 0 || email.Length == 0 || password.Length == 0 || code.Length == 0)
            {
                return Results.BadRequest(new { error = "Company, code, email and password are all required." });
            }
            if (password.Length < MinPassword)
            {
                return Results.BadRequest(new { error = $"Password must be at least {MinPassword} characters." });
            }
            // The code goes on invoices and into part numbers, and is typed by
            // hand often enough that length is worth holding down.
            if (code.Length > MaxCode)
            {
                return Results.BadRequest(new { error = $"Keep the code under {MaxCode} characters." });
            }
            if (!code.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-'))
            {
                return Results.BadRequest(new { error = "A code may use letters, numbers and hyphens only." });
            }

            var slug = Validators.Slugify(company);
            if (slug.Length == 0)
            {
                return Results.BadRequest(new { error = "Could not make a web address from that company name." });
            }

            // Both refusals name what clashed rather than reporting a
            // constraint. An applicant told "that code is taken" picks another
            // one; an applicant shown a unique-index violation writes an email.
            //
            // What is deliberately NOT said is who holds it. "IB16 belongs to
            // Ibrahim Auto Parts" turns a signup form into a way to enumerate
            // the supplier list, and the public directory already lists the
            // ones who are trading — so naming them here would leak exactly
            // the ones it does not.
            if (await db.Clients.AnyAsync(x => x.Email == email, ct))
            {
                return Results.Json(
                    new { error = "An account with this email already exists. Sign in instead." },
                    statusCode: 409);
            }

            var clash = await db.Suppliers
                .Where(s => s.Code == code || s.Slug == slug)
                .Select(s => new { s.Code })
                .FirstOrDefaultAsync(ct);
            if (clash is not null)
            {
                return Results.Json(
                    new
                    {
                        error = clash.Code == code
                            ? $"The code {code} is already taken. Choose another."
                            : "A supplier with a very similar company name is already registered. "
                              + "Add something that tells you apart.",
                    },
                    statusCode: 409);
            }

            var supplierId = Ids.New();
            var clientId = Ids.New();

            // Ten rounds, the cost every hash already in this table was made
            // at — the two APIs share one login.
            var hash = BCrypt.Net.BCrypt.HashPassword(password, AuthEndpoints.BcryptRounds);

            await using (var transaction = await db.Database.BeginTransactionAsync(ct))
            {
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO "Supplier" ("id", "name", "code", "slug", "description", "country",
                                            "active", "approvedAt")
                    VALUES ({supplierId}, {company}, {code}, {slug}, {description}, {country},
                            FALSE, NULL)
                    """, ct);

                // No categoryId: a pricing tier is what a *customer* is quoted
                // on, and a supplier account is not buying anything. Null keeps
                // them out of every tier report rather than parking them in
                // Retail.
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO "Client" ("id", "name", "email", "role", "passwordHash", "supplierId")
                    VALUES ({clientId}, {contactName}, {email}, 'SUPPLIER', {hash}, {supplierId})
                    """, ct);

                await transaction.CommitAsync(ct);
            }

            // Sent after the account exists, and not inside its transaction: a
            // notification that fails is not a reason to lose a registration,
            // and an admin told about a supplier the rollback removed is worse
            // than one told late.
            //
            // Every admin gets one. Approval is not a named person's job, and a
            // queue that depends on one inbox stops when they are away.
            var admins = await db.Clients.Where(c => c.Role == Roles.Admin)
                .OrderBy(c => c.CreatedAt).Select(c => c.Id).ToListAsync(ct);
            foreach (var adminId in admins)
            {
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO "Notification" ("id", "clientId", "type", "title", "body", "link")
                    VALUES ({Ids.New()}, {adminId}, 'account',
                            {$"{company} has applied to supply"},
                            {$"Code {code}. They can load parts and prices now; nothing goes on sale until you approve them."},
                            '/admin/suppliers/waiting')
                    """, ct);
            }

            AuthEndpoints.Issue(http, tokens, env, new SessionPayload(
                clientId, Roles.SupplierRole, null, contactName,
                DateTimeOffset.UtcNow.Add(SessionTokens.MaxAge).ToUnixTimeMilliseconds()));

            return Results.Json(
                new
                {
                    user = new { id = clientId, name = contactName, email, role = Roles.SupplierRole },
                    supplier = new { id = supplierId, name = company, code, slug, active = false },
                    // Said in the response rather than left for the UI to know,
                    // so every client that calls this tells the applicant the
                    // same thing.
                    status = "waiting",
                    message = "Your account is ready. Add your parts and prices now — they go on sale "
                            + "as soon as an administrator approves you.",
                },
                statusCode: 201);
        }

        // GET /api/admin/suppliers/waiting — who has applied and not been let in.
        //
        // ADMIN only, not staff. Approving a supplier puts their whole
        // catalogue on sale, which is a decision about what the shop sells
        // rather than about one salesperson's customers.
        app.MapGet("/api/admin/suppliers/waiting", async (
            HttpContext http, AdminGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            // `approvedAt IS NULL` is the half that matters. Filtering on
            // `active = false` alone would put every supplier who has ever been
            // suspended back into the approval queue, where an admin would
            // approve them a second time without noticing they were reinstating
            // somebody rather than admitting them.
            var suppliers = await db.Database.SqlQuery<WaitingSupplierRow>($"""
                SELECT s."id" AS "Id", s."code" AS "Code", s."slug" AS "Slug", s."name" AS "Name",
                       s."description" AS "Description", s."country" AS "Country",
                       n."count"::int AS "ProductCount",
                       c."name" AS "ContactName", c."email" AS "ContactEmail",
                       to_char(c."createdAt", 'YYYY-MM-DD"T"HH24:MI:SS.MS"Z"') AS "SignedUpAt"
                FROM "Supplier" s
                LEFT JOIN LATERAL (
                  SELECT COUNT(*) AS "count" FROM "Product" p WHERE p."supplierId" = s."id"
                ) n ON TRUE
                LEFT JOIN LATERAL (
                  SELECT cl."name", cl."email", cl."createdAt"
                  FROM "Client" cl
                  WHERE cl."supplierId" = s."id"
                  ORDER BY cl."createdAt" ASC
                  LIMIT 1
                ) c ON TRUE
                WHERE s."active" = FALSE AND s."approvedAt" IS NULL
                ORDER BY c."createdAt" ASC NULLS LAST, s."name" ASC
                """).ToListAsync(ct);

            return Results.Ok(new { suppliers });
        });

        // PATCH /api/admin/suppliers/<id>/approval { active }
        //
        // One switch, three meanings depending on where the supplier is:
        // turning it on for the first time is approval, turning it on again is
        // reinstatement, and turning it off is suspension. They share an
        // endpoint because they share a column. `approvedAt` records which of
        // the two "on" cases happened, and is only ever written once.
        //
        // Its own route rather than a field on the supplier editor: the editor
        // writes every field it is given, and putting a whole catalogue on sale
        // should not be a side effect of correcting a phone number.
        app.MapPatch("/api/admin/suppliers/{id}/approval", async (
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

            var supplier = await State(db, id, ct);
            if (supplier is null) return Results.NotFound(new { error = "Supplier not found." });

            if (supplier.Active == active)
            {
                return Results.Json(
                    new
                    {
                        error = active
                            ? $"{supplier.Name} is already trading."
                            : $"{supplier.Name} is already switched off.",
                    },
                    statusCode: 409);
            }

            // Whether this is the first time decides what the supplier is told,
            // so it is read before the write rather than inferred afterwards.
            var firstTime = supplier.ApprovedAt is null;

            if (active)
            {
                // COALESCE, so reinstating a suspended supplier leaves the
                // original date alone: that is when they were admitted, and it
                // did not happen twice.
                await db.Database.ExecuteSqlAsync($"""
                    UPDATE "Supplier"
                       SET "active" = TRUE,
                           "approvedAt" = COALESCE("approvedAt", CURRENT_TIMESTAMP)
                     WHERE "id" = {id}
                    """, ct);
            }
            else
            {
                await db.Database.ExecuteSqlAsync(
                    $"""UPDATE "Supplier" SET "active" = FALSE WHERE "id" = {id}""", ct);
            }

            // The people who have been waiting are the ones who most need to
            // know. Every account on the supplier, not just the one that
            // applied — by the time approval comes a company may have added
            // colleagues.
            var accounts = await db.Clients.Where(c => c.SupplierId == id)
                .OrderBy(c => c.CreatedAt).Select(c => c.Id).ToListAsync(ct);

            var title = active
                ? firstTime ? "Your account has been approved" : "Your account is trading again"
                : "Your account has been switched off";
            var message = active
                ? "Everything you have loaded is now on sale."
                : "Your parts have been taken out of the catalogue. Nothing you have loaded is lost.";

            foreach (var clientId in accounts)
            {
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO "Notification" ("id", "clientId", "type", "title", "body", "link")
                    VALUES ({Ids.New()}, {clientId}, 'account', {title}, {message}, '/supplier')
                    """, ct);
            }

            var after = (await State(db, id, ct))!;

            return Results.Ok(new
            {
                supplier = new { id = after.Id, name = after.Name, active = after.Active, approvedAt = after.ApprovedAt },
                // Which of the three things just happened, so a client does not
                // have to work it out from a boolean and a date.
                action = active ? (firstTime ? "approved" : "reinstated") : "suspended",
            });
        });
    }

    private static async Task<ApprovalStateRow?> State(
        AutoPartsContext db, string id, CancellationToken ct) =>
        (await db.Database.SqlQuery<ApprovalStateRow>($"""
            SELECT "id" AS "Id", "name" AS "Name", "active" AS "Active",
                   to_char("approvedAt", 'YYYY-MM-DD"T"HH24:MI:SS.MS"Z"') AS "ApprovedAt"
            FROM "Supplier" WHERE "id" = {id}
            """).ToListAsync(ct)).FirstOrDefault();
}

/// <param name="ProductCount">What they have loaded while waiting — what approval would put on sale.</param>
public record WaitingSupplierRow(
    string Id, string Code, string Slug, string Name, string? Description, string? Country,
    int ProductCount, string? ContactName, string? ContactEmail, string? SignedUpAt);

public record ApprovalStateRow(string Id, string Name, bool Active, string? ApprovedAt);
