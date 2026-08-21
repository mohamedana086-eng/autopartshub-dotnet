using AutoPartsHub.Api.Auth;
using AutoPartsHub.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsHub.Api.Endpoints;

/// <summary>
/// The lists an admin or salesperson works from.
/// </summary>
/// <remarks>
/// Three of these are scoped, and the scope is a condition inside the query
/// rather than a filter applied afterwards — a filter is one forgotten return
/// away from serving the whole customer list. The manager id is passed as a
/// parameter and the condition spelled <c>{scope}::text IS NULL OR …</c>, so
/// admin and salesperson run the same statement and there is no branch where
/// the narrowing could be dropped.
/// </remarks>
public static class AdminDeskEndpoints
{
    private const int CartLimit = 200;
    private const int NotificationLimit = 200;

    public static void MapAdminDeskEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /api/admin/stats — the figures on the dashboard.
        //
        // Staff, not admin-only: the dashboard is the first page the panel
        // opens on, so an admin-only endpoint here meant every salesperson's
        // session began on an error.
        app.MapGet("/api/admin/stats", async (
            HttpContext http, AdminGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireStaff(http);
            if (!g.Ok) return g.Response!;

            var scope = g.ScopeTo;

            var counts = (await db.Database.SqlQuery<DashboardRow>($"""
                SELECT (SELECT COUNT(*) FROM "Product")::int AS "Products",
                       (SELECT COUNT(*) FROM "Client" c
                         WHERE ({scope}::text IS NULL OR c."salesManagerId" = {scope})
                       )::int AS "Clients",
                       (SELECT COUNT(*) FROM "Order" o
                          JOIN "Client" c ON c."id" = o."clientId"
                         WHERE ({scope}::text IS NULL OR c."salesManagerId" = {scope})
                       )::int AS "Orders",
                       (SELECT COUNT(*) FROM "MarkupRule" WHERE "active")::int AS "ActiveRules"
                """).ToListAsync(ct)).Single();

            return Results.Ok(new
            {
                /* What the figures cover, so the page can label them honestly. */
                scope = g.IsAdmin ? "all" : "own",
                // Catalogue-wide for everyone: the storefront search is public,
                // so the size of the catalogue is not something staff are being
                // shown early.
                products = counts.Products,
                clients = counts.Clients,
                orders = counts.Orders,
                // Left out for SALES rather than scoped, because there is no
                // such thing as their share of the markup rules: pricing is
                // admin-only, and a number they can neither reach nor act on
                // is furniture.
                activeRules = g.IsAdmin ? counts.ActiveRules : (int?)null,
            });
        });

        // GET /api/admin/orders
        app.MapGet("/api/admin/orders", async (
            HttpContext http, AdminGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireStaff(http);
            if (!g.Ok) return g.Response!;

            var scope = g.ScopeTo;

            var orders = await db.Database.SqlQuery<AdminOrderRow>($"""
                SELECT o."id" AS "Id", o."reference" AS "Reference",
                       cl."name" AS "ClientName", o."status" AS "Status",
                       o."createdAt" AS "CreatedAt",
                       o."currencyCode" AS "CurrencyCode", o."currencyRate" AS "CurrencyRate"
                FROM "Order" o
                JOIN "Client" cl ON cl."id" = o."clientId"
                WHERE ({scope}::text IS NULL OR cl."salesManagerId" = {scope})
                ORDER BY o."createdAt" DESC
                """).ToListAsync(ct);

            var ids = orders.Select(o => o.Id).ToArray();

            // The parts themselves, not just a count of them. A unit total
            // answers "how much" and nothing else — four of one part and one
            // each of four read identically — so the lines come down with the
            // list rather than behind a request per row the table would have
            // to fire while being scrolled.
            //
            // By line id, which for cuids is the order they were written in.
            var lines = await db.Database.SqlQuery<AdminOrderLineRow>($"""
                SELECT oi."orderId" AS "OrderId", oi."productId" AS "ProductId",
                       p."partNumber" AS "PartNumber", p."name" AS "Name",
                       m."name" AS "Manufacturer", vs."name" AS "System",
                       oi."quantity" AS "Quantity", oi."unitPrice" AS "UnitPrice"
                FROM "OrderItem" oi
                JOIN "Product" p ON p."id" = oi."productId"
                JOIN "Manufacturer" m ON m."id" = p."manufacturerId"
                JOIN "VehicleSystem" vs ON vs."id" = p."vehicleSystemId"
                WHERE oi."orderId" = ANY({ids}::text[])
                ORDER BY oi."id" ASC
                """).ToListAsync(ct);

            var byOrder = lines.GroupBy(l => l.OrderId).ToDictionary(g2 => g2.Key, g2 => g2.ToList());

            return Results.Ok(new
            {
                orders = orders.Select(o =>
                {
                    var mine = byOrder.GetValueOrDefault(o.Id) ?? [];
                    var total = Money.Round(mine.Sum(l => l.UnitPrice * l.Quantity));
                    return new
                    {
                        id = o.Id,
                        reference = o.Reference,
                        clientName = o.ClientName,
                        status = o.Status,
                        createdAt = Timestamps.Iso(o.CreatedAt),
                        units = mine.Sum(l => l.Quantity),
                        /* Distinct parts, which is the number units alone cannot imply. */
                        lineCount = mine.Count,
                        // Prices here are the base-currency figures actually
                        // stored on the line, matching total rather than
                        // quotedTotal, so a reader adding the lines up arrives
                        // at the column beside them.
                        lines = mine.Select(l => new
                        {
                            productId = l.ProductId,
                            partNumber = l.PartNumber,
                            name = l.Name,
                            manufacturer = l.Manufacturer,
                            system = l.System,
                            quantity = l.Quantity,
                            unitPrice = Money.Round(l.UnitPrice),
                            lineTotal = Money.Round(l.UnitPrice * l.Quantity),
                        }),
                        // Base currency, deliberately: the only figure
                        // comparable across customers quoted in different
                        // currencies, which is what a list of every order is
                        // for. The quoted amount rides alongside for anyone
                        // reconciling against what the customer actually saw.
                        total,
                        quotedTotal = Money.Round(total * o.CurrencyRate),
                        currencyCode = o.CurrencyCode,
                    };
                }),
            });
        });

        // GET /api/admin/carts — baskets that were filled and never ordered.
        //
        // The sales question this answers is "who nearly bought something", so
        // it is open to SALES as well, scoped the same way the customer list is.
        app.MapGet("/api/admin/carts", async (
            HttpContext http, AdminGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireStaff(http);
            if (!g.Ok) return g.Response!;

            var scope = g.ScopeTo;

            // Oldest first: a basket sitting untouched for a fortnight is the
            // one worth a phone call, and it is the one a newest-first list
            // buries. Totals summed in the database rather than by adding up
            // lines in memory.
            //
            // Line values are the catalogue's purchase prices, not what the
            // customer would pay: pricing a basket per account means running
            // the markup engine for every line of every basket, and this list
            // exists to be skimmed.
            var carts = await db.Database.SqlQuery<AdminCartRow>($"""
                SELECT ct."id" AS "Id", cl."id" AS "ClientId", cl."name" AS "ClientName",
                       cl."email" AS "ClientEmail", ct."updatedAt" AS "UpdatedAt",
                       lines."units"::int AS "Units", lines."cost" AS "Cost"
                FROM "Cart" ct
                JOIN "Client" cl ON cl."id" = ct."clientId"
                JOIN LATERAL (
                  SELECT SUM(ci."quantity") AS "units",
                         SUM(ci."quantity" * COALESCE(pli."price", p."basePrice")) AS "cost"
                  FROM "CartItem" ci
                  JOIN "Product" p ON p."id" = ci."productId"
                  LEFT JOIN LATERAL (
                    SELECT i."price"
                    FROM "PriceListItem" i
                    JOIN "PriceList" pl ON pl."id" = i."priceListId" AND pl."active"
                    WHERE i."productId" = p."id"
                    LIMIT 1
                  ) pli ON TRUE
                  WHERE ci."cartId" = ct."id"
                ) lines ON TRUE
                WHERE lines."units" IS NOT NULL
                  AND ({scope}::text IS NULL OR cl."salesManagerId" = {scope})
                ORDER BY ct."updatedAt" ASC
                LIMIT {CartLimit}
                """).ToListAsync(ct);

            var cartIds = carts.Select(c => c.Id).ToArray();
            var items = await db.Database.SqlQuery<AdminCartLineRow>($"""
                SELECT ci."cartId" AS "CartId", ci."productId" AS "ProductId",
                       p."partNumber" AS "PartNumber", p."name" AS "Name",
                       ci."quantity" AS "Quantity"
                FROM "CartItem" ci
                JOIN "Product" p ON p."id" = ci."productId"
                WHERE ci."cartId" = ANY({cartIds}::text[])
                ORDER BY ci."addedAt" ASC
                """).ToListAsync(ct);

            var byCart = items.GroupBy(i => i.CartId).ToDictionary(g2 => g2.Key, g2 => g2.ToList());

            return Results.Ok(new
            {
                carts = carts.Select(c => new
                {
                    id = c.Id,
                    clientId = c.ClientId,
                    clientName = c.ClientName,
                    clientEmail = c.ClientEmail,
                    updatedAt = Timestamps.Iso(c.UpdatedAt),
                    units = c.Units,
                    cost = c.Cost,
                    items = (byCart.GetValueOrDefault(c.Id) ?? []).Select(i => new
                    {
                        productId = i.ProductId,
                        partNumber = i.PartNumber,
                        name = i.Name,
                        quantity = i.Quantity,
                    }),
                }),
            });
        });

        // GET /api/admin/notifications — what has been sent, and who to.
        app.MapGet("/api/admin/notifications", async (
            HttpContext http, AdminGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            var notifications = await db.Notifications
                .OrderByDescending(n => n.CreatedAt)
                .Take(NotificationLimit)
                .Select(n => new
                {
                    n.Id, n.ClientId, ClientName = n.Client.Name,
                    n.Type, n.Title, n.Body, n.Link, n.ReadAt, n.CreatedAt,
                })
                .AsNoTracking()
                .ToListAsync(ct);

            var recipients = await db.Clients
                .OrderBy(c => c.Name)
                .Select(c => new { id = c.Id, name = c.Name + " — " + c.Email })
                .AsNoTracking()
                .ToListAsync(ct);

            return Results.Ok(new
            {
                notifications = notifications.Select(n => new
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
                }),
                recipients,
            });
        });
    }
}

public record DashboardRow(int Products, int Clients, int Orders, int ActiveRules);

public record AdminOrderRow(
    string Id, string Reference, string ClientName, string Status,
    DateTime CreatedAt, string CurrencyCode, double CurrencyRate);

public record AdminOrderLineRow(
    string OrderId, string ProductId, string PartNumber, string Name,
    string Manufacturer, string System, int Quantity, double UnitPrice);

public record AdminCartRow(
    string Id, string ClientId, string ClientName, string ClientEmail,
    DateTime UpdatedAt, int Units, double Cost);

public record AdminCartLineRow(
    string CartId, string ProductId, string PartNumber, string Name, int Quantity);
