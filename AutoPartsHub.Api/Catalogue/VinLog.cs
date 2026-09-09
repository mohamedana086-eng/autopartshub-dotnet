using System.Text.RegularExpressions;
using AutoPartsHub.Api.Data;
using AutoPartsHub.Domain.Catalogue;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsHub.Api.Catalogue;

/// <summary>
/// Recording what VIN lookups ask for.
/// </summary>
/// <remarks>
/// One row per KIND of vehicle, never per vehicle.
///
/// A full VIN identifies one particular car, and through it usually one
/// particular person. Positions 12 to 17 are the serial that does that, and
/// nothing here ever keeps them: what is stored is positions 1-8, an asterisk
/// where the check digit was, and the model-year character. Ten characters.
///
/// That is not a compromise made for privacy's sake — it is the whole of what
/// the question needs. NHTSA's free decoder returns the same answer for the
/// pattern as for all seventeen characters, measured rather than assumed:
///
///     full    1HGCM82633A004352  ->  HONDA / Accord / 2003 / 3.0L / Gasoline
///     pattern 1HGCM826*3         ->  HONDA / Accord / 2003 / 3.0L / Gasoline
///
/// NOTHING HERE MAY COST A CUSTOMER THEIR ANSWER
/// ---------------------------------------------
/// Every failure is swallowed. A measurement being taken is worth strictly
/// less than the thing being measured working: if the write fails the customer
/// still gets their candidates, and the row is simply not there. A month of
/// data with a gap in it still answers the question; a lookup that 500s
/// because a counter could not be incremented does not.
///
/// The decoder is not called from here. That is a separate batch job in the
/// other repository, and an external service must never sit between a customer
/// and a page.
/// </remarks>
public static partial class VinLog
{
    [GeneratedRegex(@"^[A-HJ-NPR-Z0-9]$")]
    private static partial Regex VinChar();

    /// <summary>The ten-character pattern for a VIN, or null if it is not one.</summary>
    public static (string Pattern, string Wmi)? Pattern(string raw)
    {
        var vin = raw.Trim().ToUpperInvariant().Replace(" ", "").Replace("-", "");
        if (vin.Length != 17) return null;

        // Positions 1-8 and 10, one character at a time. A whole-string regex
        // would also accept the characters this deliberately discards.
        var head = vin[..8];
        var yearChar = vin[9];

        foreach (var c in head + yearChar)
        {
            if (!VinChar().IsMatch(c.ToString())) return null;
        }

        return ($"{head}*{yearChar}", vin[..3]);
    }

    public static async Task RecordAsync(
        AutoPartsContext db,
        string vin,
        int? modelYear,
        string? makeName,
        int candidateCount,
        CancellationToken ct = default)
    {
        try
        {
            if (Pattern(vin) is not { } p) return;

            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "VinLookup" ("id", "pattern", "wmi", "modelYear", "makeName", "candidateCount")
                VALUES ({Ids.New()}, {p.Pattern}, {p.Wmi}, {modelYear}, {makeName}, {candidateCount})
                ON CONFLICT ("pattern") DO UPDATE
                  SET "lookups" = "VinLookup"."lookups" + 1,
                      "lastSeenAt" = now(),
                      -- Refreshed, because the catalogue grows: the same
                      -- pattern asked again next month may match a make we did
                      -- not carry before. The measurement is of what we can
                      -- answer NOW.
                      "makeName" = EXCLUDED."makeName",
                      "candidateCount" = EXCLUDED."candidateCount"
                """, ct);
        }
        catch
        {
            // Deliberately silent. See the note above: this is a counter, and
            // a counter is not worth an error page.
        }
    }
}
