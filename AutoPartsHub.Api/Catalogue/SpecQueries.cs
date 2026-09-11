using AutoPartsHub.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsHub.Api.Catalogue;

/// <summary>
/// The technical specifications of a part.
/// </summary>
/// <remarks>
/// Two callers want different amounts of the same thing: a search result row
/// has room for three, and the part's own page shows all of them. So this is
/// one query with a limit rather than two queries, and the limit is applied
/// per part rather than to the result — <c>LIMIT 3</c> over a batch of fifty
/// parts returns three specifications, not three each.
///
/// Read through raw SQL rather than through a scaffolded entity, the same way
/// the search reads. <c>ProductSpec</c> is written only by the Node importer
/// and the seed; nothing here inserts one, so there is nothing for a tracked
/// entity to do that this does not.
/// </remarks>
public sealed class SpecQueries(AutoPartsContext db)
{
    /// <summary>What fits on a result row beside the price and the delivery time.</summary>
    public const int RowSpecs = 3;

    /// <summary>
    /// Specifications for a batch of parts, grouped by part and in display order.
    /// </summary>
    /// <param name="perProduct">Null for all of them.</param>
    /// <remarks>
    /// Grouped here rather than joined onto the product query: a part with six
    /// specifications would multiply into six rows and have to be folded back
    /// together anyway, which the search already learned with images and
    /// cross-references.
    /// </remarks>
    public async Task<Dictionary<string, List<Spec>>> ForAsync(
        IReadOnlyList<string> ids,
        int? perProduct = null,
        CancellationToken ct = default)
    {
        var grouped = new Dictionary<string, List<Spec>>();
        if (ids.Count == 0) return grouped;

        var array = ids.ToArray();

        var rows = await db.Database.SqlQuery<SpecRow>($"""
            SELECT "productId" AS "ProductId", "label" AS "Label",
                   "value" AS "Value", "unit" AS "Unit"
            FROM (
              SELECT s."productId", s."label", s."value", s."unit",
                     -- Numbered per part, which is what makes "the first three
                     -- of each" a limit the database can apply. Done any other
                     -- way this is one query per part on a page of fifty.
                     --
                     -- The id breaks a tie on sortOrder. Two specifications
                     -- given the same position is a data question nobody has
                     -- answered, and without this the pair would swap places
                     -- between requests — which on a row showing exactly three
                     -- means a specification appearing and disappearing as you
                     -- page.
                     row_number() OVER (
                       PARTITION BY s."productId"
                       ORDER BY s."sortOrder" ASC, s."id" ASC
                     ) AS rn
              FROM "ProductSpec" s
              WHERE s."productId" IN (SELECT value COLLATE DATABASE_DEFAULT FROM OPENJSON({SqlList.Of(array)}))
            ) ranked
            WHERE ({perProduct} IS NULL OR rn <= {perProduct})
            ORDER BY "productId" ASC, rn ASC
            """).ToListAsync(ct);

        // The part's id is the key, so it does not also need to be on every
        // specification underneath it.
        foreach (var row in rows)
        {
            if (!grouped.TryGetValue(row.ProductId, out var list))
            {
                list = [];
                grouped[row.ProductId] = list;
            }
            list.Add(new Spec(row.Label, row.Value, row.Unit));
        }

        return grouped;
    }

    /// <summary>How many each part has, whatever was fetched — so a row can say "+4 more".</summary>
    public async Task<Dictionary<string, int>> CountsAsync(
        IReadOnlyList<string> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return [];

        var array = ids.ToArray();

        var rows = await db.Database.SqlQuery<SpecCountRow>($"""
            SELECT "productId" AS "ProductId", COUNT(*) AS "Count"
            FROM "ProductSpec"
            WHERE "productId" IN (SELECT value COLLATE DATABASE_DEFAULT FROM OPENJSON({SqlList.Of(array)}))
            GROUP BY "productId"
            """).ToListAsync(ct);

        return rows.ToDictionary(r => r.ProductId, r => r.Count);
    }
}

/// <summary>One specification, as it goes out on the wire.</summary>
/// <param name="Unit">Null where the value is not a measurement — "Front axle", "Yes".</param>
public record Spec(string Label, string Value, string? Unit);

/// <summary>The same, still carrying the part it belongs to, for grouping.</summary>
public record SpecRow(string ProductId, string Label, string Value, string? Unit);

public record SpecCountRow(string ProductId, int Count);
