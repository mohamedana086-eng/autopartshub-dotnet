using System.Globalization;

namespace AutoPartsHub.Api.Catalogue;

/// <summary>
/// Turning <c>page</c> and <c>pageSize</c> from a query string into a slice.
/// </summary>
/// <remarks>
/// Its own class rather than a few lines in the endpoint, because the rules
/// are arithmetic on untrusted text and that is exactly the kind of thing
/// worth testing directly rather than through an HTTP round trip.
///
/// Every value arrives as text and any of it can be absent, negative,
/// fractional or a word. All of it is clamped rather than refused: these reach
/// the API from truncated urls, templates that rendered an empty variable and
/// hand-edited query strings, and none of those is worth an error page. What
/// the caller gets back is the page that was actually used, so a client can
/// tell it was adjusted.
///
/// Parsed as a double before flooring rather than as an int, so "7.9" lands on
/// 7 the way <c>Number()</c> then <c>Math.floor</c> does on the other side.
/// An int parse would fail on it and fall back to the default, which is a
/// different answer to the same request.
/// </remarks>
public static class Paging
{
    /// <summary>The floor and ceiling the system this replaces offered.</summary>
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 500;

    /// <summary>
    /// How many rows per page.
    /// </summary>
    /// <remarks>
    /// The maximum is the server's to enforce: a page size is a request for
    /// work, and one arriving as 100000 is either a mistake or an attempt to
    /// make the server do all of it at once.
    ///
    /// <paramref name="legacyLimit"/> is the name this had before there were
    /// pages. The search-as-you-type suggestions ask for six through it.
    /// </remarks>
    public static int ReadPageSize(string? raw, string? legacyLimit = null)
    {
        var text = raw is { Length: > 0 } ? raw : legacyLimit;

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
               && v > 0
            ? Math.Min((int)Math.Floor(v), MaxPageSize)
            : DefaultPageSize;
    }

    /// <summary>Which page, 1-based. Anything below one is a rounding error somewhere.</summary>
    public static int ReadPage(string? raw) =>
        double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 1
            ? (int)Math.Floor(v)
            : 1;

    /// <summary>
    /// The rows one page covers.
    /// </summary>
    /// <remarks>
    /// A page past the end is empty rather than clamped to the last one: the
    /// caller asked for something that is not there, and answering with a
    /// different page while echoing back the number they asked for would be
    /// the response disagreeing with itself.
    /// </remarks>
    public static IEnumerable<T> PageOf<T>(IEnumerable<T> rows, int page, int pageSize) =>
        rows.Skip((page - 1) * pageSize).Take(pageSize);

    /// <summary>Zero for no results, so "page 1 of 0" reads as the empty answer it is.</summary>
    public static int PageCount(int total, int pageSize) =>
        (int)Math.Ceiling((double)total / pageSize);
}
