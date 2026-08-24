using System.Globalization;
using System.Text.RegularExpressions;

namespace AutoPartsHub.Api.Pricing;

/// <summary>
/// Reading the dates a markup rule is in force between. The mirror of
/// lib/markup-window.ts.
/// </summary>
/// <remarks>
/// A window is NOT the same as switching a rule off. <c>active</c> is somebody
/// deciding; a window starts a seasonal price and stops it again on its own,
/// with nobody having to remember the date. Which is why an open end reads as
/// "until further notice" rather than "expired", and why neither bound has a
/// default — a rule with no window is the normal case.
/// </remarks>
public static partial class MarkupWindow
{
    public readonly record struct Result(
        bool Ok, DateTime? StartsAt, DateTime? EndsAt, string? Error);

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$")]
    private static partial Regex BareDate();

    /// <summary>
    /// One bound, as UTC.
    /// </summary>
    /// <remarks>
    /// Accepts what a date input sends — <c>2026-07-01</c> — as well as a full
    /// timestamp. A bare date is read as midnight UTC rather than midnight
    /// wherever the server happens to be standing, so a rule written in Cairo
    /// and read by a server in Frankfurt starts at the same moment.
    /// </remarks>
    private static (DateTime? At, string? Error) ReadBound(string? raw)
    {
        var text = raw?.Trim() ?? "";
        if (text.Length == 0) return (null, null);

        if (BareDate().IsMatch(text)) text += "T00:00:00.000Z";

        if (!DateTimeOffset.TryParse(
                text, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            return (null, $"\"{raw?.Trim()}\" is not a date.");
        }

        return (parsed.UtcDateTime, null);
    }

    public static Result Read(string? startsAtRaw, string? endsAtRaw)
    {
        var (start, startError) = ReadBound(startsAtRaw);
        if (startError is not null) return new(false, null, null, $"Start date: {startError}");

        var (end, endError) = ReadBound(endsAtRaw);
        if (endError is not null) return new(false, null, null, $"End date: {endError}");

        // A window that ends before it starts can never apply, and is far more
        // likely to be two dates typed the wrong way round than a rule anybody
        // meant. The database refuses it too.
        if (start is not null && end is not null && start > end)
        {
            return new(false, null, null, "The rule ends before it starts.");
        }

        return new(true, start, end, null);
    }

    /// <summary>
    /// A stored timestamp as epoch milliseconds, which is what the engine
    /// compares.
    /// </summary>
    /// <remarks>
    /// The column is a bare TIMESTAMP, so the value carries no zone of its own
    /// and is UTC by the convention this schema keeps. Said explicitly here
    /// rather than left to whatever the machine's local zone happens to be.
    /// </remarks>
    public static double? ToEpochMs(DateTime? at) =>
        at is null
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind(at.Value, DateTimeKind.Utc))
                .ToUnixTimeMilliseconds();
}
