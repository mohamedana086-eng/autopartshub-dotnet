namespace AutoPartsHub.Domain.Pricing;

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
/// Markup mirrors the "Complex markup" rule builder in the admin. A rule holds
/// conditions, each naming a dimension and one acceptable value on it — see
/// <see cref="MarkupDimensions"/>. Every dimension the rule mentions must be
/// satisfied, and a dimension is satisfied by ANY of its values: "the supplier
/// is among these three" is one condition to meet, not three. A rule with no
/// conditions applies to everything.
///
/// When several rules match, the MOST SPECIFIC one wins — one point per
/// dimension it narrows on, whatever the length of the list — and
/// <c>Priority</c> breaks ties. If no rule matches, the client's category
/// default markup applies.
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

    /// <summary>
    /// A number rounded the way JavaScript's <c>Math.round</c> rounds it.
    /// </summary>
    /// <remarks>
    /// Not <c>MidpointRounding.AwayFromZero</c>, which is what this used to be.
    /// The two agree on every positive midpoint and disagree on every negative
    /// one: JavaScript rounds a half TOWARDS POSITIVE INFINITY, so -16.095
    /// becomes -16.09, where away-from-zero makes it -16.10. A cent, on a
    /// number that only appears when a markup or a margin comes out negative —
    /// which is rare enough to have gone unnoticed and real enough to matter,
    /// since the other API is the one serving customers and this one has to
    /// agree with it rather than be independently defensible.
    ///
    /// Found by the four-hundred-case differential the day its inputs first
    /// produced a negative midpoint.
    /// </remarks>
    private static double JsRound(double value) => Math.Floor(value + 0.5);

    private static double Round(double value) => JsRound(value * 100) / 100;

    public static PriceResult Resolve(PricingContext ctx, IReadOnlyList<MarkupRule> rules)
    {
        // Sorted by specificity then priority. OrderByDescending is a stable
        // sort in .NET, as Array.prototype.sort is in every engine that
        // matters, so rules that tie on both keep the order they arrived in —
        // and they arrive ordered by id, which is what makes the answer the
        // same twice running.
        // Read once, here, rather than inside the matcher — so the engine
        // stays a pure function of what it is handed whenever a caller says
        // what time it is, and still enforces a window when one does not.
        var now = ctx.NowMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var winner = rules
            .Where(r => Matches(r, ctx, now))
            .OrderByDescending(r => r.Specificity)
            .ThenByDescending(r => r.Priority)
            .FirstOrDefault();

        // 1. Markup — one winner, down a ladder of four rungs.
        //
        //      a matching rule       most specific wins, then priority
        //      the purchase side     line, list, supplier — resolved already
        //      the goods category    what the shop decided this KIND is worth
        //      the client category   the account's generic default
        //
        //  The bottom two sit where they do deliberately. A goods category is
        //  a statement about this particular part — somebody put it in
        //  "slow-moving" on purpose — where the client category default is a
        //  catch-all for the buyer covering everything nobody has priced. The
        //  more deliberate statement wins.
        //
        //  The purchase rung sits above the category because all three of its
        //  own rungs name a commercial arrangement — this line, this file,
        //  this supplier — where a category names a kind of thing. Somebody
        //  setting a margin against a supplier means "this is what we make on
        //  their parts", and a category baseline quietly outranking that would
        //  make the field decorative.
        //
        //  A rule still beats all of them, and that ordering is load-bearing
        //  rather than a preference. A rule is the ONLY markup that can see who
        //  is asking. If a supplier's margin outranked one, typing a number
        //  into a supplier form would silently switch off every
        //  customer-specific rule on their parts — "consumables are +35%, but
        //  trade accounts pay +22%" would quietly stop being true, with nothing
        //  on any screen to say so.
        var purchaseMarkup = ctx.PurchaseMarkup;
        var categoryMarkup = ctx.GoodsCategoryMarkup;

        var markedUp = winner is not null
            ? ApplyMarkup(ctx.BasePrice, winner.Type, winner.Value, winner.MinAmount)
            : purchaseMarkup is not null
                ? ApplyMarkup(ctx.BasePrice, MarkupType.Percent, purchaseMarkup.Percent)
                : categoryMarkup is not null
                    ? ApplyMarkup(
                        ctx.BasePrice, categoryMarkup.Type, categoryMarkup.Value,
                        categoryMarkup.MinAmount)
                    : ApplyMarkup(
                        ctx.BasePrice, MarkupType.Percent, ctx.ClientCategoryMarkupPercent);

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
            ? JsRound(((Round(discounted) - ctx.BasePrice) / ctx.BasePrice) * 1000) / 10
            : 0;

        // Names the rung that decided, not just the number. A customer asking
        // why a part costs what it does gets an answer they can act on, and so
        // does the person who has to explain it.
        var markupLabel = winner is not null
            ? winner.Label
            : purchaseMarkup is not null
                ? purchaseMarkup.Label
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

    /// <summary>
    /// What the request can answer on one dimension.
    /// </summary>
    /// <remarks>
    /// An unknown dimension answers null, which makes every condition on it
    /// match nothing — so a rule written by a newer version of the software
    /// stops applying rather than applying wrongly. That is the safe
    /// direction: the account default catches it and the customer is quoted a
    /// price somebody meant, rather than one nobody checked.
    /// </remarks>
    private static string? SubjectOf(string dimension, PricingContext ctx) => dimension switch
    {
        // the part
        "supplier" => string.IsNullOrEmpty(ctx.SupplierId) ? null : ctx.SupplierId,
        "supplierGroup" => ctx.SupplierGroup,
        "manufacturer" => ctx.ManufacturerName,
        "vehicleSystem" => ctx.VehicleSystemSlug,
        "goodsCategory" => ctx.GoodsCategoryId,
        "partType" => ctx.PartType,
        "partNumberPrefix" => ctx.PartNumber,
        "nameContains" => ctx.PartName,

        // the caller
        "clientCategory" => ctx.ClientCategoryId,
        "client" => ctx.ClientId,
        "clientRole" => ctx.ClientRole,
        "salesManager" => ctx.SalesManagerId,
        "outlet" => ctx.OutletId,
        "city" => ctx.City,
        "currency" => (ctx.Currency ?? BaseCurrency).Code,
        "priceList" => ctx.PriceListId,

        _ => null,
    };

    /// <summary>
    /// Whether a rule applies: every dimension satisfied, by any of its values.
    /// </summary>
    /// <remarks>
    /// Grouping first is the whole point. Conditions arrive flat — one row per
    /// acceptable value — and read flat they would mean "the supplier is
    /// sup-1 AND sup-2", which nothing could satisfy. Grouped they mean "the
    /// supplier is among these", which is what somebody writing the rule
    /// meant, and what makes a list of three rank where a list of one ranks.
    /// </remarks>
    private static bool Matches(MarkupRule rule, PricingContext ctx, long now)
    {
        if (!rule.Active) return false;

        // Outside its window the rule does not exist. Deliberately separate
        // from Active: that is somebody deciding, this decides on its own,
        // which is the whole point of a seasonal price nobody has to remember
        // to switch off.
        if (rule.StartsAtMs is not null && now < rule.StartsAtMs) return false;
        if (rule.EndsAtMs is not null && now > rule.EndsAtMs) return false;

        if (rule.PurchasePriceFrom is not null && ctx.BasePrice < rule.PurchasePriceFrom) return false;
        if (rule.PurchasePriceTo is not null && ctx.BasePrice > rule.PurchasePriceTo) return false;

        // Grouped by dimension AND direction. Direction is part of the key
        // rather than assumed uniform, so a dimension that somehow holds both
        // an inclusion and an exclusion is still answered definitely — as two
        // statements, both of which must hold — instead of depending on which
        // row was read first. The write path refuses to store that; the engine
        // does not get to assume the write path ran.
        foreach (var group in rule.Conditions.GroupBy(
                     c => (c.Dimension, c.Negated),
                     ValueTupleComparer.Instance))
        {
            var dimension = MarkupDimensions.Find(group.Key.Dimension);
            // A dimension this build does not know. Refusing to match is the
            // safe direction, the same as an unanswerable subject.
            if (dimension is null) return false;

            var subject = SubjectOf(group.Key.Dimension, ctx);
            // Neither direction is answerable when the request cannot say. A
            // customer whose city nobody knows is not in Cairo, and is not
            // outside Cairo either — the rule simply does not apply.
            if (subject is null) return false;

            var hit = group.Any(
                c => MarkupDimensions.ValueMatches(dimension.Match, c.Value, subject));
            if (hit == group.Key.Negated) return false;
        }

        return true;
    }

    /// <summary>Ordinal on the name, exact on the direction.</summary>
    private sealed class ValueTupleComparer : IEqualityComparer<(string Dimension, bool Negated)>
    {
        public static readonly ValueTupleComparer Instance = new();

        public bool Equals((string Dimension, bool Negated) a, (string Dimension, bool Negated) b) =>
            a.Negated == b.Negated && string.Equals(a.Dimension, b.Dimension, StringComparison.Ordinal);

        public int GetHashCode((string Dimension, bool Negated) key) =>
            HashCode.Combine(StringComparer.Ordinal.GetHashCode(key.Dimension), key.Negated);
    }

    private static double ApplyMarkup(
        double basePrice, MarkupType type, double value, double? minAmount = null) => type switch
    {
        MarkupType.Percent => basePrice * (1 + value / 100),
        MarkupType.Amount => basePrice + value,
        MarkupType.Fixed => value,
        // The floor is on the markup, not on the price: it is a minimum
        // profit, so a part that cost more still sells for more. Missing, it
        // is nothing, which makes this behave as a plain percentage rather
        // than as a surprise — the database refuses to store the pair so.
        MarkupType.PercentMin => basePrice + Math.Max(basePrice * value / 100, minAmount ?? 0),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown markup type"),
    };
}

/// <param name="PercentMin">A percentage with a floor in money under it.</param>
public enum MarkupType { Percent, Amount, Fixed, PercentMin }

/// <param name="Conditions">
/// What this rule narrows on. All the conditions sharing a dimension are ONE
/// statement with several acceptable answers — "the supplier is among these" —
/// so a rule naming three suppliers is exactly as specific as one naming a
/// single supplier. Different dimensions must all be satisfied. Empty is a
/// rule that applies to everything, which is a legitimate thing to write and
/// the least specific rule there is.
/// </param>
/// <param name="Specificity">
/// How many dimensions this narrows on, counted once each. Stored on the rule
/// and recomputed whenever it is saved, rather than counted here per request —
/// so the engine stays a pure function of its arguments, and the number a rule
/// was ranked by is the number somebody can read off the row.
/// </param>
public record MarkupRule(
    string Id,
    string Label,
    int Priority,
    IReadOnlyList<RuleCondition> Conditions,
    int Specificity,
    /// <summary>A range is one bound at each end, not a set to be among — so not a condition.</summary>
    double? PurchasePriceFrom,
    double? PurchasePriceTo,
    MarkupType Type,
    double Value,
    bool Active,
    /// <summary>
    /// The floor under PercentMin, in the base currency. A minimum on the
    /// markup rather than on the price — one percent of a one-euro part is a
    /// cent, which does not pay for picking it off a shelf. Null on every
    /// other type.
    /// </summary>
    double? MinAmount = null,
    /// <summary>
    /// When the rule is in force, as epoch milliseconds. Null at either end is
    /// open — no end date means "until further notice", not "expired".
    ///
    /// Milliseconds rather than dates because the two ports have to agree to
    /// the millisecond and neither timezone handling nor date parsing is the
    /// same in both languages. The loaders convert; the engine compares
    /// numbers.
    /// </summary>
    long? StartsAtMs = null,
    long? EndsAtMs = null);

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
    /// <summary>The part's own name, for the "name contains" dimension.</summary>
    string? PartName = null,
    /// <summary>oem | aftermarket | substitute.</summary>
    string? PartType = null,
    /// <summary>Who is asking, for the dimensions that describe the caller rather than the part.</summary>
    string? ClientId = null,
    string? ClientRole = null,
    string? SalesManagerId = null,
    string? City = null,
    /// <summary>Which outlet this account buys through — منفذ البيع.</summary>
    string? OutletId = null,
    /// <summary>The supplier's business grouping — مجموعة الموردين.</summary>
    string? SupplierGroup = null,
    /// <summary>The purchase price list in force, so a rule can apply only while it is.</summary>
    string? PriceListId = null,
    /// <summary>
    /// Now, as epoch milliseconds, for rules that are only in force for a
    /// while. Passed in so a window can be compared without the engine reading
    /// a clock, which is what lets the same generated cases run through both
    /// ports. Absent, it falls back to the real clock — a window nobody
    /// enforces would be worse than a function reading one value from outside.
    /// </summary>
    long? NowMs = null,
    /// <summary>
    /// That category's own markup, already resolved by the caller.
    ///
    /// Passed in rather than looked up, so this stays a pure function of its
    /// arguments — the same property that lets the engine be compared against
    /// the TypeScript original over four hundred generated cases.
    /// </summary>
    GoodsCategoryMarkup? GoodsCategoryMarkup = null,
    /// <summary>
    /// The margin stated on the buying side, already resolved down its own
    /// chain of line to list to supplier by <c>PurchaseMarkups.Of</c>.
    ///
    /// Passed in rather than looked up, for the same reason the category
    /// markup is: it keeps this a pure function of its arguments, which is
    /// what lets the whole engine be compared against the TypeScript original
    /// over four hundred generated cases.
    /// </summary>
    PurchaseMarkup? PurchaseMarkup = null);

/// <summary>
/// A goods category's own markup — the baseline for everything in it.
/// </summary>
/// <remarks>
/// Applied only when no rule matched. A rule scoped to the category is how the
/// baseline gets overridden for particular customers, which is why a rule
/// always wins: it is the more specific statement of the two.
/// </remarks>
/// <param name="Label">Named in <c>AppliedRule</c>, so a quote can say which category decided.</param>
/// <param name="MinAmount">The floor under PercentMin. Null on every other type.</param>
public record GoodsCategoryMarkup(
    string Label, MarkupType Type, double Value, double? MinAmount = null);

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
