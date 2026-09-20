using System.Text.Json;
using AutoPartsHub.Domain.Catalogue;

namespace AutoPartsHub.Domain.Pricing;

/// <summary>One rung of a ladder, as a form sends it.</summary>
/// <param name="From">Where the rung starts, in the base currency. The first must be 0.</param>
/// <param name="To">Where it ends, or null for the open top. Only the last may be null.</param>
public record LadderRung(double From, double? To, double Value);

/// <param name="Label">Names the ladder; each rung's rule is labelled from it.</param>
/// <param name="MinAmount">The floor, for PercentMin. Applies to every rung.</param>
public record LadderInput(
    string Label, string Type, double? MinAmount, IReadOnlyList<LadderRung> Rungs);

/// <summary>
/// A ladder of standard margins, by what a part costs to buy.
/// </summary>
/// <remarks>
/// "Under €50 earns 40%, €50 to €200 earns 30%, over €200 earns 22%" is one
/// commercial decision, and the specification calls it the standard margin
/// brackets. The engine has been able to express it since the price band
/// arrived — each rung is an ordinary markup rule with a purchase-price band —
/// so this adds NO second engine, no table, and no new rung on the pricing
/// ladder.
///
/// What it adds is the thing a rule-at-a-time screen cannot: the shape of the
/// whole ladder. Entered one rule at a time, a gap between €50 and €51 or an
/// overlap between two rungs is invisible — every rule looks correct on its own
/// screen, and the mistake shows up as a part priced from the tier default when
/// somebody expected 30%, months later, on one part in a thousand.
///
/// So the validation IS the feature, and it is here, pure, so both ports refuse
/// the same ladder with the same sentence.
/// </remarks>
public static class MarkupLadders
{
    /// <summary>How many rungs one ladder may have.</summary>
    public const int MaxRungs = 12;

    /// <summary>
    /// Reads a ladder from a request body, and refuses one that is not a
    /// ladder.
    /// </summary>
    /// <remarks>
    /// The four refusals, and why each is worth a sentence rather than a shrug:
    /// a GAP leaves a part pricing from the tier default, which is a number
    /// nobody chose for it and which looks like a working answer; an OVERLAP
    /// makes two rules match, and which wins is decided by specificity and then
    /// priority, neither of which the person writing a ladder is thinking
    /// about; NOT STARTING AT ZERO silently leaves out the cheapest parts, the
    /// ones a margin matters most on; and a CLOSED TOP assumes the most
    /// expensive part in the catalogue today is the most expensive one next
    /// quarter.
    /// </remarks>
    public static (bool Ok, LadderInput? Value, string? Error) Read(JsonElement body)
    {
        var label = JsonValues.AsString(JsonValues.Get(body, "label")).Trim();
        if (label.Length == 0) return (false, null, "Give the ladder a name.");
        if (label.Length > 80) return (false, null, "Keep the ladder name under 80 characters.");

        var typeText = JsonValues.AsString(JsonValues.Get(body, "type"));
        if (typeText.Length == 0) typeText = "PERCENT";
        if (!MarkupTypes.All.Contains(typeText))
        {
            return (false, null,
                $"Adjustment type must be {string.Join(", ", MarkupTypes.All)}.");
        }
        var type = typeText;

        var minAmount = Number(JsonValues.Get(body, "minAmount"));
        if (type == "PERCENT_MIN")
        {
            if (minAmount is null)
            {
                return (false, null,
                    "A percentage with a floor needs the floor. Set a minimum amount.");
            }
            if (minAmount < 0)
            {
                return (false, null, "A floor below nothing is a floor that never applies.");
            }
        }
        else if (minAmount is not null)
        {
            return (false, null, "A minimum amount only applies to a percentage with a floor.");
        }

        var rungsField = JsonValues.Get(body, "rungs");
        if (rungsField is not { ValueKind: JsonValueKind.Array } array || array.GetArrayLength() == 0)
        {
            return (false, null, "A ladder needs at least one band.");
        }
        if (array.GetArrayLength() > MaxRungs)
        {
            return (false, null, $"A ladder can have at most {MaxRungs} bands.");
        }

        var rungs = new List<LadderRung>();
        var index = 0;

        foreach (var raw in array.EnumerateArray())
        {
            var at = ++index;

            var from = raw.ValueKind == JsonValueKind.Object
                ? Number(JsonValues.Get(raw, "from"))
                : null;
            if (from is null || from < 0)
            {
                return (false, null, $"Band {at} needs a starting price of zero or more.");
            }

            // Null and empty both mean the open top, read the way the margin
            // columns read an empty box — a screen should not have to know
            // which absence this one means.
            var to = raw.ValueKind == JsonValueKind.Object
                ? Number(JsonValues.Get(raw, "to"))
                : null;
            if (to is not null && to <= from)
            {
                return (false, null, $"Band {at} ends at or below where it starts.");
            }

            var value = raw.ValueKind == JsonValueKind.Object
                ? Number(JsonValues.Get(raw, "value"))
                : null;
            if (value is null) return (false, null, $"Band {at} needs a margin.");
            if (value < 0) return (false, null, $"Band {at} cannot have a negative margin.");

            rungs.Add(new LadderRung(from.Value, to, value.Value));
        }

        // Sorted rather than insisted on: the order rows were typed in is not a
        // mistake worth refusing. The shape of the ladder is.
        rungs.Sort((a, b) => a.From.CompareTo(b.From));

        if (rungs[0].From != 0)
        {
            return (false, null,
                $"The first band has to start at 0 — as it stands, anything under " +
                $"{Money(rungs[0].From)} prices from the tier default.");
        }

        for (var i = 0; i < rungs.Count - 1; i++)
        {
            var rung = rungs[i];
            var next = rungs[i + 1];

            if (rung.To is null)
            {
                return (false, null,
                    $"Band {i + 1} is open at the top but is not the last one. " +
                    "Only the top band may be left open.");
            }
            if (rung.To > next.From)
            {
                return (false, null,
                    $"Bands {i + 1} and {i + 2} overlap between {Money(next.From)} and " +
                    $"{Money(rung.To.Value)}. Two rules would match, and which one wins is " +
                    "decided by specificity rather than by this ladder.");
            }
            if (rung.To < next.From)
            {
                return (false, null,
                    $"Nothing covers {Money(rung.To.Value)} to {Money(next.From)}. " +
                    "A part costing that much would price from the tier default.");
            }
        }

        if (rungs[^1].To is not null)
        {
            return (false, null,
                $"The top band has to be open-ended — as it stands, anything over " +
                $"{Money(rungs[^1].To!.Value)} prices from the tier default.");
        }

        return (true, new LadderInput(label, type, minAmount, rungs), null);
    }

    /// <summary>What each rung's rule is called.</summary>
    /// <remarks>
    /// The band is in the name because these arrive as a dozen rules in a list
    /// sorted by neither price nor creation, and "Standard margin" twelve times
    /// over is a list nobody can read. The name is also how somebody finds the
    /// ladder again: nothing marks these rules as belonging to one,
    /// deliberately — they are ordinary rules the moment they exist, and
    /// editing one by hand is a thing somebody should be able to do without the
    /// ladder disowning it.
    /// </remarks>
    public static string RungLabel(string label, LadderRung rung) =>
        rung.To is null
            ? $"{label} · {Money(rung.From)} and up"
            : $"{label} · {Money(rung.From)}–{Money(rung.To.Value)}";

    /// <summary>Whole euros where it is whole, two places where it is not.</summary>
    private static string Money(double value) =>
        value == Math.Floor(value) && !double.IsInfinity(value)
            ? $"€{value.ToString("0", System.Globalization.CultureInfo.InvariantCulture)}"
            : $"€{value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}";

    /// <summary>A number, or null for absent, null and empty alike.</summary>
    private static double? Number(JsonElement? field)
    {
        if (field is null) return null;
        if (field.Value.ValueKind == JsonValueKind.Null) return null;
        if (JsonValues.AsString(field).Length == 0) return null;

        var value = JsonValues.AsNumber(field);
        return value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value)
            ? null
            : value;
    }
}
