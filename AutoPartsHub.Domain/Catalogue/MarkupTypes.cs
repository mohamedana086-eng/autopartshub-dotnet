namespace AutoPartsHub.Domain.Catalogue;

/// <summary>
/// The kinds of markup, as the database stores them.
/// </summary>
/// <remarks>
/// Exported because the validator, the category form and the database's own
/// check constraint all have to agree, and three copies of a vocabulary drift
/// the day somebody adds a fourth. PERCENT_MIN is that fourth, and they did
/// not drift.
/// </remarks>
public static class MarkupTypes
{
    public static readonly string[] All = ["PERCENT", "AMOUNT", "FIXED", "PERCENT_MIN"];
}
