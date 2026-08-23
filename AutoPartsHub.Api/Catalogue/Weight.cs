namespace AutoPartsHub.Api.Catalogue;

/// <summary>
/// What a part weighs, and what an order therefore weighs.
/// </summary>
/// <remarks>
/// GRAMS, AS A WHOLE NUMBER
/// ------------------------
/// A weight is a count, an order total is a sum of counts, and integers make
/// that sum exact — where kilograms as a double turn a fifty-line order into
/// fifty chances to be a gram out. Kilograms are what a person reads, so the
/// conversion happens where it is displayed and nowhere else.
///
/// UNKNOWN IS NOT ZERO
/// -------------------
/// A part nobody has weighed has null, and that is a different fact from a
/// part that weighs nothing. Summing with the unknowns counted as zero
/// produces a number that LOOKS like the weight of the order and is quietly
/// less than it — which is worse than no number at all, because a shipping
/// quote built on it is wrong in the direction that costs money.
///
/// So a total carries whether it is complete. The catalogue already draws this
/// distinction for stock — null for a part nobody counted, 0 for an empty
/// shelf — and this is the same distinction about a different column.
/// </remarks>
public static class Weight
{
    /// <summary>Two tonnes. Not a fact about parts — a guard against a misplaced decimal.</summary>
    public const int MaxGrams = 2_000_000;

    /// <summary>The weight of a basket or an order, and whether anything is missing.</summary>
    public static WeightTotal Sum(IEnumerable<WeighedLine> lines)
    {
        var grams = 0;
        var unweighed = 0;

        foreach (var line in lines)
        {
            if (line.WeightGrams is not { } w)
            {
                unweighed++;
                continue;
            }
            grams += w * line.Quantity;
        }

        return new WeightTotal(grams, unweighed == 0, unweighed);
    }

    /// <summary>
    /// A weight typed into the admin form, in kilograms, as grams.
    /// </summary>
    /// <remarks>
    /// Kilograms in because that is what a scale reads and what a catalogue
    /// quotes; grams out because that is what is stored. Blank is a real
    /// answer and means "nobody has weighed this", which is why it returns
    /// null rather than zero.
    /// </remarks>
    public static (int? Value, string? Error) ReadGrams(double? kilograms)
    {
        if (kilograms is not { } kg) return (null, null);
        if (double.IsNaN(kg) || double.IsInfinity(kg)) return (null, "The weight must be a number.");

        if (kg <= 0)
        {
            // Zero is not a lighter part, it is a part nobody weighed — and
            // that is what leaving the field blank says.
            return (null, "A weight of zero is not a weight. Leave it blank if it is unknown.");
        }

        // AwayFromZero to match JavaScript's Math.round, which is what the
        // other implementation uses — a half-gram has to land the same way on
        // both sides or the two catalogues disagree by a gram.
        var grams = (int)Math.Round(kg * 1000, MidpointRounding.AwayFromZero);
        if (grams > MaxGrams)
        {
            return (null, $"{Format(kg)} kg is heavier than this catalogue allows — check the decimal point.");
        }
        return (grams, null);
    }

    /// <summary>A number the way JavaScript writes it into a string.</summary>
    private static string Format(double value) =>
        value.ToString("0.############", System.Globalization.CultureInfo.InvariantCulture);
}

/// <param name="WeightGrams">Null where nobody has weighed the part.</param>
public record WeighedLine(int? WeightGrams, int Quantity);

/// <param name="Grams">The sum of what is known. Never null — it is a real subtotal.</param>
/// <param name="Complete">
/// False when any line's part has no weight on file. The caller must say so
/// rather than print <c>Grams</c> as a fact: it is a floor, and every shipping
/// figure derived from it inherits the error.
/// </param>
/// <param name="Unweighed">How many lines could not be counted.</param>
public record WeightTotal(int Grams, bool Complete, int Unweighed);
