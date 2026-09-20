using System.Globalization;
using System.Text.RegularExpressions;
using AutoPartsHub.Domain;

namespace AutoPartsHub.Domain.Orders;

/// <summary>What the order list was asked to narrow to.</summary>
/// <param name="Before">
/// Exclusive — a bare date at the end of a range has already been moved to the
/// following midnight.
/// </param>
/// <param name="ManagerId">
/// Whose customers' orders, when an admin narrows to one salesperson. Not the
/// same thing as the scoping that already narrows a salesperson to their own.
/// </param>
public record OrderFilter(string? Status, DateTime? From, DateTime? Before, string? ManagerId);

/// <summary>
/// Narrowing the order list: by status, by date, and by whose customer it is.
/// </summary>
/// <remarks>
/// Dates are the awkward half. A person filtering "to 27 August" means the
/// whole of the 27th, and a naive <c>createdAt &lt;= '2026-08-27'</c> reads as
/// midnight and silently hides that day's orders — the sort of bug that gets
/// noticed as "the report is missing yesterday" weeks later. So a bare date at
/// the end of a range becomes the start of the NEXT day and the comparison is
/// exclusive, which covers the day without depending on how many decimals a
/// timestamp has.
/// </remarks>
public static partial class OrderFilters
{
    /// <summary><c>YYYY-MM-DD</c>, which is what a date input sends.</summary>
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$")]
    private static partial Regex BareDate();

    /// <summary>
    /// Reads one end of the range.
    /// </summary>
    /// <remarks>
    /// A bare date is read as UTC midnight rather than through the server's
    /// local zone: the stored timestamps are UTC, and letting a machine's own
    /// zone decide which orders fall in August would make the same filter
    /// answer differently on two servers.
    /// </remarks>
    private static DateTime? ReadDate(string? raw)
    {
        var text = (raw ?? "").Trim();
        if (text.Length == 0) return null;

        var toParse = BareDate().IsMatch(text) ? $"{text}T00:00:00.000Z" : text;

        return DateTime.TryParse(
            toParse, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// Reads the four filter values, whatever they arrived in.
    /// </summary>
    /// <remarks>
    /// A lookup function rather than the request's own query collection. Every
    /// rule below — which statuses exist, that a bare date means UTC midnight,
    /// that an unreadable date is refused rather than ignored, that <c>to</c>
    /// is exclusive of the day after — is a rule about orders, and none of it
    /// is a rule about HTTP. Taking the collection would have pinned all of it
    /// to ASP.NET, which is the whole of why this file could not build in a
    /// layer that has no web framework in it.
    ///
    /// The caller passes <c>key =&gt; query[key]</c> and keeps the one line
    /// that knows where a query string lives.
    /// </remarks>
    public static Validated<OrderFilter> Read(Func<string, string?> value)
    {
        var status = (value("status") ?? "").Trim();
        if (status.Length > 0 && !OrderStatuses.IsKnown(status))
        {
            return Validation.Fail<OrderFilter>(
                $"Status must be one of: {string.Join(", ", OrderStatuses.All)}.");
        }

        var fromRaw = (value("from") ?? "").Trim();
        var toRaw = (value("to") ?? "").Trim();
        var from = ReadDate(fromRaw);
        var to = ReadDate(toRaw);

        // An unparseable date is refused rather than ignored. Dropping it would
        // answer a narrower question than the one asked, with nothing on screen
        // to say so — and a filter that silently does not apply is worse than
        // one that fails, because the numbers still look plausible.
        if (fromRaw.Length > 0 && from is null)
        {
            return Validation.Fail<OrderFilter>("The `from` date could not be read. Use YYYY-MM-DD.");
        }
        if (toRaw.Length > 0 && to is null)
        {
            return Validation.Fail<OrderFilter>("The `to` date could not be read. Use YYYY-MM-DD.");
        }

        var before = to is null ? null
            : BareDate().IsMatch(toRaw) ? to.Value.AddDays(1)
            : to;

        if (from is not null && before is not null && before <= from)
        {
            return Validation.Fail<OrderFilter>("The `to` date is not after the `from` date.");
        }

        var managerId = (value("managerId") ?? "").Trim();

        return Validation.Ok(new OrderFilter(
            status.Length > 0 ? status : null,
            from,
            before,
            managerId.Length > 0 ? managerId : null));
    }
}
