namespace AutoPartsHub.Api.Pricing;

/// <summary>How a condition's value is compared to what the request offers.</summary>
public enum MatchKind
{
    /// <summary>Same string, exactly. Ids.</summary>
    Exact,
    /// <summary>Same string, ignoring case. Names people type.</summary>
    Insensitive,
    /// <summary>The subject starts with the value, ignoring case.</summary>
    Prefix,
    /// <summary>The value appears anywhere in the subject, ignoring case.</summary>
    Contains,
}

/// <param name="Name">What the condition rows carry, and what the API takes.</param>
/// <param name="Label">What it is called on screen.</param>
/// <param name="Side">
/// <c>part</c> — a property of the thing being priced, the same for every
/// caller. <c>caller</c> — a property of who is asking, the same for every
/// part. Only used to group the admin form, but worth stating: a rule mixing
/// both says "this part, for this kind of customer", which is the useful kind.
/// </param>
/// <param name="Hint">A sentence for the form, because a dimension nobody understands is one nobody uses.</param>
public record Dimension(string Name, string Label, MatchKind Match, string Side, string Hint);

/// <summary>
/// What a markup rule can narrow on. The mirror of lib/markup-dimensions.ts.
/// </summary>
/// <remarks>
/// ADDING A DIMENSION IS ADDING A NAME HERE. That is the point of the
/// conditions table: a new dimension needs an entry below, something to
/// compare it against in the pricing context, and nothing else — no migration,
/// no join table, no new branch in the engine.
///
/// EVERY DIMENSION IS A LIST. A rule holds conditions, and all the conditions
/// on one dimension are one statement: "the supplier is among these". Three
/// suppliers is not three conditions to satisfy, it is one condition with
/// three acceptable answers — which is why a rule naming three is exactly as
/// specific as one naming one.
///
/// The purchase-price range is deliberately NOT here. A range is one bound at
/// each end rather than a set of values to be among, so it stays two columns
/// on the rule and counts as one dimension of its own.
///
/// EVERY DIMENSION CAN BE TURNED INSIDE OUT. A condition group can be an
/// exclusion — "any brand EXCEPT these" — which is a different question from a
/// longer list, not a bigger one. It is the only way to say "every brand but
/// one" without naming every other brand in the catalogue. An exclusion does
/// not change how specific a rule is: it narrows on the same dimension an
/// inclusion narrows on, and the direction of a statement is not its size.
/// </remarks>
public static class MarkupDimensions
{
    public static readonly IReadOnlyList<Dimension> All =
    [
        // ------------------------------------------------------------- the part
        new("supplier", "Supplier", MatchKind.Exact, "part",
            "Parts bought from any of these suppliers."),
        new("manufacturer", "Brand", MatchKind.Insensitive, "part",
            "Parts made by any of these brands."),
        new("vehicleSystem", "Vehicle system", MatchKind.Exact, "part",
            "Parts belonging to any of these systems — brakes, cooling, filters."),
        new("goodsCategory", "Goods category", MatchKind.Exact, "part",
            "Parts filed in any of these commercial categories."),
        new("partType", "Part type", MatchKind.Exact, "part",
            "Genuine, aftermarket or substitute — what the customer would be buying."),
        new("partNumberPrefix", "Part number starts with", MatchKind.Prefix, "part",
            "Part numbers beginning with any of these, ignoring case."),
        new("nameContains", "Name contains", MatchKind.Contains, "part",
            "Parts whose name contains any of these words."),

        // ----------------------------------------------------------- the caller
        new("clientCategory", "Pricing tier", MatchKind.Exact, "caller",
            "Accounts on any of these tiers."),
        new("client", "Specific customer", MatchKind.Exact, "caller",
            "These named accounts and nobody else."),
        new("clientRole", "Account type", MatchKind.Exact, "caller",
            "RETAIL, B2B, SALES or ADMIN."),
        new("salesManager", "Sales manager", MatchKind.Exact, "caller",
            "Customers looked after by any of these staff."),
        new("city", "City", MatchKind.Insensitive, "caller",
            "Accounts registered in any of these cities."),
        new("currency", "Currency", MatchKind.Exact, "caller",
            "Accounts quoted in any of these currencies."),
        new("priceList", "Purchase price list", MatchKind.Exact, "caller",
            "While any of these purchase price lists is the active one."),
    ];

    private static readonly Dictionary<string, Dimension> ByName =
        All.ToDictionary(d => d.Name, StringComparer.Ordinal);

    public static Dimension? Find(string name) =>
        ByName.TryGetValue(name, out var dimension) ? dimension : null;

    public static bool IsDimension(string name) => ByName.ContainsKey(name);

    /// <summary>Values one dimension may hold. A list longer than this is a mistake.</summary>
    public const int MaxConditionValues = 200;

    /// <summary>Longest a single value may be. Ids and city names are far shorter.</summary>
    public const int MaxConditionLength = 200;

    /// <summary>
    /// Whether one value accepts one subject.
    /// </summary>
    /// <remarks>
    /// Null subject never matches: a condition is a positive statement, and a
    /// request that cannot say what its city is has not said it is Cairo. The
    /// rule simply does not apply, and the next one down does.
    ///
    /// The comparisons are ordinal rather than culture-aware, to match the
    /// TypeScript original: <c>toUpperCase()</c> there is not the invariant
    /// culture's idea of upper case, and the two ports have to agree on the
    /// same four hundred generated cases.
    /// </remarks>
    public static bool ValueMatches(MatchKind kind, string value, string? subject)
    {
        if (subject is null) return false;

        return kind switch
        {
            MatchKind.Exact => string.Equals(subject, value, StringComparison.Ordinal),
            MatchKind.Insensitive => string.Equals(subject, value, StringComparison.OrdinalIgnoreCase),
            MatchKind.Prefix => subject.StartsWith(value, StringComparison.OrdinalIgnoreCase),
            MatchKind.Contains => subject.Contains(value, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    /// <summary>
    /// How specific a rule is: one point per dimension it narrows on.
    /// </summary>
    /// <remarks>
    /// The whole reason it is a function rather than a count of columns. A rule
    /// naming three suppliers narrows on ONE dimension — "the supplier is among
    /// these" — and must rank exactly where a rule naming a single supplier
    /// ranks. Counting conditions instead would make a longer list win, which
    /// is backwards: a list of three is a <i>less</i> specific statement than a
    /// list of one, not a more specific one.
    ///
    /// The purchase-price range counts as one dimension of its own. It is not a
    /// condition — a range is one bound at each end rather than a set to be
    /// among — but it is still a way the rule is narrowed.
    ///
    /// Recomputed on every save and stored on the row, so the engine can order
    /// by it and so the number a rule was ranked by is readable rather than
    /// inferred.
    /// </remarks>
    public static int SpecificityOf(IEnumerable<RuleCondition> conditions, bool hasPriceRange) =>
        conditions.Select(c => c.Dimension).Distinct(StringComparer.Ordinal).Count()
        + (hasPriceRange ? 1 : 0);
}

/// <summary>One acceptable answer on one dimension.</summary>
/// <param name="Negated">
/// Turns the condition inside out: the subject must NOT be this value.
/// Grouped with the other conditions on its dimension that point the same way,
/// so "any brand except BMW or Mini" is one statement with two exclusions
/// rather than two statements.
/// </param>
public record RuleCondition(string Dimension, string Value, bool Negated = false);
