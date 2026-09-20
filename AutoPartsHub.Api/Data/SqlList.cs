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

    /// <summary>
    /// A set of rows as a JSON array of objects, for <c>OPENJSON … WITH</c>.
    /// </summary>
    /// <remarks>
    /// The bulk inserts used PostgreSQL's <c>unnest(a, b, c, …)</c>, which
    /// zips several equal-length arrays back into rows. It is a neat trick and
    /// it has no SQL Server equivalent — and it was always one slip away from
    /// disaster, because nothing checks that the arrays are the same length or
    /// that they are listed in the same order as the columns. A row of objects
    /// cannot be misaligned: each value carries its own name.
    ///
    /// The reading side names the columns and their types:
    ///
    /// <code>
    /// SELECT … FROM OPENJSON({SqlList.Rows(rows)})
    /// WITH ("id" nvarchar(400) '$.id', "quantity" int '$.quantity')
    /// </code>
    ///
    /// Property names are written as-is rather than camel-cased by a policy,
    /// so what the C# object calls a field is what the <c>$.path</c> asks for
    /// and the two can be read side by side.
    /// </remarks>
    public static string Rows<T>(IEnumerable<T> rows) =>
        JsonSerializer.Serialize(rows, RowOptions);

    private static readonly JsonSerializerOptions RowOptions = new()
    {
        // Dates as ISO-8601, which is what OPENJSON parses into datetime2.
        // The default already does this; it is named because the alternative
        // would break every bulk insert at once and silently.
        PropertyNamingPolicy = null,
    };
}
