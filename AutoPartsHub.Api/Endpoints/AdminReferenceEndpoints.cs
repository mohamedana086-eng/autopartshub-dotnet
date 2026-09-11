using AutoPartsHub.Api.Admin;
using AutoPartsHub.Api.Auth;
using AutoPartsHub.Api.Catalogue;
using AutoPartsHub.Api.Data;
using AutoPartsHub.Domain.Catalogue;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsHub.Api.Endpoints;

/// <summary>
/// The reference data the admin edits: suppliers, sites, currencies, tiers,
/// markup rules and purchase price lists.
/// </summary>
/// <remarks>
/// Each list carries the counts its screen is read for — how many parts a
/// supplier sources, how many units a warehouse holds, how many accounts a
/// currency prices. They are counted in the database, in lateral joins, rather
/// than by loading the rows behind them and reducing in memory: the numbers
/// are the point of the list, and the rows behind them are never shown.
/// </remarks>
public static class AdminReferenceEndpoints
{
    public static void MapAdminReferenceEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /api/admin/suppliers — everyone we buy from, with how much each sources.
        app.MapGet("/api/admin/suppliers", async (
            HttpContext http, AdminGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            var suppliers = await db.Database.SqlQuery<AdminSupplierRow>($"""
                SELECT s."id" AS "Id", s."code" AS "Code", s."slug" AS "Slug", s."name" AS "Name",
                       s."description" AS "Description", s."reliability" AS "Reliability",
                       s."rating" AS "Rating", s."acceptsReturns" AS "AcceptsReturns",
                       s."country" AS "Country", s."guaranteeMonths" AS "GuaranteeMonths",
                       s."defaultStockDays" AS "DefaultStockDays",
                       s."purchaseCurrencyId" AS "PurchaseCurrencyId",
                       c."code" AS "PurchaseCurrencyCode",
                       s."priority" AS "Priority", s."minOrderAmount" AS "MinOrderAmount",
                       s."markupPercent" AS "MarkupPercent",
                       s."active" AS "Active", to_char(s."approvedAt", 'YYYY-MM-DD"T"HH24:MI:SS.MS"Z"') AS "ApprovedAt",
                       p."count" AS "ProductCount"
                FROM "Supplier" s
                LEFT JOIN "Currency" c ON c."id" = s."purchaseCurrencyId"
                OUTER APPLY (
                  SELECT COUNT(*) AS "count" FROM "Product" pr WHERE pr."supplierId" = s."id"
                ) p
                ORDER BY s."name" ASC
                """).ToListAsync(ct);

            // For the editor's currency select. Reference only on a supplier,
            // but it still has to be picked from the currencies that exist.
            return Results.Ok(new { suppliers, currencies = await CurrencyOptions(db, ct) });
        });

        // GET /api/admin/warehouses — every warehouse, with what it holds.
        //
        // Picking order, then code: the list answers "where would this ship
        // from", and that is the order the answer is decided in.
        app.MapGet("/api/admin/warehouses", async (
            HttpContext http, AdminGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            var warehouses = await db.Database.SqlQuery<AdminWarehouseRow>($"""
                SELECT w."id" AS "Id", w."code" AS "Code", w."name" AS "Name", w."city" AS "City",
                       w."address" AS "Address", w."active" AS "Active", w."priority" AS "Priority",
                       o."count" AS "OutletCount",
                       s."skus" AS "SkuCount",
                       COALESCE(s."quantity", 0) AS "TotalQuantity",
                       COALESCE(s."reserved", 0) AS "TotalReserved"
                FROM "Warehouse" w
                OUTER APPLY (
                  SELECT COUNT(*) AS "count" FROM "RetailOutlet" ro WHERE ro."warehouseId" = w."id"
                ) o
                OUTER APPLY (
                  SELECT COUNT(*) AS "skus", SUM(sl."quantity") AS "quantity",
                         SUM(sl."reserved") AS "reserved"
                  FROM "StockLevel" sl WHERE sl."warehouseId" = w."id"
                ) s
                ORDER BY w."priority" DESC, w."code" ASC
                """).ToListAsync(ct);

            return Results.Ok(new { warehouses });
        });

        // GET /api/admin/outlets — the counters, plus the warehouses they can
        // be served from. Open counters first, then by code: a closed outlet is
        // reference, not work.
        app.MapGet("/api/admin/outlets", async (
            HttpContext http, AdminGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            var outlets = await db.Database.SqlQuery<AdminOutletRow>($"""
                SELECT o."id" AS "Id", o."code" AS "Code", o."name" AS "Name", o."city" AS "City",
                       o."address" AS "Address", o."phone" AS "Phone",
                       o."warehouseId" AS "WarehouseId",
                       w."name" AS "WarehouseName", w."code" AS "WarehouseCode",
                       o."active" AS "Active"
                FROM "RetailOutlet" o
                LEFT JOIN "Warehouse" w ON w."id" = o."warehouseId"
                ORDER BY o."active" DESC, o."code" ASC
                """).ToListAsync(ct);

            var warehouses = await db.Database.SqlQuery<NamedOption>($"""
                SELECT "id" AS "Id", "code" || ' — ' || "name" AS "Name"
                FROM "Warehouse"
                ORDER BY "priority" DESC, "code" ASC
                """).ToListAsync(ct);

            return Results.Ok(new { outlets, warehouses });
        });

        // GET /api/admin/currencies — every currency, with how many accounts
        // use it. Base first, then alphabetical: the base is the one everything
        // else is measured against, so it belongs at the top rather than under E.
        app.MapGet("/api/admin/currencies", async (
            HttpContext http, AdminGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            var currencies = await db.Database.SqlQuery<AdminCurrencyRow>($"""
                SELECT c."id" AS "Id", c."code" AS "Code", c."name" AS "Name", c."symbol" AS "Symbol",
                       c."rate" AS "Rate", c."isBase" AS "IsBase", c."active" AS "Active",
                       n."count" AS "ClientCount"
                FROM "Currency" c
                OUTER APPLY (
                  SELECT COUNT(*) AS "count" FROM "Client" cl WHERE cl."currencyId" = c."id"
                ) n
                ORDER BY c."isBase" DESC, c."code" ASC
                """).ToListAsync(ct);

            return Results.Ok(new { currencies });
        });

        // GET /api/admin/client-categories — tiers cheapest markup first,
        // which is the order they are reasoned about.
        app.MapGet("/api/admin/client-categories", async (
            HttpContext http, AdminGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            var categories = await db.Database.SqlQuery<AdminCategoryRow>($"""
                SELECT c."id" AS "Id", c."name" AS "Name", c."markupPercent" AS "MarkupPercent",
                       c."minOrderAmount" AS "MinOrderAmount", c."shelfLifeDays" AS "ShelfLifeDays",
                       n."count" AS "ClientCount"
                FROM "ClientCategory" c
                OUTER APPLY (
                  SELECT COUNT(*) AS "count" FROM "Client" cl WHERE cl."categoryId" = c."id"
                ) n
                ORDER BY c."markupPercent" ASC
                """).ToListAsync(ct);

            return Results.Ok(new { categories });
        });

        // GET /api/admin/markup-rules — rules plus everything the builder's
        // selects need.
        //
        // Rules come back most specific first, which is the order the engine
        // ranks them in, so the list reads as "this is the one that applies".
        // Each condition value carries a readable label beside it: one that
        // said only "supplier = cms1a32bs…" could not be read at all.
        app.MapGet("/api/admin/markup-rules", async (
            HttpContext http, AdminGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            var rules = await MarkupRules.Read(db, null, ct);
            var options = await MarkupRules.Options(db, ct);

            return Results.Ok(new
            {
                rules,
                dimensions = ((dynamic)options).dimensions,
                values = ((dynamic)options).values,
            });
        });

        // GET /api/admin/price-lists — every list, active first then newest.
        //
        // ADMIN only, deliberately, like every write to them: these set what
        // every part costs to buy, which is the number the whole markup engine
        // multiplies up.
        app.MapGet("/api/admin/price-lists", async (
            HttpContext http, AdminGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            var lists = await PriceLists(db, null, ct);

            return Results.Ok(new { lists = lists.Select(Serialise) });
        });

        // GET /api/admin/price-lists/imports — the history of uploads, newest
        // first, optionally narrowed to one list's.
        //
        // A literal segment beside {id}, which routing prefers, so this path
        // reaches here and /price-lists/<id> still reaches the list. Its own
        // path rather than a child of a list because a refused upload never
        // produced one, and those are the entries most worth reading.
        app.MapGet("/api/admin/price-lists/imports", async (
            HttpContext http, AdminGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            var query = http.Request.Query;
            var page = Paging.ReadPage(query["page"]);
            var pageSize = Paging.ReadPageSize(query["pageSize"], query["limit"]);
            var priceListId = query["priceListId"].ToString().Trim();
            if (priceListId.Length == 0) priceListId = null!;

            var (imports, total) = await Imports(db, null, priceListId, page, pageSize, ct);

            return Results.Ok(new
            {
                imports = imports.Select(SerialiseImport),
                total,
                page,
                pageSize,
                pages = Paging.PageCount(total, pageSize),
            });
        });

        // GET /api/admin/price-lists/imports/<importId> — one upload, and the
        // lines of it that did not make it.
        //
        // This is the screen the whole log exists for. The accepted rows can
        // already be read through the list they became; the rejected ones had
        // nowhere to be read at all, and they are the half that explains why a
        // part is still on its old price.
        app.MapGet("/api/admin/price-lists/imports/{importId}", async (
            string importId, HttpContext http, AdminGate gate, AutoPartsContext db,
            CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            var record = (await Imports(db, importId, null, 1, 1, ct)).Rows.FirstOrDefault();
            if (record is null) return Results.NotFound(new { error = "Import not found." });

            var query = http.Request.Query;
            var page = Paging.ReadPage(query["page"]);
            var pageSize = Paging.ReadPageSize(query["pageSize"], query["limit"]);

            var rejected = await db.Database.SqlQuery<RejectedLineRow>($"""
                SELECT "line" AS "Line", "partNumber" AS "PartNumber", "price" AS "Price",
                       "currency" AS "Currency", "reason" AS "Reason"
                FROM "PriceListImportRow"
                WHERE "importId" = {importId}
                ORDER BY "line" ASC
                OFFSET {(page - 1) * pageSize} ROWS FETCH NEXT {pageSize} ROWS ONLY
                """).ToListAsync(ct);

            var total = (await db.Database.SqlQuery<int>($"""
                SELECT COUNT(*) AS "Value" FROM "PriceListImportRow"
                WHERE "importId" = {importId}
                """).ToListAsync(ct)).FirstOrDefault();

            return Results.Ok(new
            {
                import = SerialiseImport(record),
                rejected,
                // `total` is what can be paged through; the import's own count
                // is what actually happened. Saying both is the honest answer
                // for a file that failed more times than the log keeps.
                total,
                page,
                pageSize,
                pages = Paging.PageCount(total, pageSize),
                truncated = record.Rejected > record.RejectedStored,
                storedLimit = Admin.PriceLists.StoredRejections,
            });
        });

        // GET /api/admin/price-lists/<id> — the list and its lines, a page at a
        // time.
        //
        // Paged rather than sampled. A fixed cap answered "did the file land
        // the way it was meant to" and no other question: a list of forty
        // thousand parts showed two hundred of them and the rest could not be
        // reached from here at all.
        app.MapGet("/api/admin/price-lists/{id}", async (
            string id, HttpContext http, AdminGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireAdmin(http);
            if (!g.Ok) return g.Response!;

            var list = (await PriceLists(db, id, ct)).FirstOrDefault();
            if (list is null) return Results.NotFound(new { error = "Price list not found." });

            var query = http.Request.Query;
            var page = Paging.ReadPage(query["page"]);
            var pageSize = Paging.ReadPageSize(query["pageSize"], query["limit"]);

            var items = await db.Database.SqlQuery<PriceListLineRow>($"""
                SELECT i."productId" AS "ProductId", p."partNumber" AS "PartNumber",
                       p."name" AS "Name", i."price" AS "Price",
                       i."sourcePrice" AS "SourcePrice", i."sourceCurrency" AS "SourceCurrency",
                       i."sourcePartNumber" AS "SourcePartNumber",
                       i."markupPercent" AS "MarkupPercent",
                       p."basePrice" AS "BasePrice"
                FROM "PriceListItem" i
                JOIN "Product" p ON p."id" = i."productId"
                WHERE i."priceListId" = {id}
                ORDER BY p."partNumber" ASC
                OFFSET {(page - 1) * pageSize} ROWS FETCH NEXT {pageSize} ROWS ONLY
                """).ToListAsync(ct);

            var total = (await db.Database.SqlQuery<int>($"""
                SELECT COUNT(*) AS "Value" FROM "PriceListItem" WHERE "priceListId" = {id}
                """).ToListAsync(ct)).FirstOrDefault();

            return Results.Ok(new
            {
                list = Serialise(list),
                items,
                shown = items.Count,
                total,
                page,
                pageSize,
                pages = Paging.PageCount(total, pageSize),
            });
        });

        // GET /api/admin/clients — every account, plus the tiers they can be
        // moved to. A salesperson sees their own customers and nobody else's.
        app.MapGet("/api/admin/clients", async (
            HttpContext http, AdminGate gate, AutoPartsContext db, CancellationToken ct) =>
        {
            var g = gate.RequireStaff(http);
            if (!g.Ok) return g.Response!;

            var scope = g.ScopeTo;

            var clients = await db.Database.SqlQuery<AdminClientRow>($"""
                SELECT c."id" AS "Id", c."name" AS "Name", c."email" AS "Email", c."role" AS "Role",
                       c."city" AS "City",
                       (c."passwordHash" IS NOT NULL) AS "HasLogin",
                       c."categoryId" AS "CategoryId", cat."name" AS "CategoryName",
                       c."discountPercent" AS "DiscountPercent",
                       c."currencyId" AS "CurrencyId", cur."code" AS "CurrencyCode",
                       c."salesManagerId" AS "SalesManagerId", m."name" AS "SalesManagerName"
                FROM "Client" c
                LEFT JOIN "ClientCategory" cat ON cat."id" = c."categoryId"
                LEFT JOIN "Currency" cur ON cur."id" = c."currencyId"
                LEFT JOIN "Client" m ON m."id" = c."salesManagerId"
                WHERE ({scope} IS NULL OR c."salesManagerId" = {scope})
                ORDER BY c."createdAt" DESC
                """).ToListAsync(ct);

            var categories = await db.ClientCategories.OrderBy(c => c.MarkupPercent)
                .Select(c => new { id = c.Id, name = c.Name }).AsNoTracking().ToListAsync(ct);

            // Its own query, not derived from the scoped list above: that one
            // is narrowed to a salesperson's own customers, which would leave
            // the dropdown listing whichever staff happened to be among them —
            // usually none.
            var salesManagers = await db.Clients.Where(c => c.Role == Roles.Sales).OrderBy(c => c.Name)
                .Select(c => new { id = c.Id, name = c.Name }).AsNoTracking().ToListAsync(ct);

            return Results.Ok(new
            {
                clients,
                categories,
                currencies = await CurrencyOptions(db, ct),
                salesManagers,
            });
        });
    }

    private static Task<List<NamedOption>> CurrencyOptions(AutoPartsContext db, CancellationToken ct) =>
        db.Database.SqlQuery<NamedOption>($"""
            SELECT "id" AS "Id", "code" || ' — ' || "name" AS "Name"
            FROM "Currency"
            WHERE "active" = 1
            ORDER BY "isBase" DESC, "code" ASC
            """).ToListAsync(ct);

    private static Task<List<PriceListRow>> PriceLists(AutoPartsContext db, string? id, CancellationToken ct) =>
        db.Database.SqlQuery<PriceListRow>($"""
            SELECT l."id" AS "Id", l."name" AS "Name", l."description" AS "Description",
                   l."active" AS "Active", l."sourceName" AS "SourceName",
                   l."markupPercent" AS "MarkupPercent",
                   n."count" AS "ItemCount",
                   l."createdAt" AS "CreatedAt", l."updatedAt" AS "UpdatedAt"
            FROM "PriceList" l
            OUTER APPLY (
              SELECT COUNT(*) AS "count" FROM "PriceListItem" i WHERE i."priceListId" = l."id"
            ) n
            WHERE ({id} IS NULL OR l."id" = {id})
            ORDER BY l."active" DESC, l."createdAt" DESC
            """).ToListAsync(ct);

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

    /// <summary>
    /// Uploads, newest first, with the total behind the page.
    /// </summary>
    /// <remarks>
    /// <paramref name="id"/> and <paramref name="priceListId"/> are both
    /// optional and mean different things: one import, or every import of one
    /// list. Passing neither reads the whole history, which is the screen this
    /// exists for.
    /// </remarks>
    private static async Task<(List<PriceListImportSummary> Rows, int Total)> Imports(
        AutoPartsContext db, string? id, string? priceListId, int page, int pageSize,
        CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<PriceListImportSummary>($"""
            SELECT i."id" AS "Id", i."priceListId" AS "PriceListId", i."listName" AS "ListName",
                   i."sourceName" AS "SourceName", i."uploadedById" AS "UploadedById",
                   i."uploadedByName" AS "UploadedByName", i."outcome" AS "Outcome",
                   i."rowsSent" AS "RowsSent", i."accepted" AS "Accepted",
                   i."rejected" AS "Rejected", i."rejectedStored" AS "RejectedStored",
                   i."error" AS "Error", i."createdAt" AS "CreatedAt",
                   COALESCE(l."active", FALSE) AS "ListActive"
            FROM "PriceListImport" i
            LEFT JOIN "PriceList" l ON l."id" = i."priceListId"
            WHERE ({id} IS NULL OR i."id" = {id})
              AND ({priceListId} IS NULL OR i."priceListId" = {priceListId})
            ORDER BY i."createdAt" DESC
            OFFSET {(page - 1) * pageSize} ROWS FETCH NEXT {pageSize} ROWS ONLY
            """).ToListAsync(ct);

        var total = (await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*) AS "Value" FROM "PriceListImport" i
            WHERE ({id} IS NULL OR i."id" = {id})
              AND ({priceListId} IS NULL OR i."priceListId" = {priceListId})
            """).ToListAsync(ct)).FirstOrDefault();

        return (rows, total);
    }

    private static object SerialiseImport(PriceListImportSummary i) => new
    {
        id = i.Id,
        priceListId = i.PriceListId,
        listName = i.ListName,
        sourceName = i.SourceName,
        uploadedById = i.UploadedById,
        uploadedByName = i.UploadedByName,
        outcome = i.Outcome,
        rowsSent = i.RowsSent,
        accepted = i.Accepted,
        rejected = i.Rejected,
        // What can actually be read back, as against what happened. They differ
        // only where a file failed more times than the log keeps.
        rejectedStored = i.RejectedStored,
        error = i.Error,
        listActive = i.ListActive,
        createdAt = Timestamps.Iso(i.CreatedAt),
    };
}

public record NamedOption(string Id, string Name);

/// <param name="Active">Whether they are trading. False hides every part of theirs from the shop.</param>
/// <param name="ApprovedAt">
/// When they were let in, or null if they never have been.
///
/// A string, formatted in the query, rather than a DateTime. The other API
/// serialises a Date, which JSON.stringify writes as an ISO instant with three
/// fractional digits and a Z; System.Text.Json writes neither the Z nor the
/// same precision. This row goes to the client as it stands rather than
/// through a serialiser that could fix it up on the way out.
/// </param>
public record AdminSupplierRow(
    string Id, string Code, string Slug, string Name, string? Description, string Reliability,
    int? Rating, bool? AcceptsReturns, string? Country, int? GuaranteeMonths,
    int? DefaultStockDays, string? PurchaseCurrencyId, string? PurchaseCurrencyCode,
    /// <summary>Which supplier to prefer when several offer a part, higher winning.</summary>
    int Priority,
    /// <summary>The least they will take an order for. Zero means no minimum.</summary>
    double MinOrderAmount,
    /// <summary>The margin their parts earn, or null where none is agreed.</summary>
    double? MarkupPercent,
    bool Active, string? ApprovedAt, int ProductCount);

public record AdminWarehouseRow(
    string Id, string Code, string Name, string? City, string? Address, bool Active, int Priority,
    int OutletCount, int SkuCount, int TotalQuantity, int TotalReserved);

public record AdminOutletRow(
    string Id, string Code, string Name, string? City, string? Address, string? Phone,
    string? WarehouseId, string? WarehouseName, string? WarehouseCode, bool Active);

public record AdminCurrencyRow(
    string Id, string Code, string Name, string Symbol, double Rate, bool IsBase, bool Active, int ClientCount);

public record AdminCategoryRow(
    string Id, string Name, double MarkupPercent, double MinOrderAmount, int ShelfLifeDays, int ClientCount);

public record PriceListRow(
    string Id, string Name, string? Description, bool Active, string? SourceName,
    /// <summary>The margin everything on it earns, or null where it states none.</summary>
    double? MarkupPercent,
    int ItemCount, DateTime CreatedAt, DateTime UpdatedAt);

/// <param name="SourcePartNumber">The supplier's own number, where the row was matched through it.</param>
/// <param name="BasePrice">What the part costs without this list, so the change is visible.</param>
public record PriceListLineRow(
    string ProductId, string PartNumber, string Name, double Price,
    double? SourcePrice, string? SourceCurrency, string? SourcePartNumber,
    /// <summary>
    /// This line's own margin, or null where it states none. The narrowest
    /// rung of the purchase-side chain, and the only per-part one. Null falls
    /// through to the list, then the supplier — it is not the same as zero,
    /// which sells this part at cost and stops the search.
    /// </summary>
    double? MarkupPercent,
    double BasePrice);

/// <summary>One upload, as the history lists it.</summary>
/// <remarks>
/// Not <c>PriceListImportRow</c>, which every other query row here would be
/// called: that is the name of the TABLE holding the rejected lines, and a
/// type by that name would mean the opposite of what the table does. It shares
/// its name with the other API's type instead.
/// </remarks>
/// <param name="PriceListId">Null when the file was refused, or its list has since been deleted.</param>
/// <param name="RejectedStored">How many of <paramref name="Rejected"/> can be read back.</param>
/// <param name="ListActive">Whether the list this made is the one setting prices right now.</param>
public record PriceListImportSummary(
    string Id, string? PriceListId, string ListName, string? SourceName,
    string? UploadedById, string UploadedByName, string Outcome,
    int RowsSent, int Accepted, int Rejected, int RejectedStored,
    string? Error, DateTime CreatedAt, bool ListActive);

/// <summary>One line of an upload that did not make it, as stored.</summary>
public record RejectedLineRow(int Line, string PartNumber, string Price, string? Currency, string Reason);

public record AdminClientRow(
    string Id, string Name, string Email, string Role, string? City, bool HasLogin,
    string? CategoryId, string? CategoryName, double DiscountPercent,
    string? CurrencyId, string? CurrencyCode, string? SalesManagerId, string? SalesManagerName);
