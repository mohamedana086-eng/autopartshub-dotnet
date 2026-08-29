using System.Text.Json;

namespace AutoPartsHub.Api.Admin;

/// <summary>One supplier's terms for one part, once read off a request.</summary>
/// <param name="StockDays">Their lead time, or null to fall back to the supplier's.</param>
/// <param name="Active">Whether we would buy it from them today.</param>
public record OfferInput(
    string SupplierId, double PurchasePrice, int? StockDays,
    string? SupplierPartNumber, bool Active);

/// <summary>
/// Reading a part's offers off a request.
/// </summary>
/// <remarks>
/// No database in it, so both APIs read the same rules and both can be tested
/// without one. They have to agree: both write to the same table, and a
/// purchase price is the number the whole markup engine multiplies up.
/// </remarks>
public static class OfferInputs
{
    /// <summary>Long enough for any real part number, short enough that nothing else fits.</summary>
    public const int MaxSupplierPartNumber = 60;

    /// <summary>
    /// A price nobody would type on purpose.
    /// </summary>
    /// <remarks>
    /// Not a business rule about what parts cost — a rule about what a keyboard
    /// produces. A purchase price of ten million is a decimal point in the
    /// wrong place or a part number pasted into a price box, and both are worth
    /// catching before they become the number everything else multiplies.
    /// </remarks>
    public const double MaxPurchasePrice = 1_000_000;

    /// <summary>
    /// Reads the whole set of offers for one part.
    /// </summary>
    /// <remarks>
    /// The set, not one at a time, because the editor shows every supplier for
    /// a part together and submitting them together is what stops a part being
    /// left half-repriced. The same shape as the stock editor.
    ///
    /// A supplier left out of the list no longer offers the part at all —
    /// their row goes, rather than being kept as an inactive offer.
    /// Withdrawing an offer while keeping it listed is what <c>active</c> is
    /// for, and the two mean different things: "we do not buy this from them"
    /// and "they do not sell it". Only the second is an absence.
    /// </remarks>
    public static Validated<List<OfferInput>> Read(JsonElement body)
    {
        if (JsonValues.Get(body, "offers") is not { ValueKind: JsonValueKind.Array } list)
        {
            return Validators.Fail<List<OfferInput>>("Expected a list of offers.");
        }

        var offers = new List<OfferInput>();
        var seen = new HashSet<string>();

        foreach (var entry in list.EnumerateArray())
        {
            if (entry.ValueKind is not JsonValueKind.Object)
            {
                return Validators.Fail<List<OfferInput>>("Every offer must be an object.");
            }

            var supplierId = JsonValues.AsString(JsonValues.Get(entry, "supplierId")).Trim();
            if (supplierId.Length == 0)
            {
                return Validators.Fail<List<OfferInput>>("Every offer needs a supplier.");
            }

            // One offer per supplier per part. Two would be two answers to
            // "what do they charge", and nothing could choose between them —
            // the database says so with a unique index, and this says it in a
            // sentence somebody can act on rather than as a constraint
            // violation.
            if (!seen.Add(supplierId))
            {
                return Validators.Fail<List<OfferInput>>(
                    "That supplier is listed twice. One offer each.");
            }

            var priceField = JsonValues.Get(entry, "purchasePrice");
            var price = priceField is null ? null : JsonValues.AsNumber(priceField);
            if (price is not { } purchasePrice || double.IsNaN(purchasePrice)
                || double.IsInfinity(purchasePrice) || purchasePrice < 0)
            {
                return Validators.Fail<List<OfferInput>>(
                    "A purchase price must be a number of zero or more.");
            }
            if (purchasePrice > MaxPurchasePrice)
            {
                return Validators.Fail<List<OfferInput>>(
                    $"A purchase price above {MaxPurchasePrice.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} is a mistake, not a price.");
            }

            int? stockDays = null;
            var daysField = JsonValues.Get(entry, "stockDays");
            if (daysField is { ValueKind: not JsonValueKind.Null } d
                && JsonValues.AsString(d).Trim().Length > 0)
            {
                var days = JsonValues.AsNumber(d);
                if (!JsonValues.IsWhole(days) || days is < 0 or > 365)
                {
                    return Validators.Fail<List<OfferInput>>(
                        "A lead time must be a whole number of days, 0 to 365.");
                }
                stockDays = (int)days!.Value;
            }

            var supplierPartNumber =
                JsonValues.AsString(JsonValues.Get(entry, "supplierPartNumber")).Trim();
            if (supplierPartNumber.Length > MaxSupplierPartNumber)
            {
                return Validators.Fail<List<OfferInput>>(
                    $"Keep a supplier's part number under {MaxSupplierPartNumber} characters.");
            }

            offers.Add(new OfferInput(
                supplierId,
                // Rounded to cents on the way in. A purchase price carried to
                // four decimal places would be multiplied by a markup and
                // rounded once at the end, which puts the rounding somewhere
                // nobody chose.
                Math.Round(purchasePrice * 100, MidpointRounding.AwayFromZero) / 100,
                stockDays,
                supplierPartNumber.Length > 0 ? supplierPartNumber : null,
                // Only an explicit false withdraws it. An offer submitted
                // without the field is one somebody is adding, and adding an
                // offer nobody may buy from would be a strange default.
                JsonValues.Get(entry, "active") is not { ValueKind: JsonValueKind.False }));
        }

        return Validators.Ok(offers);
    }
}
