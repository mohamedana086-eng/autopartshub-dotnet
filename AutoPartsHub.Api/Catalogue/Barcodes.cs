using System.Text.RegularExpressions;

namespace AutoPartsHub.Api.Catalogue;

/// <summary>
/// Barcodes, and telling a real one from a typo.
/// </summary>
/// <remarks>
/// A barcode is the key a warehouse scans. A wrong one is not a cosmetic
/// error: it either finds nothing, or finds the wrong part and ships it. The
/// last digit of every GTIN is a mod-10 checksum over the rest, so a single
/// mistyped digit is detectable where it is entered rather than where somebody
/// opens the wrong box.
///
/// It catches every single-digit error and most transpositions. It does not
/// catch a barcode that is valid and belongs to a different part; nothing
/// except scanning the box does.
/// </remarks>
public static partial class Barcodes
{
    [GeneratedRegex(@"[\s-]+")]
    private static partial Regex Separators();

    [GeneratedRegex(@"^[0-9A-Z]+$")]
    private static partial Regex Allowed();

    [GeneratedRegex(@"^\d+$")]
    private static partial Regex Digits();

    /// <summary>
    /// The same code as everyone else would write it.
    /// </summary>
    /// <remarks>
    /// Printed barcodes carry spaces and hyphens for legibility and nobody
    /// keys them back the same way — the same reason part numbers are
    /// normalised before they are compared. Uppercased because Code 128 can
    /// carry letters.
    /// </remarks>
    public static string Normalise(string raw) =>
        Separators().Replace(raw, "").ToUpperInvariant();

    /// <summary>What kind of code this is, by its shape. `other` is not a rejection.</summary>
    public static string Kind(string code)
    {
        var c = Normalise(code);
        if (!Digits().IsMatch(c)) return "other";
        return c.Length switch
        {
            8 => "ean8",
            12 => "upca",
            13 => "ean13",
            14 => "itf14",
            _ => "other",
        };
    }

    /// <summary>
    /// The GTIN mod-10 check digit over everything but the last position.
    /// </summary>
    /// <remarks>
    /// One algorithm for all four lengths, which is the point of GTIN: weights
    /// alternate 3 and 1 from the right, so they are decided by distance from
    /// the check digit rather than by the length of the code. Writing it per
    /// length is where implementations get EAN-8 backwards.
    /// </remarks>
    public static int? CheckDigit(string digitsWithoutCheck)
    {
        if (!Digits().IsMatch(digitsWithoutCheck)) return null;

        var sum = 0;
        var weight = 3;
        for (var i = digitsWithoutCheck.Length - 1; i >= 0; i--, weight = weight == 3 ? 1 : 3)
        {
            sum += (digitsWithoutCheck[i] - '0') * weight;
        }
        return (10 - sum % 10) % 10;
    }

    /// <summary>
    /// Whether a numeric barcode's last digit agrees with the rest of it.
    /// </summary>
    /// <remarks>
    /// True for anything that is not a fixed-length GTIN — a Code 128 label or
    /// an internal code has no checksum to disagree with, and refusing it
    /// would be refusing a barcode for not being the kind this understands.
    /// </remarks>
    public static bool HasValidCheckDigit(string code)
    {
        var c = Normalise(code);
        if (Kind(c) == "other") return true;

        var expected = CheckDigit(c[..^1]);
        return expected is not null && expected == c[^1] - '0';
    }

    /// <summary>What is wrong with a barcode, or null when nothing is.</summary>
    public static string? Refusal(string raw)
    {
        var code = Normalise(raw);

        if (code.Length == 0) return "A barcode cannot be blank.";
        if (code.Length < 6) return "That is too short to be a barcode.";
        if (code.Length > 32) return "That is too long to be a barcode.";
        if (!Allowed().IsMatch(code)) return "A barcode may use letters and numbers only.";
        if (!HasValidCheckDigit(code))
        {
            // Named rather than "invalid barcode": the check digit is the
            // thing that failed, and saying so tells the reader to look at
            // what they typed rather than to doubt the part.
            return $"The check digit on {code} does not match the rest of it — one of the digits is wrong.";
        }
        return null;
    }
}
