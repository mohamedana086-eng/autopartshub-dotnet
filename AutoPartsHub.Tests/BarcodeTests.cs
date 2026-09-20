using AutoPartsHub.Api.Catalogue;
using AutoPartsHub.Domain.Catalogue;

namespace AutoPartsHub.Tests;

/// <summary>
/// Barcodes.
/// </summary>
/// <remarks>
/// The check digit is the whole reason the module exists: a barcode is the key
/// a warehouse scans, and a wrong one either finds nothing or finds the wrong
/// part and ships it. The codes used below are real published GTINs, chosen
/// because a hand-invented one that happens to fail its own checksum would
/// make these tests pass for the wrong reason.
///
/// Mirrors test/barcode.test.ts in the other repository.
/// </remarks>
public class BarcodeTests
{
    private const string Ean13 = "4006381333931";
    private const string Ean8 = "96385074";
    private const string Upca = "036000291452";
    private const string Itf14 = "10614141000415";

    public static TheoryData<string> EveryLength() => new() { Ean13, Ean8, Upca, Itf14 };

    [Theory]
    [InlineData("4006 381 333931", "4006381333931")]
    [InlineData("40-06381-333931", "4006381333931")]
    [InlineData("ab12cd", "AB12CD")]
    public void StripsTheSeparatorsAPrintedCodeCarries(string raw, string expected)
    {
        Assert.Equal(expected, Barcodes.Normalise(raw));
    }

    [Fact]
    public void LeavesLeadingZerosAlone()
    {
        // The reason this is text and not a number. 036000291452 as an integer
        // is a different barcode, and one that scans as nothing.
        Assert.Equal("036000291452", Barcodes.Normalise("036000291452"));
    }

    [Theory]
    [InlineData(Ean8, "ean8")]
    [InlineData(Upca, "upca")]
    [InlineData(Ean13, "ean13")]
    [InlineData(Itf14, "itf14")]
    [InlineData("ABC123456", "other")]
    [InlineData("123456789", "other")]
    [InlineData("4006 381 333931", "ean13")]
    public void ReadsTheKindOffTheLength(string code, string expected)
    {
        Assert.Equal(expected, Barcodes.Kind(code));
    }

    [Theory]
    [MemberData(nameof(EveryLength))]
    public void ComputesThePublishedDigitForEveryGtinLength(string code)
    {
        // One algorithm for all four, which is the point of GTIN — the weights
        // are decided by distance from the check digit, not by the length. An
        // implementation written per length gets EAN-8 backwards, and this is
        // where that shows.
        Assert.Equal(code[^1] - '0', Barcodes.CheckDigit(code[..^1]));
        Assert.True(Barcodes.HasValidCheckDigit(code));
    }

    [Fact]
    public void CatchesAnySingleMistypedDigit()
    {
        // The property worth having. Every one-digit error in a real code has
        // to be caught, in every position.
        foreach (var code in new[] { Ean13, Ean8, Upca, Itf14 })
        {
            for (var i = 0; i < code.Length; i++)
            {
                foreach (var d in "0123456789")
                {
                    if (d == code[i]) continue;
                    var typo = code[..i] + d + code[(i + 1)..];
                    Assert.False(Barcodes.HasValidCheckDigit(typo), $"{typo} should be refused");
                }
            }
        }
    }

    [Fact]
    public void CatchesTransposedNeighboursExceptThePairItCannot()
    {
        // A mod-10 checksum with alternating weights misses a transposition
        // whose two digits differ by 5 — the weights cancel. Pinned rather
        // than papered over: it is a property of GTIN, not of this code, and a
        // reader who knows the limit will not trust the check for more than it
        // gives.
        for (var i = 0; i < Ean13.Length - 1; i++)
        {
            var a = Ean13[i] - '0';
            var b = Ean13[i + 1] - '0';
            if (a == b) continue;

            var swapped = Ean13[..i] + Ean13[i + 1] + Ean13[i] + Ean13[(i + 2)..];
            if (Barcodes.HasValidCheckDigit(swapped))
            {
                Assert.Equal(5, Math.Abs(a - b));
            }
        }
    }

    [Theory]
    [InlineData("ABC123456")]
    [InlineData("123456789")]
    public void DoesNotJudgeACodeWithNoChecksumToJudge(string code)
    {
        // Refusing a Code 128 label for not being a GTIN would be refusing a
        // barcode for not being the kind this understands.
        Assert.True(Barcodes.HasValidCheckDigit(code));
    }

    [Theory]
    [InlineData(Ean13)]
    [InlineData("4006 381 333931")]
    public void AcceptsARealCode(string code) => Assert.Null(Barcodes.Refusal(code));

    [Theory]
    [InlineData("", "blank")]
    [InlineData("   ", "blank")]
    [InlineData("12345", "too short")]
    [InlineData("4006/381/333931", "letters and numbers")]
    public void RefusesWhatIsNotABarcode(string raw, string because)
    {
        Assert.Contains(because, Barcodes.Refusal(raw));
    }

    [Fact]
    public void RefusesSomethingTooLongToBeACode()
    {
        Assert.Contains("too long", Barcodes.Refusal(new string('1', 33)));
    }

    [Fact]
    public void NamesTheCheckDigitRatherThanCallingTheCodeInvalid()
    {
        var message = Barcodes.Refusal("4006381333932");
        // Saying which part failed tells the reader to look at what they typed
        // rather than to doubt the part.
        Assert.Contains("check digit", message);
        Assert.Contains("4006381333932", message);
    }
}
