namespace AutoPartsHub.Api.Vehicles;

/// <summary>
/// VIN parsing — structure only.
/// </summary>
/// <remarks>
/// A VIN identifies the manufacturer, the model year and the plant, and every
/// bit of that is decodable offline from the published standard. What it does
/// NOT carry, in any readable form, is the model and engine: that mapping is
/// held per-manufacturer in licensed databases (TecDoc and the like). So this
/// gets as far as make and year honestly, and the caller narrows the rest by
/// picking from the models we list for that make.
///
/// ISO 3779 for the layout, and the year table every issuer follows.
/// </remarks>
public static class Vin
{
    /// <summary>I, O and Q are never used, so they cannot be confused with 1 and 0.</summary>
    private const string Alphabet = "ABCDEFGHJKLMNPRSTUVWXYZ0123456789";

    /// <summary>Position 10, cycling every 30 years from 1980.</summary>
    private static readonly char[] YearCodes =
    [
        'A', 'B', 'C', 'D', 'E', 'F', 'G', 'H', 'J', 'K',
        'L', 'M', 'N', 'P', 'R', 'S', 'T', 'V', 'W', 'X',
        'Y', '1', '2', '3', '4', '5', '6', '7', '8', '9',
    ];

    /// <summary>Transliteration used by the North American check digit.</summary>
    private static readonly Dictionary<char, int> Transliteration = new()
    {
        ['A'] = 1, ['B'] = 2, ['C'] = 3, ['D'] = 4, ['E'] = 5, ['F'] = 6, ['G'] = 7, ['H'] = 8,
        ['J'] = 1, ['K'] = 2, ['L'] = 3, ['M'] = 4, ['N'] = 5, ['P'] = 7, ['R'] = 9,
        ['S'] = 2, ['T'] = 3, ['U'] = 4, ['V'] = 5, ['W'] = 6, ['X'] = 7, ['Y'] = 8, ['Z'] = 9,
    };

    private static readonly int[] Weights = [8, 7, 6, 5, 4, 3, 2, 10, 0, 9, 8, 7, 6, 5, 4, 3, 2];

    public static VinResult Parse(string raw)
    {
        var vin = new string(raw.Trim().ToUpperInvariant().Where(c => c is not (' ' or '-')).ToArray());

        if (vin.Length != 17)
        {
            return VinResult.Fail($"A VIN is 17 characters; that one is {vin.Length}.");
        }
        if (vin.Any(c => !Alphabet.Contains(c)))
        {
            return VinResult.Fail("A VIN never contains the letters I, O or Q. Check for a typo.");
        }

        var currentYear = DateTime.UtcNow.Year;
        var index = Array.IndexOf(YearCodes, vin[9]);

        int? modelYear = null;
        if (index >= 0)
        {
            // Walk the cycles and keep the most recent one that is not in the future.
            foreach (var year in new[] { 1980 + index, 1980 + index + 30, 1980 + index + 60 })
            {
                if (year <= currentYear + 1) modelYear = year;
            }
        }

        return VinResult.Ok(new VinReading(
            Vin: vin,
            Wmi: vin[..3],
            ModelYear: modelYear,
            ModelYearIsEstimate: true,
            CheckDigitValid: VerifyCheckDigit(vin)));
    }

    private static bool? VerifyCheckDigit(string vin)
    {
        var sum = 0;
        for (var i = 0; i < 17; i++)
        {
            var c = vin[i];
            int value;
            if (char.IsAsciiDigit(c)) value = c - '0';
            else if (Transliteration.TryGetValue(c, out var t)) value = t;
            else return null;

            sum += value * Weights[i];
        }

        var remainder = sum % 11;
        var expected = remainder == 10 ? "X" : remainder.ToString();
        return vin[8].ToString() == expected;
    }
}

/// <param name="Wmi">Characters 1-3: who built it.</param>
/// <param name="ModelYear">Best estimate from position 10 — see ModelYearIsEstimate.</param>
/// <param name="ModelYearIsEstimate">
/// The year character repeats every 30 years, so a VIN alone cannot say which
/// cycle it belongs to. The most recent plausible year is returned.
/// </param>
/// <param name="CheckDigitValid">
/// The check digit is mandatory on vehicles built for North America and merely
/// conventional elsewhere, so a mismatch is reported rather than treated as a
/// rejection.
/// </param>
public record VinReading(
    string Vin,
    string Wmi,
    int? ModelYear,
    bool ModelYearIsEstimate,
    bool? CheckDigitValid);

public record VinResult(bool Success, VinReading? Value, string? Error)
{
    public static VinResult Ok(VinReading value) => new(true, value, null);
    public static VinResult Fail(string error) => new(false, null, error);
}
