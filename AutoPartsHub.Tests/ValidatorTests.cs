using System.Text.Json;
using AutoPartsHub.Api.Admin;
using AutoPartsHub.Api.Catalogue;

namespace AutoPartsHub.Tests;

/// <summary>
/// What the admin forms are allowed to send.
/// </summary>
/// <remarks>
/// Each of these has a database constraint behind it. The point of checking
/// here as well is not belt and braces — it is that a violation names an index
/// and a sentence names the field, and the person reading it is filling in a
/// form.
/// </remarks>
public class ValidatorTests
{
    private static JsonElement Json(object value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement;

    private static JsonElement Raw(string json) => JsonDocument.Parse(json).RootElement;

    private static T Ok<T>(Validated<T> r)
    {
        Assert.True(r.Ok, $"expected success, got: {r.Error}");
        return r.Value!;
    }

    private static string Err<T>(Validated<T> r)
    {
        Assert.False(r.Ok, "expected a refusal, got success");
        return r.Error!;
    }

    /* ----------------------------------------------------- stock rows --- */

    private static JsonElement Levels(params object[] rows) => Json(new { levels = rows });

    private static object Row(
        string warehouseId = "w1", object? quantity = null, object? reserved = null,
        string? binLocation = null) =>
        new { warehouseId, quantity = quantity ?? 5, reserved = reserved ?? 0, binLocation };

    [Fact]
    public void AcceptsAPlainCount()
    {
        var row = Assert.Single(Ok(Validators.ReadStockRows(Levels(Row()))));

        Assert.Equal("w1", row.WarehouseId);
        Assert.Equal(5, row.Quantity);
        Assert.Equal(0, row.Reserved);
        Assert.Null(row.BinLocation);
    }

    [Fact]
    public void RefusesToPromiseMoreThanIsOnTheShelf()
    {
        // Mirrors the CHECK constraint. The easy way to reach it is editing
        // quantity downwards while a reservation is already standing.
        Assert.Contains("Reserved cannot exceed",
            Err(Validators.ReadStockRows(Levels(Row(quantity: 2, reserved: 5)))));
    }

    [Fact]
    public void AllowsReservingExactlyWhatIsThere()
    {
        Assert.Single(Ok(Validators.ReadStockRows(Levels(Row(quantity: 5, reserved: 5)))));
    }

    [Fact]
    public void NamesTheDuplicateRatherThanLettingTheUniqueKeySurface()
    {
        Assert.Contains("appears twice", Err(Validators.ReadStockRows(Levels(Row(), Row()))));
    }

    [Theory]
    [InlineData(1.5, 0)]
    [InlineData(-1, 0)]
    [InlineData(5, -1)]
    [InlineData(5, 0.5)]
    public void RefusesFractionalAndNegativeCounts(double quantity, double reserved)
    {
        Assert.Contains("whole number",
            Err(Validators.ReadStockRows(Levels(Row(quantity: quantity, reserved: reserved)))));
    }

    [Fact]
    public void RequiresAWarehouseOnEveryRow()
    {
        Assert.Contains("needs a warehouse",
            Err(Validators.ReadStockRows(Levels(Row(warehouseId: "")))));
    }

    [Fact]
    public void RefusesAnythingThatIsNotAListOfObjects()
    {
        Assert.Contains("list of stock levels", Err(Validators.ReadStockRows(Json(new { }))));
        Assert.Contains("list of stock levels",
            Err(Validators.ReadStockRows(Json(new { levels = "all of them" }))));
        Assert.Contains("must be an object", Err(Validators.ReadStockRows(Raw("""{"levels":[null]}"""))));
    }

    [Fact]
    public void TakesAnEmptyListWhichIsHowAPartStopsBeingHeldAnywhere()
    {
        Assert.Empty(Ok(Validators.ReadStockRows(Levels())));
    }

    [Fact]
    public void ReadsABlankBinLocationAsNone()
    {
        var row = Assert.Single(Ok(Validators.ReadStockRows(Levels(Row(binLocation: "   ")))));

        Assert.Null(row.BinLocation);
    }

    /* ------------------------------------------------ warehouse input --- */

    private static JsonElement Warehouse(object? code = null, object? name = null, object? extra = null)
    {
        var d = new Dictionary<string, object?>
        {
            ["code"] = code ?? "eu1",
            ["name"] = name ?? "Rotterdam",
        };
        if (extra is not null)
        {
            foreach (var p in extra.GetType().GetProperties()) d[p.Name] = p.GetValue(extra);
        }
        return Json(d);
    }

    [Fact]
    public void StoresTheCodeUppercasedSinceCodesAreComparedByEye()
    {
        Assert.Equal("EU1", Ok(Validators.ReadWarehouse(Warehouse())).Code);
    }

    [Fact]
    public void RequiresACodeAndAName()
    {
        Assert.Contains("code is required", Err(Validators.ReadWarehouse(Warehouse(code: "  "))));
        Assert.Contains("name is required", Err(Validators.ReadWarehouse(Warehouse(name: ""))));
    }

    [Fact]
    public void ReadsABlankPriorityAsTheDefaultRatherThanAsNaN()
    {
        Assert.Equal(0, Ok(Validators.ReadWarehouse(Warehouse(extra: new { priority = "" }))).Priority);
        Assert.Equal(0, Ok(Validators.ReadWarehouse(Warehouse())).Priority);
    }

    [Fact]
    public void RefusesAFractionalPriority()
    {
        Assert.Contains("whole number",
            Err(Validators.ReadWarehouse(Warehouse(extra: new { priority = 1.5 }))));
    }

    [Fact]
    public void IsActiveUnlessSomethingExplicitlySaysOtherwise()
    {
        Assert.True(Ok(Validators.ReadWarehouse(Warehouse())).Active);
        Assert.False(Ok(Validators.ReadWarehouse(Warehouse(extra: new { active = false }))).Active);
    }

    /* -------------------------------------------------- product input --- */

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
        return Json(d);
    }

    [Fact]
    public void RequiresTheFieldsAPartCannotExistWithout()
    {
        Assert.Contains("Part number", Err(Validators.ReadProduct(Product(new { partNumber = "" }))));
        Assert.Contains("Name", Err(Validators.ReadProduct(Product(new { name = "" }))));
        Assert.Contains("manufacturer", Err(Validators.ReadProduct(Product(new { manufacturerId = "" }))));
        Assert.Contains("vehicle system", Err(Validators.ReadProduct(Product(new { vehicleSystemId = "" }))));
    }

    [Fact]
    public void AcceptsAPurchasePriceOfZeroWhichIsWhatAnImportLandsOn()
    {
        // TecDoc carries no prices, so a freshly imported article has
        // basePrice 0 until a supplier price list gives it one. Refusing zero
        // would refuse it.
        Assert.Equal(0, Ok(Validators.ReadProduct(Product(new { basePrice = 0 }))).BasePrice);
    }

    [Fact]
    public void RefusesANegativeOrUnparseablePurchasePrice()
    {
        Assert.Contains("zero or more", Err(Validators.ReadProduct(Product(new { basePrice = -1 }))));
        Assert.Contains("zero or more", Err(Validators.ReadProduct(Product(new { basePrice = "free" }))));
    }

    [Fact]
    public void ReadsBlankDeliveryDaysAsInheritFromTheSupplier()
    {
        Assert.Null(Ok(Validators.ReadProduct(Product(new { stockDays = "" }))).StockDays);
        Assert.Null(Ok(Validators.ReadProduct(Product())).StockDays);
        Assert.Equal(3, Ok(Validators.ReadProduct(Product(new { stockDays = 3 }))).StockDays);
    }

    /* ------------------------------------------------- product images --- */

    private static JsonElement Images(params object[] rows) => Json(new { images = rows });

    [Fact]
    public void AcceptsHttpHttpsAndASiteRelativePath()
    {
        var rows = Ok(Validators.ReadImages(Images(
            new { url = "https://cdn.example.com/a.jpg" },
            new { url = "http://cdn.example.com/b.jpg" },
            new { url = "/uploads/c.jpg" })));

        Assert.Equal(3, rows.Count);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:image/png;base64,AAA")]
    [InlineData("blob:abc")]
    public void RefusesTheSchemesThatWouldExecuteOrEmbed(string url)
    {
        // These end up in an <img src> on a public page, so they are refused
        // here rather than sanitised at each place that renders one.
        Assert.Contains("must start", Err(Validators.ReadImages(Images(new { url }))));
    }

    [Theory]
    [InlineData("//evil.example/a.jpg")]
    [InlineData("/\\evil.example/a.jpg")]
    public void RefusesAProtocolRelativeUrlDressedAsAPath(string url)
    {
        // Starts with '/', so a bare startsWith('/') check waves it through,
        // and the browser then fetches it off-site.
        Assert.Contains("must start", Err(Validators.ReadImages(Images(new { url }))));
    }

    [Fact]
    public void CapsHowManyPicturesAPartCanCarry()
    {
        var many = Enumerable.Range(0, 13).Select(i => new { url = $"/img/{i}.jpg" }).ToArray();

        Assert.Contains("at most 12", Err(Validators.ReadImages(Images(many))));
    }

    [Fact]
    public void RequiresAUrlOnEveryRowAndRefusesAnOverLongOne()
    {
        Assert.Contains("needs a url", Err(Validators.ReadImages(Images(new { url = "" }))));
        Assert.Contains("too long",
            Err(Validators.ReadImages(Images(new { url = "/" + new string('a', 2048) }))));
    }

    [Fact]
    public void ReadsABlankAltAsNoneSoThePartNameCanStandIn()
    {
        var row = Assert.Single(Ok(Validators.ReadImages(Images(new { url = "/a.jpg", alt = "  " }))));

        Assert.Null(row.Alt);
    }

    /* ----------------------------------------------------- part numbers --- */

    [Theory]
    [InlineData("0 986 424 815", "0986424815")]
    [InlineData("09.9772.11", "09977211")]
    [InlineData("24.5219-0713.3", "24521907133")]
    [InlineData("W 712/75", "W71275")]
    public void IgnoresWhateverSeparatorsABrandPrints(string typed, string expected)
    {
        Assert.Equal(expected, PartNumbers.Normalise(typed));
    }

    [Fact]
    public void MakesTheSameNumberTypedTwoWaysCompareEqual()
    {
        Assert.Equal(PartNumbers.Normalise("17138616418"), PartNumbers.Normalise("171-386.164 18"));
    }

    [Fact]
    public void UpperCasesSoCaseCannotSplitAMatch()
    {
        Assert.Equal("W71275", PartNumbers.Normalise("w712/75"));
    }

    [Theory]
    [InlineData("---")]
    [InlineData("")]
    public void SurvivesAStringWithNothingUsableInIt(string value)
    {
        Assert.Equal("", PartNumbers.Normalise(value));
    }

    /* ------------------------------------------------------- site paths --- */

    [Theory]
    [InlineData("/")]
    [InlineData("/cart")]
    [InlineData("/orders?ref=APH-1")]
    [InlineData("/product/abc#specs")]
    public void AcceptsAPathOnThisSite(string value)
    {
        Assert.True(SiteLink.IsSitePath(value));
    }

    [Theory]
    // The whole reason this function exists. Every one of these begins with
    // '/', and every one of them leaves the site.
    [InlineData("//evil.example")]
    [InlineData("//evil.example/orders")]
    // The URL standard treats \ as / in the authority position.
    [InlineData("/\\evil.example")]
    [InlineData("/\\/evil.example")]
    public void RefusesAUrlThatOnlyLooksLikeAPath(string value)
    {
        Assert.False(SiteLink.IsSitePath(value));
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("javascript:alert(1)")]
    [InlineData("evil.example")]
    [InlineData("\\\\evil.example")]
    [InlineData("")]
    public void RefusesAnythingThatIsNotAPathAtAll(string value)
    {
        Assert.False(SiteLink.IsSitePath(value));
    }

    /* ---------------------------------------------- supplier reliability --- */

    [Fact]
    public void IsRankedStrongestRelationshipFirst()
    {
        Assert.Equal(new[] { "official", "dealer", "reliable", "standard" }, Reliabilities.All);
    }

    [Fact]
    public void RecognisesExactlyTheFourValuesAndNothingElse()
    {
        foreach (var value in Reliabilities.All) Assert.True(Reliabilities.IsKnown(value));

        Assert.False(Reliabilities.IsKnown("Official"));
        Assert.False(Reliabilities.IsKnown("trusted"));
        Assert.False(Reliabilities.IsKnown(""));
    }

    /* ------------------------------------------------------------ slugs --- */

    [Theory]
    [InlineData("ZZ Probe Supply Co", "zz-probe-supply-co")]
    [InlineData("  Spaced  Out  ", "spaced-out")]
    [InlineData("Ünïcode & Symbols!", "n-code-symbols")]
    [InlineData("---", "")]
    public void MakesAUrlOutOfACompanyName(string name, string expected)
    {
        // The supplier signup shows this back to the applicant before they
        // submit, so the two have to agree on what it produces.
        Assert.Equal(expected, Validators.Slugify(name));
    }
}
