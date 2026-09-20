using System.Text.RegularExpressions;
using AutoPartsHub.Api.Data;
using AutoPartsHub.Domain.Catalogue;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsHub.Api.Catalogue;

/// <summary>
/// Recording what customers looked for and did not find.
/// </summary>
/// <remarks>
/// The catalogue can say what it holds; this says what people came looking for
/// and left without, which is the question that decides what to stock next.
///
/// One row per term, never per search — the same shape as the VIN log, for the
/// same two reasons: two customers searching the same thing would be two rows
/// saying one fact, and the counter gives the traffic-weighted answer while the
/// row count gives the distinct one.
///
/// It records no client, no session and no address.
/// </remarks>
public static partial class SearchMisses
{
    /// <summary>
    /// The longest term worth keeping.
    /// </summary>
    /// <remarks>
    /// A search box will accept whatever is on somebody's clipboard. Past a
    /// certain length what arrives is not a search — it is a paragraph, an
    /// address, a paste that went to the wrong field — and storing it would be
    /// storing that.
    /// </remarks>
    public const int MaxTerm = 100;

    /// <summary>An email address, the one personal detail a search box collects.</summary>
    [GeneratedRegex(@"[^\s@]+@[^\s@]+\.[^\s@]+")]
    private static partial Regex Email();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>
    /// The form a term is stored in, or null when it is not to be stored.
    /// </summary>
    /// <remarks>
    /// Lower-cased with its whitespace collapsed, so <c>Brake Pad</c>,
    /// <c>brake pad</c> and <c>brake  pad</c> are one row and not three.
    ///
    /// An <b>email address</b> is refused: it is not a part search, and it is
    /// the shape of a personal detail typed into the wrong box — which happens,
    /// because a search field is the first thing on the page.
    ///
    /// A <b>long run of digits</b> is NOT refused, though it is the shape of a
    /// phone number. It is equally the shape of a part number: <c>0986424815</c>
    /// is ten digits and is a Bosch filter. There is no rule separating the
    /// two, and one that dropped both would blind the report to exactly the
    /// searches it exists to catch — somebody typing the number off the old
    /// part. So the digits stay, and this is the note saying that was a
    /// decision rather than an oversight.
    /// </remarks>
    public static string? ReadTerm(string raw)
    {
        var collapsed = Whitespace().Replace(raw.Trim(), " ").ToLowerInvariant();

        if (collapsed.Length == 0) return null;
        if (collapsed.Length > MaxTerm) return null;
        if (Email().IsMatch(collapsed)) return null;

        return collapsed;
    }

    /// <summary>
    /// Writes down a search that found nothing.
    /// </summary>
    /// <remarks>
    /// NOTHING HERE MAY COST A CUSTOMER THEIR SEARCH. The same rule as the VIN
    /// log: this runs after the results are assembled and every failure inside
    /// it is swallowed. A measurement being taken is worth strictly less than
    /// the thing being measured working — a month of data with a gap in it
    /// still answers the question, and a search that 500s because a counter
    /// could not be incremented does not.
    /// </remarks>
    public static async Task RecordAsync(
        AutoPartsContext db, string q, bool narrowed, CancellationToken ct = default)
    {
        try
        {
            var term = ReadTerm(q);
            if (term is null) return;

            // MERGE with HOLDLOCK, which is SQL Server's ON CONFLICT. The lock
            // is what makes it one decision: without it two searches for the
            // same missing term can both find no row, both insert, and the
            // loser gets a unique-key violation instead of a counter going up.
            // A popular missing term is exactly the case where that happens.
            await db.Database.ExecuteSqlAsync($"""
                MERGE "SearchMiss" WITH (HOLDLOCK) AS target
                USING (VALUES ({term}, {narrowed})) AS source("term", "narrowed")
                  ON target."term" = source."term"
                 AND target."narrowed" = source."narrowed"
                WHEN MATCHED THEN
                  UPDATE SET "searches" = target."searches" + 1,
                             "lastSeenAt" = SYSUTCDATETIME()
                WHEN NOT MATCHED THEN
                  INSERT ("id", "term", "narrowed")
                  VALUES ({Ids.New()}, {term}, {narrowed});
                """, ct);
        }
        catch
        {
            // Deliberately silent. See the note above: this is a counter, and a
            // counter is not worth an error page.
        }
    }
}
