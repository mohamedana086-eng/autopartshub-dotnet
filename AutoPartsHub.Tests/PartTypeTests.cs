using System.Text.Json;
using AutoPartsHub.Api.Admin;

namespace AutoPartsHub.Tests;

/// <summary>
/// What a part is, as opposed to which number found it.
/// </summary>
/// <remarks>
/// The distinction is the whole feature and it is easy to lose: searching an
/// OE number returns aftermarket parts, so a filter answering "which number
/// matched" and one answering "what am I buying" give opposite answers about
/// the same rows. Nothing in a compiler notices if the two are folded together,
/// which is why the vocabulary is pinned here — and in the other API's suite,
/// since the two have to agree.
/// </remarks>
public class PartTypeTests
{
    private static JsonElement Product(object? over = null)
    {
        var d = new Dictionary<string, object?>
        {
            ["partNumber"] = "BP-1",
            ["name"] = "Brake pad",
            ["manufacturerId"] = "m1",
            ["vehicleSystemId"] = "v1",
            ["basePrice"] = 10,
        };
        if (over is not null)
        {
            foreach (var p in over.GetType().GetProperties()) d[p.Name] = p.GetValue(over);
        }
        return JsonDocument.Parse(JsonSerializer.Serialize(d)).RootElement;
    }

    private static ProductInput Ok(JsonElement body)
    {
        var r = Validators.ReadProduct(body);
        Assert.True(r.Ok, $"expected success, got: {r.Error}");
        return r.Value!;
    }

    private static string Err(JsonElement body)
    {
        var r = Validators.ReadProduct(body);
        Assert.False(r.Ok, "expected a refusal, got success");
        return r.Error!;
    }

    [Fact]
    public void IsExactlyTheseInMakerFirstOrder()
    {
        // The order is not cosmetic: it is what the filter chips and the admin
        // select are listed in, and the search endpoint echoes selections back
        // in it.
        Assert.Equal(new[] { "oem", "aftermarket", "substitute" }, PartTypes.All);
    }

    [Theory]
    [InlineData("oem")]
    [InlineData("aftermarket")]
    [InlineData("substitute")]
    public void AcceptsEachOfThem(string type)
    {
        Assert.Equal(type, Ok(Product(new { partType = type })).PartType);
    }

    [Fact]
    public void DefaultsToAftermarketTheSameAsTheColumn()
    {
        // The understating direction. Calling an independent brand's part
        // genuine is a claim made to a customer; calling a genuine part
        // aftermarket is only a missing badge.
        Assert.Equal("aftermarket", Ok(Product()).PartType);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TreatsBlankAsAbsentRatherThanInvalid(string value)
    {
        // A form that submits an untouched select sends an empty string, and a
        // client written before this field existed sends nothing at all.
        // Neither should fail.
        Assert.Equal("aftermarket", Ok(Product(new { partType = value })).PartType);
    }

    [Theory]
    [InlineData("genuine")]
    [InlineData("OEM")]
    [InlineData("nonsense")]
    public void RefusesAKindThatIsNotOneOfTheThree(string value)
    {
        Assert.Contains("oem, aftermarket, substitute", Err(Product(new { partType = value })));
    }

    [Fact]
    public void RefusesTheVocabularyOfTheOtherFilter()
    {
        // `part-number` is a matchIn value, not a part type. Sending one where
        // the other belongs is exactly the confusion this column exists to
        // prevent, so it is refused rather than quietly defaulted.
        Assert.Contains("oem, aftermarket, substitute",
            Err(Product(new { partType = "part-number" })));
    }

    [Fact]
    public void NamesAllThreeSoTheMessageSaysWhatToSendInstead()
    {
        var message = Err(Product(new { partType = "nonsense" }));

        foreach (var type in PartTypes.All) Assert.Contains(type, message);
    }

    [Theory]
    [InlineData("oem", true)]
    [InlineData("aftermarket", true)]
    [InlineData("substitute", true)]
    [InlineData("part-number", false)]
    [InlineData("", false)]
    public void KnowsWhichNamesAreItsOwn(string value, bool expected)
    {
        Assert.Equal(expected, PartTypes.IsKnown(value));
    }
}
