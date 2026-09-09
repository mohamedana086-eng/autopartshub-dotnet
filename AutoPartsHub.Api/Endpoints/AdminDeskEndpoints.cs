using AutoPartsHub.Api.Auth;
using AutoPartsHub.Api.Catalogue;
using AutoPartsHub.Api.Data;
using AutoPartsHub.Domain.Catalogue;
using AutoPartsHub.Domain.Orders;
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

        // GET /api/admin/orders/<id>/suppliers
        //
        // What this order draws from each supplier, and whether it clears their
        // minimum. Its own endpoint rather than a field on the order list: the
        // figures are per-order joins over live purchase prices, and the list
        // renders fifty orders.
        //
        // MEASURED IN WHAT WE PAY, NOT WHAT THE CUSTOMER PAID. A supplier's
        // minimum is a floor on OUR purchase order; the customer's line price
        // is our cost plus a markup that varies by their tier, so measuring
        // against it would make the same basket clear the minimum for one
        // customer and miss it for another — nonsense, since the supplier ships
        // the same goods either way.
        //
        // AT TODAY'S COST, not the cost when it was ordered. That looks like
        // the opposite of freezing `Order.currencyRate`, and it is the opposite
        // case: the rate is frozen because what a customer was quoted is a fact
        // about the past, and this answers "may I place a purchase order this
        // afternoon", which is about the present.
        app.MapGet("/api/admin/orders/{id}/suppliers", async (
            string id, HttpContext http, AdminGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireStaff(http);
            if (!g.Ok) return g.Response!;

            var scope = g.ScopeTo;

            // Scoped through the order, not the shares: reading who supplies a
            // part is harmless, but confirming that an order EXISTS is not.
            var exists = await db.Database.SqlQuery<int>($"""
                SELECT 1 AS "Value"
                FROM "Order" o
                JOIN "Client" c ON c."id" = o."clientId"
                WHERE o."id" = {id}
                  AND ({scope}::text IS NULL OR c."salesManagerId" = {scope})
                """).ToListAsync(ct);
            if (exists.Count == 0) return Results.NotFound(new { error = "Order not found." });

            // Lines whose part has no live offer are left out entirely rather
            // than grouped under a null supplier: there is no purchase order
            // for them to be part of, and a "no supplier" row would read as a
            // supplier failing a minimum.
            var shares = await db.Database.SqlQuery<SupplierShareRow>($"""
                SELECT bo."supplierId" AS "SupplierId", s."name" AS "SupplierName",
                       s."code" AS "SupplierCode",
                       SUM(i."quantity" * bo."purchasePrice") AS "Amount",
                       s."minOrderAmount" AS "MinOrderAmount",
                       GREATEST(s."minOrderAmount" - SUM(i."quantity" * bo."purchasePrice"), 0)
                         AS "Shortfall"
                FROM "OrderItem" i
                JOIN "Product" p ON p."id" = i."productId"
                -- inner by design: a line no supplier offers is on nobody's purchase order
                JOIN "BestOffer" bo ON bo."productId" = p."id"
                JOIN "Supplier" s ON s."id" = bo."supplierId"
                WHERE i."orderId" = {id}
                GROUP BY bo."supplierId", s."name", s."code", s."minOrderAmount"
                ORDER BY SUM(i."quantity" * bo."purchasePrice") DESC, s."code" ASC
                """).ToListAsync(ct);

            return Results.Ok(new
            {
                suppliers = shares.Select(x => new
                {
                    supplierId = x.SupplierId,
                    supplierName = x.SupplierName,
                    supplierCode = x.SupplierCode,
                    amount = Money.Round(x.Amount),
                    minOrderAmount = x.MinOrderAmount,
                    shortfall = Money.Round(x.Shortfall),
                }),
                // Answered here rather than left to the screen to work out, so
                // that a second screen asking the same question cannot decide
                // it differently.
                belowMinimum = shares.Any(x => x.Shortfall > 0),
            });
        });

        // GET /api/admin/search-misses?order=searches|recent&page=&pageSize=
        //
        // What customers looked for and did not find. RequireStaff, not
        // RequireAdmin: this is a buying report — which parts to stock next —
        // and it is also the list a salesperson should read before a call.
        // There is nothing per-customer in it to scope, because the table
        // records no client by design, so a salesperson and an admin see the
        // same rows and that is the correct answer rather than a missing
        // narrowing.
        app.MapGet("/api/admin/search-misses", async (
            HttpContext http, AdminGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireStaff(http);
            if (!g.Ok) return g.Response!;

            // Two orderings, because they answer different questions: what is
            // asked for most, and what has STARTED being asked for. The second
            // catches a new model arriving on the roads while the all-time list
            // is still topped by something stocked months ago.
            var byRecent = http.Request.Query["order"].ToString() == "recent";
            var page = Paging.ReadPage(http.Request.Query["page"]);
            var pageSize = Paging.ReadPageSize(
                http.Request.Query["pageSize"], http.Request.Query["limit"]);

            var misses = await db.Database.SqlQuery<SearchMissRow>($"""
                SELECT "term" AS "Term", "narrowed" AS "Narrowed", "searches" AS "Searches",
                       "firstSeenAt" AS "FirstSeenAt", "lastSeenAt" AS "LastSeenAt"
                FROM "SearchMiss"
                ORDER BY
                  CASE WHEN {byRecent} THEN "lastSeenAt" END DESC,
                  CASE WHEN {byRecent} THEN NULL ELSE "searches" END DESC,
                  "term" ASC
                LIMIT {pageSize} OFFSET {(page - 1) * pageSize}
                """).ToListAsync(ct);

            var total = (await db.Database.SqlQuery<int>(
                $"""SELECT COUNT(*)::int AS "Value" FROM "SearchMiss" """).ToListAsync(ct))
                .FirstOrDefault();

            return Results.Ok(new
            {
                misses = misses.Select(m => new
                {
                    term = m.Term,
                    // Whether a filter was on. A term that only ever missed
                    // while narrowed is a gap in one supplier relationship,
                    // not in the catalogue.
                    narrowed = m.Narrowed,
                    searches = m.Searches,
                    firstSeenAt = Timestamps.Iso(m.FirstSeenAt),
                    lastSeenAt = Timestamps.Iso(m.LastSeenAt),
                }),
                order = byRecent ? "recent" : "searches",
                total,
                page,
                pageSize,
                pages = Paging.PageCount(total, pageSize),
            });
        });

        // GET /api/admin/orders?status=&from=&to=&managerId=&page=&pageSize=
        app.MapGet("/api/admin/orders", async (
            HttpContext http, AdminGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireStaff(http);
            if (!g.Ok) return g.Response!;

            // Scoped through the customer's owner, so a salesperson sees the
            // orders of the accounts they look after and no others. Separate
            // from the `managerId` filter below, and applied as well as it — a
            // request can narrow the set it is allowed to see, never widen it.
            var scope = g.ScopeTo;

            var filter = OrderFilters.Read(key => http.Request.Query[key]);
            if (!filter.Ok) return Results.BadRequest(new { error = filter.Error });

            var f = filter.Value!;
            var page = Paging.ReadPage(http.Request.Query["page"]);
            var pageSize = Paging.ReadPageSize(
                http.Request.Query["pageSize"], http.Request.Query["limit"]);

            var orders = await db.Database.SqlQuery<AdminOrderRow>($"""
                SELECT o."id" AS "Id", o."reference" AS "Reference",
                       cl."name" AS "ClientName", o."status" AS "Status",
                       o."createdAt" AS "CreatedAt",
                       o."currencyCode" AS "CurrencyCode", o."currencyRate" AS "CurrencyRate",
                       o."weightGrams" AS "WeightGrams", o."weightComplete" AS "WeightComplete",
                       o."statusReason" AS "StatusReason",
                       o."statusChangedAt" AS "StatusChangedAt",
                       who."name" AS "StatusChangedByName",
                       o."trackingNumber" AS "TrackingNumber", o."carrier" AS "Carrier"
                FROM "Order" o
                JOIN "Client" cl ON cl."id" = o."clientId"
                LEFT JOIN "Client" who ON who."id" = o."statusChangedById"
                WHERE ({scope}::text IS NULL OR cl."salesManagerId" = {scope})
                  AND ({f.ManagerId}::text IS NULL OR cl."salesManagerId" = {f.ManagerId})
                  AND ({f.Status}::text IS NULL OR o."status" = {f.Status})
                  AND ({f.From}::timestamp IS NULL OR o."createdAt" >= {f.From})
                  AND ({f.Before}::timestamp IS NULL OR o."createdAt" < {f.Before})
                ORDER BY o."createdAt" DESC
                LIMIT {pageSize} OFFSET {(page - 1) * pageSize}
                """).ToListAsync(ct);

            var total = (await db.Database.SqlQuery<int>($"""
                SELECT COUNT(*)::int AS "Value"
                FROM "Order" o
                JOIN "Client" cl ON cl."id" = o."clientId"
                WHERE ({scope}::text IS NULL OR cl."salesManagerId" = {scope})
                  AND ({f.ManagerId}::text IS NULL OR cl."salesManagerId" = {f.ManagerId})
                  AND ({f.Status}::text IS NULL OR o."status" = {f.Status})
                  AND ({f.From}::timestamp IS NULL OR o."createdAt" >= {f.From})
                  AND ({f.Before}::timestamp IS NULL OR o."createdAt" < {f.Before})
                """).ToListAsync(ct)).FirstOrDefault();

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
                        // Why it was refused or called off, and who last moved
                        // it. A null `statusChangedAt` means the status has
                        // never moved — the order is still exactly as the
                        // customer placed it, which is a different thing from
                        // having been moved back to where it started.
                        statusReason = o.StatusReason,
                        statusChangedAt = o.StatusChangedAt is null
                            ? null
                            : Timestamps.Iso(o.StatusChangedAt.Value),
                        statusChangedByName = o.StatusChangedByName,
                        trackingNumber = o.TrackingNumber,
                        carrier = o.Carrier,
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
                        weightGrams = o.WeightGrams,
                        weightComplete = o.WeightComplete,
                    };
                }),
                total,
                page,
                pageSize,
                pages = Paging.PageCount(total, pageSize),
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
                         -- The active price list where it covers the part, the
                         -- best supplier offer where it does not, the part's
                         -- own price where there is neither: the same three
                         -- rungs as PurchasePrice() in PricingContextLoader,
                         -- which is the C# spelling of this chain. Two
                         -- spellings of one rule, and they have to agree — an
                         -- admin's basket total that disagreed with the
                         -- customer's would be the kind of wrong nobody
                         -- reports because both look right.
                         SUM(ci."quantity" * COALESCE(pli."price", bo."purchasePrice", p."basePrice")) AS "cost"
                  FROM "CartItem" ci
                  JOIN "Product" p ON p."id" = ci."productId"
                  LEFT JOIN "BestOffer" bo ON bo."productId" = p."id"
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
        //
        // Open to SALES, narrowed to their own customers. The recipient list is
        // scoped as well, and not merely for tidiness: it is the address book
        // the compose box is filled from, so an unscoped one would hand a
        // salesperson every customer's name and email address on the same
        // screen that refuses to let them write to any of them.
        app.MapGet("/api/admin/notifications", async (
            HttpContext http, AdminGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireStaff(http);
            if (!g.Ok) return g.Response!;

            var scope = g.ScopeTo;

            var notifications = await db.Notifications
                .Where(n => scope == null || n.Client.SalesManagerId == scope)
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
                .Where(c => scope == null || c.SalesManagerId == scope)
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

/// <summary>One supplier's share of one order, and their minimum.</summary>
public record SupplierShareRow(
    string SupplierId, string SupplierName, string SupplierCode,
    double Amount, double MinOrderAmount, double Shortfall);

/// <summary>One thing customers looked for and did not find.</summary>
public record SearchMissRow(
    string Term, bool Narrowed, int Searches, DateTime FirstSeenAt, DateTime LastSeenAt);

/// <param name="StatusChangedAt">
/// Null means the status has never moved — the order is still exactly as the
/// customer placed it, which is a different thing from having been put back.
/// </param>
public record AdminOrderRow(
    string Id, string Reference, string ClientName, string Status,
    DateTime CreatedAt, string CurrencyCode, double CurrencyRate,
    int WeightGrams, bool WeightComplete,
    string? StatusReason, DateTime? StatusChangedAt, string? StatusChangedByName,
    string? TrackingNumber, string? Carrier);

public record AdminOrderLineRow(
    string OrderId, string ProductId, string PartNumber, string Name,
    string Manufacturer, string System, int Quantity, double UnitPrice);

public record AdminCartRow(
    string Id, string ClientId, string ClientName, string ClientEmail,
    DateTime UpdatedAt, int Units, double Cost);

public record AdminCartLineRow(
    string CartId, string ProductId, string PartNumber, string Name, int Quantity);
