using AutoPartsHub.Api.Auth;
using AutoPartsHub.Api.Data;
using AutoPartsHub.Domain.Pricing;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsHub.Api.Pricing;

/// <summary>
/// What the caller's prices are worked out from: their tier, their discount,
/// the currency they are quoted in, and the active markup rules.
/// </summary>
/// <remarks>
/// Read from the account rather than the cookie. The session is signed, so it
/// could carry these safely, but a discount agreed this morning should apply
/// on the next request rather than whenever the cookie is next reissued.
///
/// One statement for the account, its tier and its currency — they are read
/// together on every priced request in the application, and three round trips
/// to answer one question is two too many. Left joins hung off a single anchor
/// row, so an anonymous visitor still comes back with the Retail tier and the
/// base currency rather than with no row at all.
/// </remarks>
public sealed class PricingContextLoader(AutoPartsContext db, SessionTokens tokens)
{
    public async Task<RequestPricing> LoadAsync(HttpContext http, CancellationToken ct = default)
    {
        var session = tokens.Decode(http.Request.Cookies[SessionTokens.CookieName]);
        var userId = session?.UserId;
        var categoryId = session?.CategoryId;

        var account = (await db.Database.SqlQuery<AccountRow>($"""
            SELECT TOP 1 c."discountPercent" AS "DiscountPercent",
                   -- Who is asking, for the dimensions that describe the caller
                   -- rather than the part.
                   c."id" AS "ClientId", c."role" AS "ClientRole",
                   c."salesManagerId" AS "SalesManagerId", c."city" AS "City",
                   cat."id" AS "CategoryId",
                   cat."name" AS "CategoryName",
                   cat."markupPercent" AS "CategoryMarkupPercent",
                   -- The account's currency where it has an active one, and the
                   -- base row otherwise, so a deactivated currency cannot leave
                   -- somebody quoted at a rate nobody is maintaining.
                   COALESCE(cur."code", base."code") AS "CurrencyCode",
                   COALESCE(cur."symbol", base."symbol") AS "CurrencySymbol",
                   COALESCE(cur."rate", base."rate") AS "CurrencyRate"
            FROM (SELECT 1) AS anchor
            LEFT JOIN "Client" c ON c."id" = {userId}
            LEFT JOIN "ClientCategory" cat
                   ON cat."id" = {categoryId}
                   OR ({categoryId} IS NULL AND cat."name" = 'Retail')
            LEFT JOIN "Currency" cur ON cur."id" = c."currencyId" AND cur."active" = 1
            LEFT JOIN "Currency" base ON base."isBase"
            """).ToListAsync(ct)).FirstOrDefault();

        // Ordered by id, because the engine sorts by specificity then priority
        // and leaves a tie to input order. Heap order settled that before,
        // which is to say nothing settled it.
        // Raw SQL rather than LINQ over the entity: the scaffolded model does
        // not carry specificity, the same way it does not carry partType, and
        // columns added since are read this way rather than by hand-editing
        // something generated.
        var ruleRows = await db.Database.SqlQuery<MarkupRuleRow>($"""
            SELECT "id" AS "Id", "label" AS "Label", "priority" AS "Priority",
                   "specificity" AS "Specificity",
                   "purchasePriceFrom" AS "PurchasePriceFrom",
                   "purchasePriceTo" AS "PurchasePriceTo",
                   "type" AS "Type", "value" AS "Value", "active" AS "Active",
                   "minAmount" AS "MinAmount",
                   -- As epoch milliseconds, not as dates. The two ports have
                   -- to agree on a window to the millisecond, and neither
                   -- timezone handling nor date parsing is the same in both
                   -- languages — whereas a number is a number. AT TIME ZONE
                   -- 'UTC' pins down what a bare TIMESTAMP means rather than
                   -- leaving it to the driver.
                   (EXTRACT(EPOCH FROM "startsAt" AT TIME ZONE 'UTC') * 1000)::bigint
                     AS "StartsAtMs",
                   (EXTRACT(EPOCH FROM "endsAt" AT TIME ZONE 'UTC') * 1000)::bigint
                     AS "EndsAtMs"
            FROM "MarkupRule"
            WHERE "active" = 1
            ORDER BY "id" ASC
            """).ToListAsync(ct);

        // Every condition on every active rule, in one read rather than one per
        // rule. There are a few hundred at most and they are wanted together.
        var conditionRows = await db.Database.SqlQuery<RuleConditionRow>($"""
            SELECT c."ruleId" AS "RuleId", c."dimension" AS "Dimension", c."value" AS "Value",
                   c."negated" AS "Negated"
            FROM "MarkupRuleCondition" c
            JOIN "MarkupRule" r ON r."id" = c."ruleId"
            WHERE r."active" = 1
            ORDER BY c."ruleId" ASC, c."dimension" ASC, c."value" ASC
            """).ToListAsync(ct);

        // Grouped here rather than joined: a join would repeat every rule once
        // per condition and have to be folded back together anyway.
        var conditionsByRule = conditionRows
            .GroupBy(c => c.RuleId, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<RuleCondition>)g
                    .Select(c => new RuleCondition(c.Dimension, c.Value, c.Negated)).ToList(),
                StringComparer.Ordinal);

        var rules = ruleRows
            .Select(r => new MarkupRule(
                r.Id, r.Label, r.Priority,
                conditionsByRule.GetValueOrDefault(r.Id, []),
                r.Specificity,
                r.PurchasePriceFrom, r.PurchasePriceTo,
                ReadMarkupType(r.Type),
                r.Value, r.Active,
                r.MinAmount, r.StartsAtMs, r.EndsAtMs))
            .ToList();

        // Which purchase price list is in force, so a rule can apply only while
        // a particular one is. At most one is active, which the database
        // enforces with a partial unique index.
        //
        // Its name and margin come back with the id: the margin is the middle
        // rung of the purchase-side chain, and the name is what a quote says
        // when that rung decides the price.
        var activeList = (await db.Database.SqlQuery<ActiveListRow>($"""
            SELECT TOP 1 "id" AS "Id", "name" AS "Name", "markupPercent" AS "MarkupPercent"
            FROM "PriceList" WHERE "active" = 1
            """).ToListAsync(ct)).FirstOrDefault();

        // The suppliers that state a margin, as a lookup for the same reason
        // the goods categories are one: which supplier a part comes from is
        // already on its row, and there are a few hundred suppliers against
        // tens of thousands of parts. A join would have to be added to all six
        // queries that price a row, and one of them forgetting it is exactly
        // the silent-wrong-price failure the BestOffer join test exists for.
        var supplierMarkupRows = await db.Database.SqlQuery<SupplierMarkupRow>($"""
            SELECT "id" AS "Id", "name" AS "Name", "markupPercent" AS "MarkupPercent"
            FROM "Supplier"
            WHERE "markupPercent" IS NOT NULL
            """).ToListAsync(ct);

        var supplierMarkups = supplierMarkupRows.ToDictionary(
            s => s.Id, s => (s.Name, s.MarkupPercent), StringComparer.Ordinal);

        // The goods categories that price something, as a lookup rather than a
        // join on every priceable query.
        //
        // A part's row carries only its category id; the markup itself is the
        // same handful of rows for every part in a response, so loading them
        // once beside the rules costs one round trip instead of a join on
        // search, the part page, bulk, the basket and the order.
        //
        // Only the active ones with a markup set: a switched-off category, or
        // one that is purely organisational, prices nothing.
        var categoryRows = await db.Database.SqlQuery<GoodsCategoryMarkupRow>($"""
            SELECT "id" AS "Id", "name" AS "Name",
                   "markupType" AS "MarkupType", "markupValue" AS "MarkupValue",
                   "markupMinAmount" AS "MarkupMinAmount"
            FROM "GoodsCategory"
            WHERE "active" = 1 AND "markupType" IS NOT NULL AND "markupValue" IS NOT NULL
            """).ToListAsync(ct);

        var goodsCategoryMarkups = categoryRows.ToDictionary(
            g => g.Id,
            g => new GoodsCategoryMarkup(
                g.Name, ReadMarkupType(g.MarkupType), g.MarkupValue, g.MarkupMinAmount));

        var currency = account is { CurrencyCode: not null, CurrencySymbol: not null, CurrencyRate: not null }
            ? new PricingCurrency(account.CurrencyCode, account.CurrencySymbol, account.CurrencyRate.Value)
            : null;

        return new RequestPricing(
            CategoryId: account?.CategoryId,
            CategoryMarkupPercent: account?.CategoryMarkupPercent,
            Rules: rules,
            TierName: account?.CategoryName ?? "Retail",
            IsLoggedIn: session is not null,
            DiscountPercent: account?.DiscountPercent ?? 0,
            Currency: currency,
            GoodsCategoryMarkups: goodsCategoryMarkups,
            ClientId: account?.ClientId,
            ClientRole: account?.ClientRole,
            SalesManagerId: account?.SalesManagerId,
            City: account?.City,
            PriceListId: activeList?.Id,
            PriceListName: activeList?.Name,
            PriceListMarkupPercent: activeList?.MarkupPercent,
            SupplierMarkups: supplierMarkups);
    }

    /// <summary>
    /// The stored word as the engine's enum.
    /// </summary>
    /// <remarks>
    /// Anything unrecognised reads as PERCENT, which is what the column
    /// defaults to and what the check constraint keeps it to. Shared between
    /// the rules and the categories because they are the same three kinds —
    /// two copies would disagree the day a fourth is added.
    /// </remarks>
    private static MarkupType ReadMarkupType(string stored) => stored switch
    {
        "AMOUNT" => MarkupType.Amount,
        "FIXED" => MarkupType.Fixed,
        "PERCENT_MIN" => MarkupType.PercentMin,
        _ => MarkupType.Percent,
    };

    private record MarkupRuleRow(
        string Id, string Label, int Priority, int Specificity,
        double? PurchasePriceFrom, double? PurchasePriceTo,
        string Type, double Value, bool Active,
        double? MinAmount, long? StartsAtMs, long? EndsAtMs);

    private record RuleConditionRow(
        string RuleId, string Dimension, string Value, bool Negated);

    private record GoodsCategoryMarkupRow(
        string Id, string Name, string MarkupType, double MarkupValue, double? MarkupMinAmount);

    /// <summary>The list in force, with the margin it states.</summary>
    private record ActiveListRow(string Id, string Name, double? MarkupPercent);

    /// <summary>One supplier who has stated a margin.</summary>
    private record SupplierMarkupRow(string Id, string Name, double MarkupPercent);

    private record AccountRow(
        double? DiscountPercent,
        string? ClientId,
        string? ClientRole,
        string? SalesManagerId,
        string? City,
        string? CategoryId,
        string? CategoryName,
        double? CategoryMarkupPercent,
        string? CurrencyCode,
        string? CurrencySymbol,
        double? CurrencyRate);
}

public record RequestPricing(
    string? CategoryId,
    double? CategoryMarkupPercent,
    List<MarkupRule> Rules,
    string TierName,
    bool IsLoggedIn,
    double DiscountPercent,
    PricingCurrency? Currency,
    /// <summary>
    /// Every goods category that prices something, by id.
    ///
    /// A lookup rather than a join: the same handful of rows applies to every
    /// part in a response.
    /// </summary>
    Dictionary<string, GoodsCategoryMarkup> GoodsCategoryMarkups,
    /// <summary>Who is asking, for the dimensions that describe the caller.</summary>
    string? ClientId = null,
    string? ClientRole = null,
    string? SalesManagerId = null,
    string? City = null,
    /// <summary>The purchase price list in force, or null when none is.</summary>
    string? PriceListId = null,
    /// <summary>
    /// That list's name and its own margin — the middle rung of the chain a
    /// bought part's markup comes down. Both null when no list is active.
    /// </summary>
    string? PriceListName = null,
    double? PriceListMarkupPercent = null,
    /// <summary>
    /// The suppliers that state a margin, by id — the bottom rung of that
    /// chain. A lookup for the same reason GoodsCategoryMarkups is one.
    /// </summary>
    Dictionary<string, (string Name, double MarkupPercent)>? SupplierMarkups = null)
{
    /// <summary>
    /// Prices one row, or null when there is no tier to price against — which
    /// is what the callers fall back to the purchase price on.
    /// </summary>
    public PriceResult? Price(IPriceable row)
    {
        if (CategoryId is null || CategoryMarkupPercent is null) return null;

        return PricingEngine.Resolve(new PricingContext(
            BasePrice: PurchasePrice(row),
            // Whose offer won, where one did. This was once whichever supplier
            // the table happened to return first, which made every supplier
            // markup rule either dead or catalogue-wide depending on row order;
            // then it was the part's single supplier column; now a part can
            // have several and the BestOffer view names the one we would
            // actually buy from.
            SupplierId: SupplierIdFor(row),
            ManufacturerName: row.ManufacturerName,
            VehicleSystemSlug: row.SystemSlug,
            PartNumber: row.PartNumber,
            ClientCategoryId: CategoryId,
            ClientCategoryMarkupPercent: CategoryMarkupPercent.Value,
            DiscountPercent: DiscountPercent,
            Currency: Currency,
            GoodsCategoryId: row.GoodsCategoryId,
            // Null where the part has no category, or where its category holds
            // no opinion about price — the lookup only holds the ones that do,
            // so both cases come out of it the same way.
            GoodsCategoryMarkup: row.GoodsCategoryId is null
                ? null
                : GoodsCategoryMarkups.GetValueOrDefault(row.GoodsCategoryId),
            // The buying side's own chain, resolved before the engine sees it.
            // The supplier looked up is the one SupplierIdFor names — whose
            // offer won — so the margin follows the part to whoever we would
            // actually buy it from today, rather than to whoever first
            // supplied it.
            PurchaseMarkup: PurchaseMarkups.Of(new PurchaseMarkupSource(
                ListPrice: row.ListPrice,
                RowMarkupPercent: row.ListRowMarkupPercent,
                ListMarkupPercent: PriceListMarkupPercent,
                ListName: PriceListName,
                SupplierMarkupPercent: SupplierMarkupFor(row)?.MarkupPercent,
                SupplierName: SupplierMarkupFor(row)?.Name)),
            // The clock, read once per priced row rather than inside the
            // engine, so the engine stays a pure function of what it is handed.
            NowMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            PartName: row.Name,
            PartType: row.PartType,
            ClientId: ClientId,
            ClientRole: ClientRole,
            SalesManagerId: SalesManagerId,
            City: City,
            PriceListId: PriceListId), Rules);
    }

    /// <summary>
    /// The margin of the supplier this part would actually be bought from.
    /// </summary>
    /// <remarks>
    /// Null when nobody has stated one for them, which is the state every
    /// supplier is in until an admin types a number into the field.
    /// </remarks>
    private (string Name, double MarkupPercent)? SupplierMarkupFor(IPriceable row)
    {
        if (SupplierMarkups is null) return null;

        return SupplierMarkups.TryGetValue(SupplierIdFor(row), out var found) ? found : null;
    }

    /// <summary>
    /// What the part cost to buy.
    /// </summary>
    /// <remarks>
    /// Three rungs, most specific first:
    ///
    /// <list type="number">
    ///   <item>the ACTIVE PRICE LIST, where it covers this part — a figure
    ///         somebody uploaded against a dated file from a named supplier,
    ///         which is the most deliberate answer there is</item>
    ///   <item>the BEST OFFER, where a supplier offers the part — standing
    ///         terms rather than a quarter's file</item>
    ///   <item><c>basePrice</c>, the part's own stored cost</item>
    /// </list>
    ///
    /// The order is the interesting part. A price list is deliberately ABOVE a
    /// standing offer, because uploading one is the act of saying "these are
    /// the prices now" — and a list that could be silently outranked by an
    /// offer nobody looked at would make that act meaningless.
    /// </remarks>
    public static double PurchasePrice(IPriceable row) =>
        row.ListPrice ?? row.OfferPrice ?? row.BasePrice;

    /// <summary>
    /// Which supplier the markup rules should treat this part as coming from.
    /// </summary>
    /// <remarks>
    /// The one whose offer won, where there is one. Falling back to the part's
    /// own column keeps a part with no offers behaving as it did — and that
    /// column is the supplier it was first sourced from, which is the only
    /// answer available when nobody has offered it since.
    /// </remarks>
    public static string SupplierIdFor(IPriceable row) =>
        row.OfferSupplierId ?? row.SupplierId ?? "";
}

/// <summary>A row with enough on it to be priced. Flat, because a join returns columns.</summary>
public interface IPriceable
{
    double BasePrice { get; }
    string PartNumber { get; }
    string? SupplierId { get; }
    string ManufacturerName { get; }
    string SystemSlug { get; }
    double? ListPrice { get; }

    /// <summary>
    /// That same line's own margin, or null where it states none.
    /// </summary>
    /// <remarks>
    /// Per-row, and so on the row rather than in a lookup: it is the one rung
    /// of the purchase-side chain that differs from part to part. It comes off
    /// the same PriceListItem join that produces <c>ListPrice</c>, which is
    /// what keeps the margin and the cost from disagreeing about whether the
    /// file covers this part.
    /// </remarks>
    double? ListRowMarkupPercent { get; }

    /// <summary>
    /// What the best supplier offer charges, or null where none offers it.
    /// </summary>
    /// <remarks>
    /// A part can be bought from several suppliers. Which of their offers is
    /// "best" is decided in one place — the <c>BestOffer</c> view — rather than
    /// by the six queries that build a priceable row. They have to agree
    /// exactly: a part that costs one thing in search and another in the basket
    /// is the worst kind of wrong.
    ///
    /// On the interface rather than left to each record, so that adding it
    /// breaks every query that has not joined the view. The other API needs a
    /// runtime guard for the same purpose because its rows come out of casts;
    /// here the compiler does it.
    /// </remarks>
    double? OfferPrice { get; }

    /// <summary>Whose offer that was, for the supplier markup dimension.</summary>
    string? OfferSupplierId { get; }

    /// <summary>
    /// The commercial category the part is in, or null.
    ///
    /// Only the id: the markup it carries is loaded once per request with the
    /// rules, because it is the same handful of rows for every part in a
    /// response.
    /// </summary>
    string? GoodsCategoryId { get; }

    /// <summary>The part's own name, for the "name contains" dimension.</summary>
    string Name { get; }

    /// <summary>oem | aftermarket | substitute, for the "part type" dimension.</summary>
    string PartType { get; }
}
