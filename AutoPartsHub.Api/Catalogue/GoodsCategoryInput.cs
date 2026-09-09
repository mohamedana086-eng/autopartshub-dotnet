using System.Text.Json;
using System.Text.RegularExpressions;
using AutoPartsHub.Api.Pricing;
using AutoPartsHub.Domain.Catalogue;
using AutoPartsHub.Domain.Pricing;
using AutoPartsHub.Domain;

namespace AutoPartsHub.Api.Endpoints;

/// <summary>
/// Reading a goods category off a request.
/// </summary>
/// <remarks>
/// Mirrors lib/admin-goods-categories.ts, message for message: the comparison
/// harness reads the sentences, not just the status codes.
///
/// The one rule worth stating twice is that a markup is a pair. A type without
/// a value, or a value without a type, is not a smaller markup — it is an
/// unfinished one, and letting it through would leave a category that looks
/// priced and prices nothing.
/// </remarks>
public static partial class GoodsCategoryInput
{
    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NotSlugSafe();

    [GeneratedRegex(@"^-+|-+$")]
    private static partial Regex EdgeHyphens();

    private const int MaxName = 40;
    private const int MaxDescription = 400;

    /// <summary>Lower case, hyphens, no runs — the shape the rest of the app uses.</summary>
    public static string Slugify(string value) =>
        EdgeHyphens().Replace(NotSlugSafe().Replace(value.ToLowerInvariant(), "-"), "");

    public static (GoodsCategoryValues? Value, string? Error) Read(JsonElement body)
    {
        var name = JsonValues.AsString(JsonValues.Get(body, "name")).Trim();
        if (name.Length == 0) return (null, "A category needs a name.");
        if (name.Length > MaxName) return (null, $"Keep the name under {MaxName} characters.");

        // Derived unless given, so the common case is one field. Given
        // explicitly it is still normalised — a slug with a space in it is a
        // link that breaks.
        var given = JsonValues.AsString(JsonValues.Get(body, "slug")).Trim();
        var slug = Slugify(given.Length > 0 ? given : name);
        if (slug.Length == 0) return (null, "Could not make a web address from that name.");

        var description = JsonValues.AsString(JsonValues.Get(body, "description")).Trim();
        if (description.Length > MaxDescription)
        {
            return (null, $"Keep the description under {MaxDescription} characters.");
        }

        var rawType = JsonValues.Get(body, "markupType");
        var rawValue = JsonValues.Get(body, "markupValue");
        var hasType = HasContent(rawType);
        var hasValue = HasContent(rawValue);

        if (hasType != hasValue)
        {
            // Named as the pair it is. "Invalid markup" would leave the reader
            // checking the half they did fill in.
            return (null,
                "A markup needs both a type and a value, or neither — leave both blank for a category that only organises.");
        }

        string? markupType = null;
        double? markupValue = null;
        double? markupMinAmount = null;

        if (hasType)
        {
            var type = JsonValues.AsString(JsonValues.Get(body, "markupType")) ?? "";
            if (!MarkupTypes.All.Contains(type))
            {
                return (null, $"Markup type must be one of {string.Join(", ", MarkupTypes.All)}.");
            }
            markupType = type;

            var number = JsonValues.AsNumber(JsonValues.Get(body, "markupValue"));
            if (number is null) return (null, "The markup value must be a number.");

            // Below -100% the part prices negative and the shop pays the
            // customer to take it. A loss-leader is a real decision; that is
            // not one.
            if (number <= -100)
            {
                return (null, "A markup below -100% would price the part below nothing.");
            }
            if (markupType == "FIXED" && number < 0)
            {
                return (null, "A fixed price cannot be negative.");
            }
            markupValue = number;

            // The floor belongs to exactly one type — set on any other it
            // would do nothing, and missing on this one the type is a plain
            // percentage under another name. The database enforces the same
            // pair, both directions.
            var rawFloor = JsonValues.Get(body, "markupMinAmount");
            var hasFloor = HasContent(rawFloor);

            if (markupType == "PERCENT_MIN")
            {
                if (!hasFloor)
                {
                    return (null, "A percentage with a floor needs the floor. Set a minimum amount.");
                }
                var floor = JsonValues.AsNumber(rawFloor);
                if (floor is null) return (null, "The minimum amount must be a number.");
                if (floor < 0)
                {
                    return (null, "A floor below nothing is a floor that never applies.");
                }
                markupMinAmount = floor;
            }
            else if (hasFloor)
            {
                return (null, "A minimum amount only applies to a percentage with a floor.");
            }
        }

        var sortOrder = (int)(JsonValues.AsNumber(JsonValues.Get(body, "sortOrder")) ?? 0);

        return (new GoodsCategoryValues(
            Name: name,
            Slug: slug,
            Description: description.Length > 0 ? description : null,
            MarkupType: markupType,
            MarkupValue: markupValue,
            MarkupMinAmount: markupMinAmount,
            SortOrder: sortOrder,
            // Absent means on. A category created switched off is a category
            // nobody meant to create.
            Active: JsonValues.Get(body, "active") is not { } a || a.ValueKind != JsonValueKind.False), null);
    }

    private static bool HasContent(JsonElement? raw) =>
        raw is { } v
        && v.ValueKind != JsonValueKind.Null
        && v.ValueKind != JsonValueKind.Undefined
        && !(v.ValueKind == JsonValueKind.String && v.GetString()?.Length == 0);
}

public record GoodsCategoryValues(
    string Name,
    string Slug,
    string? Description,
    string? MarkupType,
    double? MarkupValue,
    /// <summary>The floor under PERCENT_MIN, and null on every other type.</summary>
    double? MarkupMinAmount,
    int SortOrder,
    bool Active);

/// <summary>An id and a name — what a dropdown is built from.</summary>
public record NamedRow(string Id, string Name);
