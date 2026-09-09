namespace AutoPartsHub.Domain.Catalogue;

/// <summary>
/// How a part is packed, and what that allows somebody to order.
/// </summary>
/// <remarks>
/// The rule has to hold in three places at once: the basket, the order, and
/// the quantity control the customer types into. Each had every reason to grow
/// its own copy, and a rule implemented three times is a rule that disagrees
/// with itself the first time one of them is edited.
///
/// THE RULE
/// --------
/// <c>quantityPerPackage</c> is how many pieces come in one package, and a
/// part is sold by the package. Brake pads come two to a set and wheel studs
/// ten to a bag: three brake pads is not a smaller order, it is an order
/// nobody can pick. So an orderable quantity is a whole number of packages.
///
/// A part packed one to a package — most of them — has 1, every quantity
/// divides by 1, and the rule costs nothing. That is deliberate: the
/// constraint is arithmetic that is vacuous in the ordinary case rather than a
/// branch that has to remember to be skipped.
/// </remarks>
public static class Packaging
{
    /// <summary>
    /// What one package is called, commonest first.
    /// </summary>
    /// <remarks>
    /// <c>piece</c> is the default because most of a catalogue is sold singly,
    /// and because it is the reading that constrains nothing — a part whose
    /// packaging nobody has recorded should not start refusing orders.
    /// </remarks>
    public static readonly string[] Units =
        ["piece", "pair", "set", "box", "pack", "litre", "metre", "kit"];

    public const string DefaultUnit = "piece";

    /// <summary>
    /// The largest quantity per package the catalogue will accept.
    /// </summary>
    /// <remarks>
    /// Not a fact about packaging — a guard against a mistyped import turning
    /// one part into an order nobody can place. A pack of a thousand exists;
    /// a pack of a million is a decimal point.
    /// </remarks>
    public const int MaxPerPackage = 1000;

    public static bool IsUnit(string value) => Units.Contains(value);

    /// <summary>A quantity a part can actually be ordered in.</summary>
    public static bool IsOrderableQuantity(int quantity, int perPackage)
    {
        if (quantity < 1) return false;
        // A nonsensical pack size constrains nothing rather than refusing
        // everything. Bad data should not take the shop down; the check
        // constraint on the column is what keeps it out in the first place.
        if (perPackage < 1) return true;
        return quantity % perPackage == 0;
    }

    /// <summary>
    /// The orderable quantities either side of one that is not.
    /// </summary>
    /// <remarks>
    /// <c>Down</c> is null below one package: there is no smaller order, and
    /// offering zero would be offering to remove the line.
    /// </remarks>
    public static (int? Down, int Up) NearestOrderable(int quantity, int perPackage)
    {
        if (perPackage < 1) return (quantity, quantity);

        var packages = quantity / perPackage;
        return (packages >= 1 ? packages * perPackage : null, (packages + 1) * perPackage);
    }

    /// <summary>
    /// Why a quantity was refused, in the words the customer needs.
    /// </summary>
    /// <remarks>
    /// Names the two quantities that would work rather than only the rule.
    /// "Sold in pairs" leaves the reader to do the arithmetic on the number
    /// they already typed; "order 2 or 4" is the same sentence with the answer
    /// in it.
    /// </remarks>
    public static string Refusal(int quantity, int perPackage, string unit)
    {
        var (down, up) = NearestOrderable(quantity, perPackage);
        var choice = down is null ? $"{up}" : $"{down} or {up}";
        var plural = unit == DefaultUnit ? "packs" : $"{unit}s";

        return $"This part is sold in {plural} of {perPackage}, " +
               $"so {quantity} cannot be picked. Order {choice}.";
    }
}
