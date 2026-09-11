using AutoPartsHub.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsHub.Api.Catalogue;

/// <summary>
/// Whether this database can match product names by word, and what to ask it.
/// </summary>
/// <remarks>
/// Full-Text Search is a separate component of SQL Server rather than part of
/// the engine, and an instance can be perfectly healthy without it — LocalDB,
/// which is what this repository's tests run against, cannot have it at all.
/// So "is there a full-text index on Product" is a question about the
/// deployment, not about the code, and it has to be asked rather than assumed.
/// A <c>CONTAINSTABLE</c> against an instance without one does not degrade; it
/// fails the statement outright.
///
/// Asked once and remembered, because the answer only changes when somebody
/// installs a component or runs a migration, and neither happens under a
/// running process. A probe that FAILS is not remembered: a database that was
/// briefly unreachable would otherwise leave every later search in the
/// degraded lane until the next deployment, which is a bug that looks like
/// nothing.
///
/// Both lanes seek. The difference is reach, not cost — full text matches a
/// word anywhere in a name, and without it a name is only reachable from its
/// start.
/// </remarks>
public sealed class FullTextSearch(IServiceScopeFactory scopes)
{
    /// <summary>Null until something has successfully asked.</summary>
    private bool? indexed;

    /// <summary>
    /// The most words taken from one query.
    /// </summary>
    /// <remarks>
    /// A near-miss search runs on whatever a customer typed, and that is
    /// sometimes a pasted line from a quotation. Six prefix terms is already
    /// more than a person types; the cap is there so the size of the search
    /// condition is not caller input.
    /// </remarks>
    private const int MostTerms = 6;

    public async Task<bool> IsIndexedAsync(CancellationToken ct = default)
    {
        if (indexed is { } settled) return settled;

        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AutoPartsContext>();

            // Both halves matter. The component can be installed on an
            // instance whose database has never had the migration applied,
            // and the index can exist on an instance where the service is
            // stopped — in both cases CONTAINSTABLE is not available.
            var found = await db.Database.SqlQuery<int>($"""
                SELECT CASE
                         WHEN SERVERPROPERTY('IsFullTextInstalled') = 1
                          AND EXISTS (SELECT 1 FROM sys.fulltext_indexes
                                      WHERE object_id = OBJECT_ID('Product'))
                         THEN 1 ELSE 0
                       END AS "Value"
                """).FirstAsync(ct);

            return (indexed = found == 1).Value;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Deliberately not remembered — see the remarks above. The search
            // still answers, from the lane that needs no index.
            return false;
        }
    }

    /// <summary>
    /// A <c>CONTAINS</c> search condition matching any word of the query as a
    /// prefix, or null when the query has no word worth asking about.
    /// </summary>
    /// <remarks>
    /// Prefix terms rather than <c>FREETEXT</c>: this runs only when nothing
    /// matched as typed, so the query is already known to be wrong somehow,
    /// and the way a part name is usually typed wrong is short — "brak pad",
    /// "oil filt". <c>"brak*"</c> reaches "Brake pad set, front" and FREETEXT
    /// does not, because stemming relates forms of a word that was spelled
    /// correctly.
    ///
    /// OR rather than AND, for the same reason: one of the words being wrong
    /// past its prefix is the case this exists for, and AND would drop the
    /// result that the other word found. Ranking puts the rows that matched
    /// more of the query first.
    ///
    /// The condition is a parameter, not interpolated text — but it still has
    /// to be valid full-text syntax, so everything that is not a letter or a
    /// digit is dropped on the way in. That leaves the quotes this method adds
    /// as the only quotes in the string, which is the property the escaping
    /// depends on and the reason it is tested.
    /// </remarks>
    public static string? TermsFor(string query)
    {
        var words = query
            .Split(w => !char.IsLetterOrDigit(w))
            .Where(word => word.Length >= 2)
            .Take(MostTerms)
            .ToArray();

        return words.Length == 0 ? null : string.Join(" OR ", words.Select(w => $"\"{w}*\""));
    }
}

/// <summary>Splitting a string on a predicate, which string.Split does not do.</summary>
internal static class WordSplit
{
    public static IEnumerable<string> Split(this string value, Func<char, bool> isSeparator)
    {
        var start = -1;

        for (var i = 0; i <= value.Length; i++)
        {
            var separator = i == value.Length || isSeparator(value[i]);

            if (separator && start >= 0)
            {
                yield return value[start..i];
                start = -1;
            }
            else if (!separator && start < 0)
            {
                start = i;
            }
        }
    }
}
