using System.Text.Json;

namespace AutoPartsHub.Api.Data;

/// <summary>
/// Passing a list of values to SQL Server as one parameter.
/// </summary>
/// <remarks>
/// PostgreSQL has an array type and Npgsql binds a C# array straight to it, so
/// <c>"id" = ANY({ids}::text[])</c> is one parameter holding many values. SQL
/// Server has no array type and no array parameter, and the usual answers are
/// all worse than they look:
///
/// <list type="bullet">
///   <item>
///     Splicing the values into the SQL is string-built SQL, which is the one
///     thing every query in this application is written to avoid.
///   </item>
///   <item>
///     One parameter per value makes the statement's <em>shape</em> depend on
///     how many there are, so the plan cache fills with a plan per list length
///     — and the bulk lookup takes two thousand of them.
///   </item>
///   <item>
///     <c>STRING_SPLIT</c> over a joined string needs a delimiter no value can
///     contain. These are part numbers typed by customers.
///   </item>
/// </list>
///
/// So: JSON in one <c>nvarchar</c> parameter, read back with <c>OPENJSON</c>.
/// One parameter, one plan whatever the length, and no delimiter to collide
/// with because JSON escapes its own.
///
/// <code>"id" IN (SELECT value FROM OPENJSON({SqlList.Of(ids)}))</code>
///
/// <c>OPENJSON</c>'s <c>value</c> column is <c>nvarchar(4000)</c>, which
/// compares to the text columns these lists are matched against without a
/// cast. A list of numbers matched against a numeric column would need one.
/// </remarks>
public static class SqlList
{
    /// <summary>
    /// The list as a JSON array, or null when there is no list.
    /// </summary>
    /// <remarks>
    /// Null in, null out — deliberately, and not the JSON literal
    /// <c>"null"</c> that <see cref="JsonSerializer"/> would produce. Several
    /// of these statements switch a filter off with
    /// <c>({list} IS NULL OR …)</c>, and a four-character string is not null.
    /// </remarks>
    public static string? Of(IEnumerable<string>? values) =>
        values is null ? null : JsonSerializer.Serialize(values);

    /// <inheritdoc cref="Of(IEnumerable{string})"/>
    public static string? Of(IEnumerable<int>? values) =>
        values is null ? null : JsonSerializer.Serialize(values);
}
