using System.Text.Json;

namespace AutoPartsHub.Domain;

/// <summary>
/// Reads request fields the way the JavaScript API does.
/// </summary>
/// <remarks>
/// The other API coerces: <c>Number(raw.quantity)</c> turns "5" into 5 and
/// "two" into NaN, and <c>String(raw.productId ?? '')</c> turns a number into
/// its text. It then answers a bad value with a sentence naming the field.
///
/// Typed model binding cannot do that. A quantity of "two" fails to bind and
/// ASP.NET answers with an exception page before the handler runs, so the
/// caller gets a wall of text where the other API gave them "Quantities must
/// be whole numbers between 1 and 999". The endpoints that take a list of
/// lines therefore bind the body as raw JSON and read it through here.
///
/// Not a general permissiveness — it is one specific behaviour being matched,
/// in the two places the storefront posts a basket.
/// </remarks>
public static class JsonValues
{
    /// <summary>Number(value), including its NaN cases. Null when not a number.</summary>
    public static double? AsNumber(JsonElement? element)
    {
        if (element is not { } e) return 0;   // Number(undefined ?? 0) is 0

        return e.ValueKind switch
        {
            JsonValueKind.Number => e.GetDouble(),
            JsonValueKind.Null => 0,          // Number(null) is 0
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            JsonValueKind.String => double.TryParse(
                e.GetString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null,                        // Number("two") is NaN
            _ => null,                         // an object or an array is NaN
        };
    }

    /// <summary>String(value ?? ''), for the fields read as text.</summary>
    public static string AsString(JsonElement? element)
    {
        if (element is not { } e) return "";

        return e.ValueKind switch
        {
            JsonValueKind.String => e.GetString() ?? "",
            JsonValueKind.Null or JsonValueKind.Undefined => "",
            JsonValueKind.Number => e.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => e.GetRawText(),
        };
    }

    /// <summary>A property, or null when it is absent.</summary>
    public static JsonElement? Get(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value)
            ? value
            : null;

    /// <summary>True for a whole number, as Number.isInteger is.</summary>
    public static bool IsWhole(double? value) =>
        value is { } v && !double.IsNaN(v) && !double.IsInfinity(v) && Math.Floor(v) == v;
}
