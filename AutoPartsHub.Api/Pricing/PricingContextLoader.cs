using AutoPartsHub.Api.Auth;
using AutoPartsHub.Api.Data;
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
            SELECT c."discountPercent" AS "DiscountPercent",
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
                   OR ({categoryId}::text IS NULL AND cat."name" = 'Retail')
            LEFT JOIN "Currency" cur ON cur."id" = c."currencyId" AND cur."active"
            LEFT JOIN "Currency" base ON base."isBase"
            LIMIT 1
            """).ToListAsync(ct)).FirstOrDefault();

        // Ordered by id, because the engine sorts by specificity then priority
        // and leaves a tie to input order. Heap order settled that before,
        // which is to say nothing settled it.
        var rules = await db.MarkupRules
            .Where(r => r.Active)
            .OrderBy(r => r.Id)
            .Select(r => new MarkupRule(
                r.Id, r.Label, r.Priority, r.ClientCategoryId, r.SupplierId,
                r.ManufacturerName, r.VehicleSystemSlug, r.PartNumberPrefix,
                r.PurchasePriceFrom, r.PurchasePriceTo,
                r.Type == "AMOUNT" ? MarkupType.Amount
                    : r.Type == "FIXED" ? MarkupType.Fixed
                    : MarkupType.Percent,
                r.Value, r.Active))
            .AsNoTracking()
            .ToListAsync(ct);

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
            Currency: currency);
    }

    private record AccountRow(
        double? DiscountPercent,
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
    PricingCurrency? Currency)
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
            // The part's own supplier. This was once whichever supplier the
            // table happened to return first, which made every supplier markup
            // rule either dead or catalogue-wide depending on row order.
            SupplierId: row.SupplierId ?? "",
            ManufacturerName: row.ManufacturerName,
            VehicleSystemSlug: row.SystemSlug,
            PartNumber: row.PartNumber,
            ClientCategoryId: CategoryId,
            ClientCategoryMarkupPercent: CategoryMarkupPercent.Value,
            DiscountPercent: DiscountPercent,
            Currency: Currency), Rules);
    }

    /// <summary>
    /// What the part cost to buy: the active price list's figure where one
    /// covers it, the part's own price where none does.
    /// </summary>
    public static double PurchasePrice(IPriceable row) => row.ListPrice ?? row.BasePrice;
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
}
