namespace AutoPartsHub.Api.Vehicles;

/// <summary>
/// The nine things a customer can be asked about a car.
/// </summary>
/// <remarks>
/// THE ORDER IS NOT A SEQUENCE
/// ---------------------------
/// They are listed in the order the UI shows them, and that is all the order
/// means. Somebody standing in front of a car knows its shape and which side
/// the wheel is on immediately, and may have no idea which series it belongs
/// to — so any subset may be given, in any order, and every list is computed
/// from whatever else is known.
///
/// Mirrors lib/vehicle-fields.ts, label for label: the comparison harness
/// reads the words, not just the shapes.
/// </remarks>
public static class VehicleFields
{
    public static readonly string[] All =
        ["make", "series", "model", "year", "bodyType", "steeringSide", "transmission", "region", "engine"];

    /// <summary>
    /// Below this many vehicles, showing the list beats asking another
    /// question — a customer who can see eight cars finds theirs by reading,
    /// and another dropdown is another chance to pick wrong and see nothing.
    /// </summary>
    public const int ShowVehiclesAt = 8;

    public static bool IsField(string value) => All.Contains(value);

    /// <summary>
    /// The closed vocabularies, and what each value is called on screen.
    /// </summary>
    /// <remarks>
    /// Closed because a dropdown offering both "Saloon" and "Sedan" splits the
    /// same cars into two answers and finds parts for neither. The database
    /// carries the same lists as check constraints; these are the labels.
    /// </remarks>
    public static readonly Dictionary<string, string> Regions = new()
    {
        ["europe"] = "Europe",
        ["north-america"] = "North America",
        ["middle-east"] = "Middle East",
        ["asia"] = "Asia",
        ["africa"] = "Africa",
        ["south-america"] = "South America",
        ["oceania"] = "Oceania",
    };

    public static readonly Dictionary<string, string> SteeringSides = new()
    {
        ["left"] = "Left-hand drive",
        ["right"] = "Right-hand drive",
    };

    public static readonly Dictionary<string, string> BodyTypes = new()
    {
        ["hatchback"] = "Hatchback",
        ["saloon"] = "Saloon",
        ["estate"] = "Estate",
        ["coupe"] = "Coupé",
        ["convertible"] = "Convertible",
        ["suv"] = "SUV",
        ["mpv"] = "MPV",
        ["pickup"] = "Pickup",
        ["van"] = "Van",
    };

    public static readonly Dictionary<string, string> Transmissions = new()
    {
        ["manual"] = "Manual",
        ["automatic"] = "Automatic",
    };

    /// <summary>What each field is called on screen, in the order the form shows them.</summary>
    public static readonly Dictionary<string, string> Labels = new()
    {
        ["make"] = "Make",
        ["series"] = "Series",
        ["model"] = "Model",
        ["year"] = "Year",
        ["bodyType"] = "Body",
        ["steeringSide"] = "Steering",
        ["transmission"] = "Gearbox",
        ["region"] = "Market",
        ["engine"] = "Engine",
    };

    /// <summary>The label for a coded value, or the value itself where it is already a name.</summary>
    public static string ValueLabel(string field, string value) => field switch
    {
        "region" => Regions.GetValueOrDefault(value, value),
        "steeringSide" => SteeringSides.GetValueOrDefault(value, value),
        "bodyType" => BodyTypes.GetValueOrDefault(value, value),
        "transmission" => Transmissions.GetValueOrDefault(value, value),
        // make, series, model, year and engine are already the words the
        // catalogue holds.
        _ => value,
    };

    /// <summary>
    /// Whether a value is one this field can take.
    /// </summary>
    /// <remarks>
    /// Only the four coded fields have a list to check against. The rest are
    /// catalogue text, and the finder's answer to an unknown one is an empty
    /// result — which is the honest answer to "show me the cars that are a BMW
    /// model called nonsense".
    /// </remarks>
    public static bool IsKnownValue(string field, string value) => field switch
    {
        "region" => Regions.ContainsKey(value),
        "steeringSide" => SteeringSides.ContainsKey(value),
        "bodyType" => BodyTypes.ContainsKey(value),
        "transmission" => Transmissions.ContainsKey(value),
        "year" => value.Length == 4 && value.All(char.IsAsciiDigit),
        _ => value.Length > 0,
    };
}
