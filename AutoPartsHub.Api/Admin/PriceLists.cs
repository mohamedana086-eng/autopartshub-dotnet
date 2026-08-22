using System.Text.Json;
using AutoPartsHub.Api.Catalogue;

namespace AutoPartsHub.Api.Admin;

/// <summary>One row as the uploader sends it, before it is matched to anything.</summary>
/// <param name="Price">In the base currency — what gets stored and priced from.</param>
/// <param name="SourcePrice">What the file said, kept only when a conversion actually happened.</param>
public record PricedRow(
    string ProductId, string PartNumber, double Price, double? SourcePrice, string? SourceCurrency);

public record RejectedRow(string PartNumber, string Reason);

public record ReadResult(List<PricedRow> Rows, List<RejectedRow> Rejected);

/// <summary>Enough of a Currency row to convert with.</summary>
/// <param name="Rate">Units of this currency per one unit of the base. 1 on the base row.</param>
public record ConversionRate(string Code, double Rate);

public record ListDetails(string Name, string? Description, string? SourceName);

/// <summary>
/// Reading an uploaded purchase-price list.
/// </summary>
/// <remarks>
/// The file is parsed in the browser — the storefront already does that for
/// the bulk lookup — and arrives here as rows. What is left is the part that
/// must not be got wrong: matching each row to a part, and converting what the
/// supplier quoted into the currency the markup engine multiplies up.
/// </remarks>
public static class PriceLists
{
    private const int MaxRows = 50_000;

    /// <summary>
    /// Converts a quoted price into the base currency.
    /// </summary>
    /// <remarks>
    /// <paramref name="rate"/> is defined as units of that currency per one
    /// unit of the base — the definition the Currency model states and the
    /// markup engine relies on — so going the other way DIVIDES. The engine
    /// multiplies because it converts base into the customer's currency; this
    /// converts a supplier's currency into the base, which is the same rate
    /// read backwards. Getting it upside down would not throw, it would just
    /// quietly misprice a whole catalogue, which is why it is one named
    /// function rather than an expression at a call site.
    /// </remarks>
    public static double ToBaseCurrency(double price, double rate) => price / rate;

    private static double Round(double value) =>
        Math.Round(value * 100, MidpointRounding.AwayFromZero) / 100;

    /// <summary>
    /// Matches rows to parts and converts them.
    /// </summary>
    /// <remarks>
    /// Part numbers are compared on their normalised form, the same way search
    /// and the bulk lookup compare them, so a list that writes
    /// <c>0 986 424 815</c> still lands on the part stored as
    /// <c>0986424815</c>.
    ///
    /// Nothing is rejected silently: every row that finds no part, names a
    /// currency nobody maintains, or carries a price that is not a number
    /// comes back in <c>Rejected</c> with a reason, so the admin sees what a
    /// file failed to cover rather than discovering it as a wrong price later.
    /// </remarks>
    public static Validated<ReadResult> ReadPriceRows(
        JsonElement? raw,
        Dictionary<string, string> productIdByNormalisedPartNumber,
        Dictionary<string, ConversionRate> ratesByCode)
    {
        if (raw is not { ValueKind: JsonValueKind.Array } list)
        {
            return Validators.Fail<ReadResult>("Expected a list of rows.");
        }

        var length = list.GetArrayLength();
        if (length == 0) return Validators.Fail<ReadResult>("That file has no rows in it.");
        if (length > MaxRows)
        {
            return Validators.Fail<ReadResult>(
                $"A price list can carry at most {MaxRows.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} rows.");
        }

        var rows = new List<PricedRow>();
        var rejected = new List<RejectedRow>();

        /* Last row wins on a repeat, but the earlier one is reported, not dropped. */
        var seen = new Dictionary<string, int>();

        foreach (var entry in list.EnumerateArray())
        {
            // `typeof null === 'object'` on the other side, but the guard there
            // is `!entry || typeof entry !== 'object'`, so null fails it too.
            // An array passes it there and would here as well; a spreadsheet
            // exporter emitting arrays of cells has no partNumber key either
            // way, so it lands on the same blank-line branch below.
            if (entry.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            {
                return Validators.Fail<ReadResult>("Every row must be an object.");
            }

            var partNumber = JsonValues.AsString(JsonValues.Get(entry, "partNumber")).Trim();
            if (partNumber.Length == 0) continue; // a blank line in a spreadsheet is not an error

            // Number(row.price) with no fallback: an absent price is NaN, not
            // zero, and a free part is not what a missing column means.
            var priceField = JsonValues.Get(entry, "price");
            var price = priceField is null ? null : JsonValues.AsNumber(priceField);
            if (price is not { } quoted || double.IsNaN(quoted) || double.IsInfinity(quoted) || quoted < 0)
            {
                rejected.Add(new RejectedRow(partNumber, "Price is not a number of zero or more."));
                continue;
            }

            var normalised = PartNumbers.Normalise(partNumber);
            if (!productIdByNormalisedPartNumber.TryGetValue(normalised, out var productId))
            {
                rejected.Add(new RejectedRow(partNumber, "No part in the catalogue matches that number."));
                continue;
            }

            var code = JsonValues.AsString(JsonValues.Get(entry, "currency")).Trim().ToUpperInvariant();
            var basePrice = quoted;
            double? sourcePrice = null;
            string? sourceCurrency = null;

            if (code.Length > 0)
            {
                if (!ratesByCode.TryGetValue(code, out var currency))
                {
                    rejected.Add(new RejectedRow(partNumber, $"No currency called {code} is set up."));
                    continue;
                }
                if (!(currency.Rate > 0))
                {
                    rejected.Add(new RejectedRow(partNumber, $"{code} has no usable rate."));
                    continue;
                }
                // Only recorded as converted when it actually was: a list
                // already in the base currency should not carry a "source"
                // that says the same number.
                if (currency.Rate != 1)
                {
                    basePrice = ToBaseCurrency(quoted, currency.Rate);
                    sourcePrice = quoted;
                    sourceCurrency = code;
                }
            }

            var priced = new PricedRow(productId, partNumber, Round(basePrice), sourcePrice, sourceCurrency);

            if (seen.TryGetValue(productId, out var previous))
            {
                rejected.Add(new RejectedRow(
                    partNumber, "That part appears more than once; the last price in the file was used."));
                rows[previous] = priced;
                continue;
            }

            seen[productId] = rows.Count;
            rows.Add(priced);
        }

        if (rows.Count == 0)
        {
            // Nothing survived. Say which reason accounted for most of it
            // rather than assuming the part numbers were wrong: a file that
            // matched every part and named a currency nobody has set up fails
            // just as completely, and sending the admin to check part numbers
            // would be sending them to the wrong place.
            return Validators.Fail<ReadResult>($"Not one row could be used. {CommonestReason(rejected)}");
        }

        return Validators.Ok(new ReadResult(rows, rejected));
    }

    /// <summary>The reason that explains most of a wholly-rejected file, for its message.</summary>
    private static string CommonestReason(List<RejectedRow> rejected)
    {
        if (rejected.Count == 0) return "The file had no usable rows.";

        // Grouped in first-seen order and sorted with a stable sort, so a tie
        // between two reasons resolves to whichever the file hit first — twice
        // running, and the same way the other API resolves it.
        var counts = rejected.GroupBy(r => r.Reason)
            .Select(g => (Reason: g.Key, Count: g.Count()))
            .OrderByDescending(x => x.Count)
            .ToList();

        var (reason, count) = counts[0];

        return count == rejected.Count
            ? reason
            : $"Most often: {reason} ({count} of {rejected.Count}).";
    }

    public static Validated<ListDetails> ReadListDetails(JsonElement body)
    {
        var name = JsonValues.AsString(JsonValues.Get(body, "name")).Trim();
        if (name.Length == 0) return Validators.Fail<ListDetails>("Give the list a name.");
        if (name.Length > 120) return Validators.Fail<ListDetails>("Keep the name under 120 characters.");

        var description = JsonValues.AsString(JsonValues.Get(body, "description")).Trim();
        var sourceName = JsonValues.AsString(JsonValues.Get(body, "sourceName")).Trim();

        return Validators.Ok(new ListDetails(
            name,
            description.Length > 0 ? description : null,
            sourceName.Length > 0 ? sourceName : null));
    }
}
