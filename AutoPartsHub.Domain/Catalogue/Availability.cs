namespace AutoPartsHub.Domain.Catalogue;

/// <summary>
/// Whether a part can be sold from stock.
/// </summary>
/// <remarks>
/// One distinction carries the whole thing: never counted is not the same as
/// counted and gone. The queries keep them apart — <c>SUM</c> over no shelves
/// is null, and null travels all the way out to the catalogue's responses so
/// an admin can tell an unfilled record from an empty shelf.
///
/// Nothing a customer can do turns on which it is, and that collapse happens
/// here and nowhere else. Reading uncounted as "sell on the lead time" instead
/// would put the entire catalogue on sale, which is what this shape exists to
/// prevent.
/// </remarks>
public static class Availability
{
    public static int Sellable(int? available) => available ?? 0;
}

/// <summary>Ids generated the way the rest of the data already is.</summary>
/// <remarks>
/// The database has no default on any id column: every row's id has always
/// been produced by the application, and every existing one is a cuid. New
/// rows keep the same shape so a mixed table does not appear.
/// </remarks>
public static class Ids
{
    private static int _counter = Random.Shared.Next(0, 1 << 24);

    /// <summary>
    /// A cuid: 'c', a base-36 timestamp, a counter, and randomness.
    /// </summary>
    /// <remarks>
    /// Sortable by creation time, which several queries rely on — order lines
    /// come back in the order they were written, and the markup rules are read
    /// in id order so a tie between two rules resolves the same way twice
    /// running.
    /// </remarks>
    public static string New()
    {
        var timestamp = Base36(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var counter = Base36(Interlocked.Increment(ref _counter) & 0xFFFFFF).PadLeft(4, '0');
        var random = Base36(Random.Shared.NextInt64(0, 0xFFFFFFFFFFFF)).PadLeft(8, '0');

        return $"c{timestamp}{counter[^4..]}{random[^8..]}";
    }

    private static string Base36(long value)
    {
        const string Alphabet = "0123456789abcdefghijklmnopqrstuvwxyz";
        if (value == 0) return "0";

        var chars = new Stack<char>();
        while (value > 0)
        {
            chars.Push(Alphabet[(int)(value % 36)]);
            value /= 36;
        }
        return new string(chars.ToArray());
    }
}
