using System.Text.Json;
using AutoPartsHub.Api.Catalogue;

namespace AutoPartsHub.Api.Admin;

/// <summary>
/// What the admin forms are allowed to send, and the sentence each refusal
/// gives back.
/// </summary>
/// <remarks>
/// The messages are copied, not rewritten. They reach a person in a form, and
/// two APIs that refuse the same thing in different words are two products.
/// </remarks>
public static class Validators
{
    public static Validated<T> Ok<T>(T value) => new(true, value, null);
    public static Validated<T> Fail<T>(string error) => new(false, default, error);

    private static string Text(JsonElement body, string key) =>
        JsonValues.AsString(JsonValues.Get(body, key)).Trim();

    private static string? Optional(JsonElement body, string key) =>
        Text(body, key) is { Length: > 0 } v ? v : null;

    /// <summary>Absent, empty or explicitly null all mean "not set".</summary>
    private static bool IsBlank(JsonElement? e) =>
        e is null
        || e.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
        || (e.Value.ValueKind == JsonValueKind.String && e.Value.GetString()!.Length == 0);

    /* ------------------------------------------------------- suppliers --- */

    public static Validated<SupplierInput> ReadSupplier(JsonElement body)
    {
        var name = Text(body, "name");
        if (name.Length == 0) return Fail<SupplierInput>("Name is required.");

        var code = Text(body, "code").ToUpperInvariant();
        if (code.Length == 0) return Fail<SupplierInput>("Code is required.");

        // Falls back to the name so the form does not have to ask for a slug
        // that is almost always just the name in url form.
        var slug = Slugify(Text(body, "slug") is { Length: > 0 } s ? s : name);
        if (slug.Length == 0)
        {
            return Fail<SupplierInput>("Could not make a url from that name — give a slug explicitly.");
        }

        var reliability = Text(body, "reliability") is { Length: > 0 } r ? r : "standard";
        if (!Reliabilities.IsKnown(reliability))
        {
            return Fail<SupplierInput>($"Reliability must be one of {string.Join(", ", Reliabilities.All)}.");
        }

        var rating = ReadRating(JsonValues.Get(body, "rating"));
        if (!rating.Ok) return Fail<SupplierInput>(rating.Error!);

        var returns = ReadTriState(JsonValues.Get(body, "acceptsReturns"), "Returns");
        if (!returns.Ok) return Fail<SupplierInput>(returns.Error!);

        var guarantee = ReadCount(JsonValues.Get(body, "guaranteeMonths"), "Guarantee");
        if (!guarantee.Ok) return Fail<SupplierInput>(guarantee.Error!);

        var stockDays = ReadCount(JsonValues.Get(body, "defaultStockDays"), "Delivery time");
        if (!stockDays.Ok) return Fail<SupplierInput>(stockDays.Error!);

        return Ok(new SupplierInput(
            name, code, slug, Optional(body, "description"), reliability,
            rating.Value, returns.Value, Optional(body, "country"),
            guarantee.Value, stockDays.Value, Optional(body, "purchaseCurrencyId")));
    }

    /// <summary>Lowercase, hyphenated, url-safe — a supplier's page is addressed by it.</summary>
    public static string Slugify(string value)
    {
        var chars = value.ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')
            .ToArray();
        var collapsed = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return collapsed;
    }

    /// <summary>
    /// A rating from a request body.
    /// </summary>
    /// <remarks>
    /// Absent, empty or explicitly null all mean unrated — the form sends an
    /// empty select value for it, and clearing a rating has to be possible.
    /// The database carries the same constraint, but failing here gives the
    /// admin a sentence instead of a violation naming an index.
    /// </remarks>
    public static Validated<int?> ReadRating(JsonElement? raw)
    {
        if (IsBlank(raw)) return Ok<int?>(null);

        var value = JsonValues.AsNumber(raw);
        if (!JsonValues.IsWhole(value) || value < 1 || value > 5)
        {
            return Fail<int?>("Rating must be a whole number from 1 to 5, or left blank.");
        }
        return Ok<int?>((int)value!.Value);
    }

    /// <summary>
    /// A tri-state flag.
    /// </summary>
    /// <remarks>
    /// Absent, empty or null all mean "not established" — the form sends an
    /// empty select for that. Everything else has to be an actual boolean or
    /// the string form of one, so a typo cannot quietly land as a confident
    /// "no".
    /// </remarks>
    public static Validated<bool?> ReadTriState(JsonElement? raw, string label)
    {
        if (IsBlank(raw)) return Ok<bool?>(null);

        return raw!.Value.ValueKind switch
        {
            JsonValueKind.True => Ok<bool?>(true),
            JsonValueKind.False => Ok<bool?>(false),
            JsonValueKind.String when raw.Value.GetString() == "true" => Ok<bool?>(true),
            JsonValueKind.String when raw.Value.GetString() == "false" => Ok<bool?>(false),
            _ => Fail<bool?>($"{label} must be yes, no, or left blank."),
        };
    }

    /// <summary>A whole number of something, or null. Rejects negatives and fractions.</summary>
    public static Validated<int?> ReadCount(JsonElement? raw, string label)
    {
        if (IsBlank(raw)) return Ok<int?>(null);

        var value = JsonValues.AsNumber(raw);
        if (!JsonValues.IsWhole(value) || value < 0)
        {
            return Fail<int?>($"{label} must be a whole number of zero or more, or left blank.");
        }
        return Ok<int?>((int)value!.Value);
    }

    /* ------------------------------------------------------ warehouses --- */

    public static Validated<WarehouseInput> ReadWarehouse(JsonElement body)
    {
        var code = Text(body, "code").ToUpperInvariant();
        var name = Text(body, "name");

        if (code.Length == 0) return Fail<WarehouseInput>("A warehouse code is required.");
        if (name.Length == 0) return Fail<WarehouseInput>("A warehouse name is required.");

        // Blank means "leave it where it is", which for a new row is the default.
        var raw = JsonValues.Get(body, "priority");
        var priority = IsBlank(raw) ? 0d : JsonValues.AsNumber(raw);
        if (!JsonValues.IsWhole(priority))
        {
            return Fail<WarehouseInput>("Priority must be a whole number.");
        }

        return Ok(new WarehouseInput(
            code, name, Optional(body, "city"), Optional(body, "address"),
            !IsFalse(body, "active"), (int)priority!.Value));
    }

    public static Validated<OutletInput> ReadOutlet(JsonElement body)
    {
        var code = Text(body, "code").ToUpperInvariant();
        var name = Text(body, "name");

        if (code.Length == 0) return Fail<OutletInput>("An outlet code is required.");
        if (name.Length == 0) return Fail<OutletInput>("An outlet name is required.");

        return Ok(new OutletInput(
            code, name, Optional(body, "city"), Optional(body, "address"),
            Optional(body, "phone"), Optional(body, "warehouseId"), !IsFalse(body, "active")));
    }

    /* ------------------------------------------------------ currencies --- */

    /// <summary>
    /// A currency, without <c>isBase</c>.
    /// </summary>
    /// <remarks>
    /// Which currency the catalogue's purchase prices are kept in is a
    /// property of the data, not something to flip on a form — changing it
    /// would reinterpret every stored price and every past order at once. The
    /// database carries a partial unique index so there can only ever be one,
    /// and moving it is a migration.
    /// </remarks>
    public static Validated<CurrencyInput> ReadCurrency(JsonElement body)
    {
        var code = Text(body, "code").ToUpperInvariant();
        if (code.Length != 3 || !code.All(char.IsAsciiLetterUpper))
        {
            return Fail<CurrencyInput>("Code must be a three-letter currency code, like EUR or EGP.");
        }

        var name = Text(body, "name");
        if (name.Length == 0) return Fail<CurrencyInput>("Name is required.");

        var symbol = Text(body, "symbol");
        if (symbol.Length == 0) return Fail<CurrencyInput>("Symbol is required.");

        var rate = JsonValues.AsNumber(JsonValues.Get(body, "rate"));
        if (rate is null || double.IsNaN(rate.Value) || double.IsInfinity(rate.Value) || rate <= 0)
        {
            return Fail<CurrencyInput>("Rate must be a number greater than zero.");
        }

        return Ok(new CurrencyInput(code, name, symbol, rate.Value, !IsFalse(body, "active")));
    }

    /* -------------------------------------------------------- products --- */

    public static Validated<ProductInput> ReadProduct(JsonElement body)
    {
        var partNumber = Text(body, "partNumber");
        var name = Text(body, "name");
        var manufacturerId = Text(body, "manufacturerId");
        var vehicleSystemId = Text(body, "vehicleSystemId");

        if (partNumber.Length == 0) return Fail<ProductInput>("Part number is required.");
        if (name.Length == 0) return Fail<ProductInput>("Name is required.");
        if (manufacturerId.Length == 0) return Fail<ProductInput>("Pick a manufacturer.");
        if (vehicleSystemId.Length == 0) return Fail<ProductInput>("Pick a vehicle system.");

        var basePrice = JsonValues.AsNumber(JsonValues.Get(body, "basePrice"));
        if (basePrice is null || double.IsNaN(basePrice.Value) || double.IsInfinity(basePrice.Value)
            || basePrice < 0)
        {
            return Fail<ProductInput>("Purchase price must be a number of zero or more.");
        }

        // Blank means "use the supplier's default", which the route resolves.
        var rawDays = JsonValues.Get(body, "stockDays");
        int? stockDays = null;
        if (!IsBlank(rawDays))
        {
            var days = JsonValues.AsNumber(rawDays);
            if (!JsonValues.IsWhole(days) || days < 0)
            {
                return Fail<ProductInput>("Delivery days must be a whole number of zero or more.");
            }
            stockDays = (int)days!.Value;
        }

        // Absent means aftermarket, matching the column's own default, so a
        // client that predates this field still writes a valid row rather than
        // failing.
        var partType = Text(body, "partType") is { Length: > 0 } t ? t : "aftermarket";
        if (!PartTypes.All.Contains(partType))
        {
            return Fail<ProductInput>($"Part type must be one of {string.Join(", ", PartTypes.All)}.");
        }

        return Ok(new ProductInput(
            partNumber, name, Optional(body, "description"), manufacturerId, vehicleSystemId,
            Optional(body, "supplierId"), basePrice.Value, stockDays, partType,
            // Blank means unclassified, which is a real answer: the part prices
            // by the account default, as everything did before this existed.
            Optional(body, "goodsCategoryId")));
    }

    /// <summary>
    /// The whole picture list for one part, in display order.
    /// </summary>
    /// <remarks>
    /// Submitted as an ordered set rather than one image at a time because
    /// order is the only thing that says which picture leads. Reordering is
    /// then the same request as adding, and there is no moment where two
    /// images both claim to be first.
    /// </remarks>
    public static Validated<List<ImageInput>> ReadImages(JsonElement body)
    {
        var raw = JsonValues.Get(body, "images");
        if (raw is not { ValueKind: JsonValueKind.Array } list)
        {
            return Fail<List<ImageInput>>("Expected a list of images.");
        }
        if (list.GetArrayLength() > 12)
        {
            return Fail<List<ImageInput>>("A part can carry at most 12 pictures.");
        }

        var rows = new List<ImageInput>();
        foreach (var entry in list.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                return Fail<List<ImageInput>>("Every image must be an object.");
            }

            var url = JsonValues.AsString(JsonValues.Get(entry, "url")).Trim();
            if (url.Length == 0) return Fail<List<ImageInput>>("Every image needs a url.");

            // http(s) or a site-relative path. Anything else — data:,
            // javascript:, blob: — ends up in an <img src> on a public page, so
            // it is refused here rather than sanitised at each place that
            // renders it.
            var allowed = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                          || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                          || SiteLink.IsSitePath(url);
            if (!allowed)
            {
                return Fail<List<ImageInput>>("An image url must start with http://, https:// or /.");
            }
            if (url.Length > 2048) return Fail<List<ImageInput>>("That image url is too long.");

            var alt = JsonValues.AsString(JsonValues.Get(entry, "alt")).Trim();
            rows.Add(new ImageInput(url, alt.Length > 0 ? alt : null));
        }

        return Ok(rows);
    }

    /// <summary>
    /// The whole stock table for one part.
    /// </summary>
    /// <remarks>
    /// Submitted as a set rather than row by row: the editor shows every
    /// warehouse at once, and one request that either takes all the counts or
    /// none of them cannot leave a part half-recounted.
    /// </remarks>
    public static Validated<List<StockRowInput>> ReadStockRows(JsonElement body)
    {
        var raw = JsonValues.Get(body, "levels");
        if (raw is not { ValueKind: JsonValueKind.Array } list)
        {
            return Fail<List<StockRowInput>>("Expected a list of stock levels.");
        }

        var rows = new List<StockRowInput>();
        var seen = new HashSet<string>();

        foreach (var entry in list.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                return Fail<List<StockRowInput>>("Every stock level must be an object.");
            }

            var warehouseId = JsonValues.AsString(JsonValues.Get(entry, "warehouseId")).Trim();
            if (warehouseId.Length == 0)
            {
                return Fail<List<StockRowInput>>("Every stock level needs a warehouse.");
            }

            // The database refuses a duplicate pair anyway; catching it here
            // names the warehouse instead of surfacing a constraint violation.
            if (!seen.Add(warehouseId))
            {
                return Fail<List<StockRowInput>>("A warehouse appears twice. Each may hold one count.");
            }

            var quantityRaw = JsonValues.Get(entry, "quantity");
            var reservedRaw = JsonValues.Get(entry, "reserved");
            var quantity = quantityRaw is null ? 0 : JsonValues.AsNumber(quantityRaw);
            var reserved = reservedRaw is null ? 0 : JsonValues.AsNumber(reservedRaw);

            if (!JsonValues.IsWhole(quantity) || quantity < 0)
            {
                return Fail<List<StockRowInput>>("Quantity must be a whole number of zero or more.");
            }
            if (!JsonValues.IsWhole(reserved) || reserved < 0)
            {
                return Fail<List<StockRowInput>>("Reserved must be a whole number of zero or more.");
            }
            // Mirrors the CHECK constraint, so the admin gets a sentence rather
            // than a database error. Promising more than is on the shelf is the
            // mistake this catches, and it is the easy one to make by editing
            // quantity downwards.
            if (reserved > quantity)
            {
                return Fail<List<StockRowInput>>("Reserved cannot exceed the quantity on the shelf.");
            }

            var bin = JsonValues.AsString(JsonValues.Get(entry, "binLocation")).Trim();
            rows.Add(new StockRowInput(
                warehouseId, (int)quantity!.Value, (int)reserved!.Value, bin.Length > 0 ? bin : null));
        }

        return Ok(rows);
    }

    /// <summary>`active: false` explicitly; anything else is true.</summary>
    private static bool IsFalse(JsonElement body, string key) =>
        JsonValues.Get(body, key) is { ValueKind: JsonValueKind.False };
}

public record Validated<T>(bool Ok, T? Value, string? Error);

public record SupplierInput(
    string Name, string Code, string Slug, string? Description, string Reliability,
    int? Rating, bool? AcceptsReturns, string? Country, int? GuaranteeMonths,
    int? DefaultStockDays, string? PurchaseCurrencyId);

public record WarehouseInput(
    string Code, string Name, string? City, string? Address, bool Active, int Priority);

public record OutletInput(
    string Code, string Name, string? City, string? Address, string? Phone,
    string? WarehouseId, bool Active);

public record CurrencyInput(string Code, string Name, string Symbol, double Rate, bool Active);

public record ProductInput(
    string PartNumber, string Name, string? Description, string ManufacturerId,
    string VehicleSystemId, string? SupplierId, double BasePrice, int? StockDays,
    string PartType,
    /// <summary>The commercial category the part is priced through, or null.</summary>
    string? GoodsCategoryId);

/// <summary>
/// The three kinds a part can be.
/// </summary>
/// <remarks>
/// Its own type rather than three strings in the validator, because the search
/// endpoint and the storefront read the same list — and a fourth kind added in
/// one place and rejected in another is the kind of split nothing catches
/// until a form refuses a value the filter is already offering.
/// </remarks>
public static class PartTypes
{
    public static readonly string[] All = ["oem", "aftermarket", "substitute"];

    public static bool IsKnown(string value) => All.Contains(value);
}

public record ImageInput(string Url, string? Alt);

public record StockRowInput(string WarehouseId, int Quantity, int Reserved, string? BinLocation);

/// <summary>
/// Whether a string is a path on this site.
/// </summary>
/// <remarks>
/// <c>/…</c> is a path. <c>//…</c> is not: the first character says path and
/// every browser reads it as an absolute url on another host, which is how a
/// check meant to keep a link on this site hands out an off-site one. The
/// backslash form is the same trap wearing a different hat — the URL standard
/// treats <c>\</c> as <c>/</c> in the authority position, so
/// <c>/\evil.example</c> resolves off-site too.
/// </remarks>
public static class SiteLink
{
    public static bool IsSitePath(string value)
    {
        if (!value.StartsWith('/')) return false;

        // A lone "/" is the site root, which is a path like any other.
        if (value.Length == 1) return true;
        return value[1] is not ('/' or '\\');
    }
}
