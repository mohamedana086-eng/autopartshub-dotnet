namespace AutoPartsHub.Api.Pricing;

/// <summary>
/// Resolves the final client-facing price for a (client, product) pair.
/// </summary>
/// <remarks>
/// Order of operations. Each concept applies exactly once, in this order:
/// <code>
///   purchase price
///     -> markup   the most specific matching rule, else the tier default
///     -> discount the account's negotiated percentage, off the marked-up price
///     -> currency converted into what the account is quoted in
/// </code>
/// Markup mirrors the "Complex markup" rule builder in the admin: a rule can
/// filter on client category, supplier, manufacturer, vehicle system,
/// part-number prefix, and a purchase-price band. Any filter left empty means
/// "any". When several rules match, the MOST SPECIFIC one wins (most non-null
/// filters), and <c>Priority</c> breaks ties. If no rule matches, the client's
/// category default markup applies.
///
/// Discount is deliberately a separate step rather than another rule type.
/// Inside the engine it would have had to either beat the markup or lose to
/// it, since only one rule can win — and a discount that cancels the markup
/// instead of reducing it is not what "10% off" means. Kept outside, the two
/// compose: the tier decides the price, the account's discount comes off it.
///
/// Currency is last, and only ever multiplies. Discounting after conversion
/// would give a different answer per currency for the same agreed percentage.
/// </remarks>
public static class PricingEngine
{
    /// <summary>The base currency, for accounts that are quoted in it.</summary>
    private static readonly PricingCurrency BaseCurrency = new("EUR", "€", 1);

    private static double Round(double value) => Math.Round(value * 100, MidpointRounding.AwayFromZero) / 100;

    public static PriceResult Resolve(PricingContext ctx, IReadOnlyList<MarkupRule> rules)
    {
        // Sorted by specificity then priority. OrderByDescending is a stable
        // sort in .NET, as Array.prototype.sort is in every engine that
        // matters, so rules that tie on both keep the order they arrived in —
        // and they arrive ordered by id, which is what makes the answer the
        // same twice running.
        var winner = rules
            .Where(r => Matches(r, ctx))
            .OrderByDescending(Specificity)
            .ThenByDescending(r => r.Priority)
            .FirstOrDefault();

        // 1. Markup — one winner, down a ladder of three rungs.
        //
        //      a matching rule       most specific wins, then priority
        //      the goods category    what the shop decided this KIND is worth
        //      the client category   the account's generic default
        //
        //  The middle rung sits where it does deliberately. A goods category
        //  is a statement about this particular part — somebody put it in
        //  "slow-moving" on purpose — where the client category default is a
        //  catch-all for the buyer covering everything nobody has priced. The
        //  more deliberate statement wins.
        //
        //  A rule still beats both, because a rule is how a category baseline
        //  gets overridden for particular customers: "consumables are +35%,
        //  but trade accounts pay +22%".
        var categoryMarkup = ctx.GoodsCategoryMarkup;

        var markedUp = winner is not null
            ? ApplyMarkup(ctx.BasePrice, winner.Type, winner.Value)
            : categoryMarkup is not null
                ? ApplyMarkup(ctx.BasePrice, categoryMarkup.Type, categoryMarkup.Value)
                : ApplyMarkup(ctx.BasePrice, MarkupType.Percent, ctx.ClientCategoryMarkupPercent);

        // 2. Discount. Clamped to 0–100: a negative one would quietly become a
        //    surcharge, and over 100 would pay the customer to take the part.
        var discountPercent = Math.Min(100, Math.Max(0, ctx.DiscountPercent ?? 0));
        var discounted = markedUp * (1 - discountPercent / 100);

        // 3. Currency, last and multiplicative only.
        var currency = ctx.Currency ?? BaseCurrency;
        var finalPrice = Round(discounted * currency.Rate);

        // Margin stays a fact about the sale in the base currency: converting
        // it would leave the number unchanged but invite reading it as a rate.
        var marginPercent = ctx.BasePrice > 0
            ? Math.Round(((Round(discounted) - ctx.BasePrice) / ctx.BasePrice) * 1000, MidpointRounding.AwayFromZero) / 10
            : 0;

        // Names the rung that decided, not just the number. A customer asking
        // why a part costs what it does gets an answer they can act on, and so
        // does the person who has to explain it.
        var markupLabel = winner is not null
            ? winner.Label
            : categoryMarkup is not null
                ? $"{categoryMarkup.Label} category markup"
                : "Client category default markup";

        return new PriceResult(
            BasePrice: ctx.BasePrice,
            FinalPrice: finalPrice,
            NetBase: Round(discounted),
            AppliedRule: discountPercent > 0
                ? $"{markupLabel} · less {Format(discountPercent)}% account discount"
                : markupLabel,
            MarginPercent: marginPercent,
            DiscountPercent: discountPercent,
            PriceBeforeDiscount: Round(markedUp * currency.Rate),
            CurrencyCode: currency.Code,
            CurrencySymbol: currency.Symbol);
    }

    /// <summary>
    /// A number the way JavaScript writes it into a string.
    /// </summary>
    /// <remarks>
    /// The discount reaches the customer inside a sentence, and 7.5 has to
    /// read as "7.5" and 10 as "10" — not "7.50" or "10.00". Invariant
    /// culture, so a machine set to a comma decimal separator does not quote
    /// "7,5% account discount" to one customer and "7.5%" to another.
    /// </remarks>
    private static string Format(double value) =>
        value.ToString("0.############", System.Globalization.CultureInfo.InvariantCulture);

    private static bool Matches(MarkupRule rule, PricingContext ctx)
    {
        if (!rule.Active) return false;

        if (!string.IsNullOrEmpty(rule.ClientCategoryId) && rule.ClientCategoryId != ctx.ClientCategoryId) return false;
        if (!string.IsNullOrEmpty(rule.SupplierId) && rule.SupplierId != ctx.SupplierId) return false;
        // An unclassified part matches no category-scoped rule. Written
        // against the rule's own value rather than the context's, so a part
        // with no category falls through every one of them instead of
        // matching the first.
        if (!string.IsNullOrEmpty(rule.GoodsCategoryId) && rule.GoodsCategoryId != ctx.GoodsCategoryId) return false;
        if (!string.IsNullOrEmpty(rule.ManufacturerName)
            && !rule.ManufacturerName.Equals(ctx.ManufacturerName, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrEmpty(rule.VehicleSystemSlug) && rule.VehicleSystemSlug != ctx.VehicleSystemSlug) return false;
        if (!string.IsNullOrEmpty(rule.PartNumberPrefix)
            && !ctx.PartNumber.StartsWith(rule.PartNumberPrefix, StringComparison.OrdinalIgnoreCase)) return false;
        if (rule.PurchasePriceFrom is not null && ctx.BasePrice < rule.PurchasePriceFrom) return false;
        if (rule.PurchasePriceTo is not null && ctx.BasePrice > rule.PurchasePriceTo) return false;

        return true;
    }

    private static int Specificity(MarkupRule rule) =>
        (string.IsNullOrEmpty(rule.ClientCategoryId) ? 0 : 1)
        + (string.IsNullOrEmpty(rule.SupplierId) ? 0 : 1)
        + (string.IsNullOrEmpty(rule.GoodsCategoryId) ? 0 : 1)
        + (string.IsNullOrEmpty(rule.ManufacturerName) ? 0 : 1)
        + (string.IsNullOrEmpty(rule.VehicleSystemSlug) ? 0 : 1)
        + (string.IsNullOrEmpty(rule.PartNumberPrefix) ? 0 : 1)
        + (rule.PurchasePriceFrom is not null || rule.PurchasePriceTo is not null ? 1 : 0);

    private static double ApplyMarkup(double basePrice, MarkupType type, double value) => type switch
    {
        MarkupType.Percent => basePrice * (1 + value / 100),
        MarkupType.Amount => basePrice + value,
        MarkupType.Fixed => value,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown markup type"),
    };
}

public enum MarkupType { Percent, Amount, Fixed }

public record MarkupRule(
    string Id,
    string Label,
    int Priority,
    string? ClientCategoryId,
    string? SupplierId,
    /// <summary>Narrows the rule to one goods category. Null is "any".</summary>
    string? GoodsCategoryId,
    string? ManufacturerName,
    string? VehicleSystemSlug,
    string? PartNumberPrefix,
    double? PurchasePriceFrom,
    double? PurchasePriceTo,
    MarkupType Type,
    double Value,
    bool Active);

/// <param name="BasePrice">Supplier purchase price, in the base currency.</param>
/// <param name="ClientCategoryMarkupPercent">Fallback when no rule matches.</param>
/// <param name="DiscountPercent">The account's negotiated discount, off the marked-up price.</param>
/// <param name="Currency">What the account is quoted in. Null means the base currency.</param>
public record PricingContext(
    double BasePrice,
    string SupplierId,
    string ManufacturerName,
    string VehicleSystemSlug,
    string PartNumber,
    string ClientCategoryId,
    double ClientCategoryMarkupPercent,
    double? DiscountPercent = null,
    PricingCurrency? Currency = null,
    /// <summary>The goods category this part is in, or null if unclassified.</summary>
    string? GoodsCategoryId = null,
    /// <summary>
    /// That category's own markup, already resolved by the caller.
    ///
    /// Passed in rather than looked up, so this stays a pure function of its
    /// arguments — the same property that lets the engine be compared against
    /// the TypeScript original over four hundred generated cases.
    /// </summary>
    GoodsCategoryMarkup? GoodsCategoryMarkup = null);

/// <summary>
/// A goods category's own markup — the baseline for everything in it.
/// </summary>
/// <remarks>
/// Applied only when no rule matched. A rule scoped to the category is how the
/// baseline gets overridden for particular customers, which is why a rule
/// always wins: it is the more specific statement of the two.
/// </remarks>
/// <param name="Label">Named in <c>AppliedRule</c>, so a quote can say which category decided.</param>
public record GoodsCategoryMarkup(string Label, MarkupType Type, double Value);

/// <summary>Enough of a Currency row to convert and label a price.</summary>
/// <param name="Rate">Units of this currency per one unit of the base. 1 on the base.</param>
public record PricingCurrency(string Code, string Symbol, double Rate);

/// <param name="FinalPrice">What the customer is quoted: after discount, in their currency.</param>
/// <param name="NetBase">
/// The same price after discount but before conversion, in the base currency.
/// This is the figure to store and to compare against anything else
/// denominated in the base — an order's line prices, a tier's minimum order —
/// since those cannot be read against a total that changes with whichever
/// currency the account happens to be set to.
/// </param>
/// <param name="AppliedRule">Human readable: which rule or default won.</param>
/// <param name="DiscountPercent">The discount taken off, so a quote can show it rather than imply it.</param>
/// <param name="PriceBeforeDiscount">Price before the discount, in the quoted currency.</param>
public record PriceResult(
    double BasePrice,
    double FinalPrice,
    double NetBase,
    string AppliedRule,
    double MarginPercent,
    double DiscountPercent,
    double PriceBeforeDiscount,
    string CurrencyCode,
    string CurrencySymbol);
