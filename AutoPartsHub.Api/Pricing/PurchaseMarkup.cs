namespace AutoPartsHub.Api.Pricing;

/// <summary>Which rung of the purchase-side chain answered.</summary>
public enum MarkupRung
{
    Line,
    List,
    Supplier,
}

/// <summary>One rung's answer, and which rung gave it.</summary>
/// <param name="Label">
/// Named in <c>AppliedRule</c> — a quote says which rung decided it. "Why is
/// this part €40?" has to be answerable, and "the March file says so" is an
/// answer somebody can act on where a bare percentage is not.
/// </param>
public record PurchaseMarkup(string Label, double Percent, MarkupRung Rung);

/// <summary>What the three rungs hold for one part.</summary>
/// <param name="ListPrice">
/// The active list's price for this part, or null where it holds none. The
/// same field <c>PurchasePrice</c> reads, so the margin and the cost cannot
/// disagree about whether the file covers this part.
/// </param>
/// <param name="SupplierMarkupPercent">
/// The margin of the supplier we would actually buy this from — the one
/// <c>SupplierIdFor</c> names, which is whose offer won rather than whichever
/// supplier the part was first sourced from.
/// </param>
public record PurchaseMarkupSource(
    double? ListPrice,
    double? RowMarkupPercent,
    double? ListMarkupPercent,
    string? ListName,
    double? SupplierMarkupPercent,
    string? SupplierName);

/// <summary>
/// The margin stated where the part is BOUGHT.
/// </summary>
/// <remarks>
/// Every markup the engine had until now describes the selling side: a rule
/// that matched, the goods category, the client category's default. None of
/// them can say the thing a buyer actually says while working through a
/// supplier's file — "their parts earn 18%, but everything on the March list
/// earns 22%, except this line at 35%".
///
/// Three rungs, narrowest first: the LINE (this part, on the file in force),
/// the LIST (everything that came in on that file), then the SUPPLIER
/// (everything we buy from them).
///
/// Its own class, and pure, for the reason <c>Paging</c> is: it is the same
/// decision in two languages, and a decision that lives in an endpoint cannot
/// be compared across ports.
/// </remarks>
public static class PurchaseMarkups
{
    /// <summary>
    /// The ceiling the three columns are checked against, in both ports and in
    /// the database.
    /// </summary>
    /// <remarks>
    /// Eleven times cost. It is not there to stop an ambitious margin — it is
    /// there because all three of these boxes sit next to a price box on their
    /// screen, and 1450 typed into the wrong one is a mistake nobody would
    /// spot in a list of percentages.
    /// </remarks>
    public const double MaxMarkupPercent = 1000;

    /// <summary>Which rung answers, or null when none of them does.</summary>
    /// <remarks>
    /// NULL IS NOT ZERO, and the whole chain turns on that. Null means "no
    /// opinion, ask the next rung"; zero means "sell it at cost", which is a
    /// real instruction and stops the search. A <c>?? 0</c> anywhere in here
    /// would silently turn every deliberate zero into a fall-through to the
    /// supplier's margin.
    ///
    /// The line and the list only answer while the file in force covers the
    /// part. A margin from a file that is not setting the cost would be half of
    /// one deal and half of another — the customer paying the March file's
    /// margin on a price that came from a standing offer nobody looked at.
    /// </remarks>
    public static PurchaseMarkup? Of(PurchaseMarkupSource src)
    {
        var onTheList = src.ListPrice is not null;

        if (onTheList && src.RowMarkupPercent is double row)
        {
            return new PurchaseMarkup(
                $"{src.ListName ?? "Price list"} line markup", row, MarkupRung.Line);
        }

        if (onTheList && src.ListMarkupPercent is double list)
        {
            return new PurchaseMarkup(
                $"{src.ListName ?? "Price list"} list markup", list, MarkupRung.List);
        }

        if (src.SupplierMarkupPercent is double supplier)
        {
            return new PurchaseMarkup(
                $"{src.SupplierName ?? "Supplier"} markup", supplier, MarkupRung.Supplier);
        }

        return null;
    }

    /// <summary>Reads one of the three from a request body.</summary>
    /// <remarks>
    /// Absent, null and empty all mean "no opinion", exactly as they do in the
    /// other API — the form sends an empty box for a margin nobody has agreed,
    /// and JSON null is how a screen takes one away again.
    /// </remarks>
    public static (bool Ok, double? Value, string? Error) Read(
        System.Text.Json.JsonElement? field, string name)
    {
        if (field is null) return (true, null, null);
        if (field.Value.ValueKind == System.Text.Json.JsonValueKind.Null) return (true, null, null);
        if (JsonValues.AsString(field).Trim().Length == 0) return (true, null, null);

        var n = JsonValues.AsNumber(field);
        if (n is null) return (false, null, $"{name} must be a number.");

        return Read(n.Value, name);
    }

    /// <summary>Reads one of the three from a form.</summary>
    /// <remarks>
    /// Empty and null both mean "no opinion" and come back as null, because
    /// that is how a rung is cleared — a screen has to be able to take the
    /// number away again, and an editor that could only ever set one would make
    /// the first margin somebody typed permanent.
    ///
    /// The refusals are worded exactly as the other API words them. Two APIs
    /// refusing the same thing in different words are two products.
    /// </remarks>
    public static (bool Ok, double? Value, string? Error) Read(object? value, string field)
    {
        if (value is null) return (true, null, null);
        if (value is string s && s.Length == 0) return (true, null, null);

        double n;
        if (value is double d) n = d;
        else if (value is int i) n = i;
        else if (value is long l) n = l;
        else if (value is decimal m) n = (double)m;
        else if (!double.TryParse(
                     value.ToString(),
                     System.Globalization.NumberStyles.Float,
                     System.Globalization.CultureInfo.InvariantCulture,
                     out n))
        {
            return (false, null, $"{field} must be a number.");
        }

        if (double.IsNaN(n) || double.IsInfinity(n)) return (false, null, $"{field} must be a number.");
        if (n < 0) return (false, null, $"{field} cannot be negative.");
        if (n > MaxMarkupPercent)
        {
            // Says the number back. The mistake this catches is a price typed
            // into a percent box, and seeing "1450%" written out is what makes
            // that obvious.
            // Formatted the way JavaScript writes a number into a string, so
            // the two APIs refuse an over-large margin with the same sentence.
            var written = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return (false, null,
                $"{field} of {written}% is above the {MaxMarkupPercent:0}% limit. " +
                "Is that a price rather than a percentage?");
        }

        // Two places, like every other percentage here. A margin stored as
        // 18.333333333 would come back out of the database as a number nobody
        // typed.
        return (true, Math.Round(n * 100, MidpointRounding.AwayFromZero) / 100, null);
    }
}
