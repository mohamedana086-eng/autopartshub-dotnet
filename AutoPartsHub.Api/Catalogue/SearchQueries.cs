using AutoPartsHub.Api.Data;
using AutoPartsHub.Api.Pricing;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsHub.Api.Catalogue;

/// <summary>
/// The catalogue search, as far as the database is concerned.
/// </summary>
/// <remarks>
/// Ranking, facet counts and every filter that depends on a resolved price
/// stay in the endpoint: they are decided per caller, after the tier has
/// priced each row, and no amount of SQL would move them here honestly. What
/// the database answers is narrower — which parts are reachable at all.
///
/// The filters that do belong here are written so a null parameter turns its
/// own clause off:
/// <code>AND ({supplier}::text IS NULL OR s."slug" = {supplier})</code>
/// Every combination is therefore the same statement with the same number of
/// parameters, which is what lets a search with six optional filters stay one
/// interpolated-string query — no SQL assembled from strings, and no path
/// where a value could be spliced instead of bound. EF's FormattableString
/// overloads bind every hole, so this reads like concatenation and is not.
///
/// Which is also why the SELECT is written out twice below instead of being
/// shared in a constant. EF binds EVERY hole, including one holding SQL: a
/// {SelectClause} became a parameter and Postgres answered "syntax error at
/// or near $1". The safety and the inconvenience are the same property, and
/// the fix is duplication rather than a way around it. If the two drift, the
/// fuzzy fallback returns different columns from the exact match, which the
/// comparison harness sees.
/// </remarks>
public sealed class SearchQueries(AutoPartsContext db)
{
    /// <summary>How many rows the query will consider before the endpoint narrows them.</summary>
    public const int MaxResults = 200;

    /// <summary>
    /// A token list that cannot match anything.
    /// </summary>
    /// <remarks>
    /// <c>NOT EXISTS (SELECT … WHERE NOT …)</c> over an empty set is vacuously
    /// true, so an empty array would quietly return the whole catalogue for a
    /// query that found nothing. The query branch is guarded by
    /// <c>hasQuery</c> anyway; this makes the failure impossible rather than
    /// merely unreachable.
    /// </remarks>
    private static readonly string[] NeverMatches = ["no-token-can-contain-this-sentinel"];

    /// <summary>Parts matching the query and every filter a column can decide.</summary>
    public async Task<SearchPage> SearchAsync(
        IReadOnlyList<string> queryTokens,
        IReadOnlyList<string> normalisedIds,
        string? variant,
        string? supplier,
        RowFilters rows,
        int limit,
        CancellationToken ct = default)
    {
        var hasQuery = queryTokens.Count > 0 || normalisedIds.Count > 0;
        var tokens = queryTokens.Count > 0 ? queryTokens.ToArray() : NeverMatches;
        var ids = normalisedIds.ToArray();

        var system = rows.System;
        var manufacturer = rows.Manufacturer;
        var minRating = rows.MinRating;
        var reliability = rows.Reliability;
        var returnsOnly = rows.ReturnsOnly;
        // An empty selection means every kind, which the null-cancelling shape
        // below expresses as null rather than as a list of all three — the two
        // are the same answer, and only one of them stays right when a fourth
        // kind is added.
        var partType = rows.PartType is { Length: > 0 } chosen ? chosen : null;

        var counted = await db.Database.SqlQuery<CountedSearchRow>($"""
            SELECT COUNT(*) OVER () ::int AS "Total",
                   p."id" AS "Id", p."partNumber" AS "PartNumber", p."name" AS "Name",
                   p."description" AS "Description", p."stockDays" AS "StockDays",
                   p."basePrice" AS "BasePrice", p."supplierId" AS "SupplierId",
                   p."partType" AS "PartType",
                   p."packagingUnit" AS "PackagingUnit",
                   p."quantityPerPackage" AS "QuantityPerPackage",
                   p."goodsCategoryId" AS "GoodsCategoryId",
                   p."weightGrams" AS "WeightGrams",
                   m."name" AS "ManufacturerName",
                   v."name" AS "SystemName", v."slug" AS "SystemSlug",
                   pli."price" AS "ListPrice",
                   img."url" AS "ImageUrl", img."alt" AS "ImageAlt",
                   st."available" AS "Available",
                   s."slug" AS "SupplierSlug", s."name" AS "SupplierName", s."rating" AS "SupplierRating",
                   s."reliability" AS "SupplierReliability", s."acceptsReturns" AS "SupplierAcceptsReturns"
            FROM "Product" p
            JOIN "Manufacturer" m ON m."id" = p."manufacturerId"
            JOIN "VehicleSystem" v ON v."id" = p."vehicleSystemId"
            LEFT JOIN "Supplier" s ON s."id" = p."supplierId"
            LEFT JOIN "PriceListItem" pli
              ON pli."productId" = p."id"
             AND pli."priceListId" = (SELECT "id" FROM "PriceList" WHERE "active" LIMIT 1)
            LEFT JOIN LATERAL (
              SELECT pi."url", pi."alt"
              FROM "ProductImage" pi
              WHERE pi."productId" = p."id"
              ORDER BY pi."sortOrder" ASC
              LIMIT 1
            ) img ON true
            LEFT JOIN LATERAL (
              SELECT SUM(sl."quantity" - sl."reserved")::int AS "available"
              FROM "StockLevel" sl
              JOIN "Warehouse" w ON w."id" = sl."warehouseId"
              WHERE sl."productId" = p."id" AND w."active" = true
            ) st ON true
            WHERE (
              -- No query: everything is reachable, and the filters below do the work.
              {hasQuery}::bool IS NOT TRUE
              OR p."id" = ANY({ids}::text[])
              -- Every token has to land somewhere, but not all in the same
              -- column — which is what lets "bosch brake pad" work, with the
              -- brand on one and the rest on another. Written as "no token
              -- fails to match", so the number of tokens is a value rather
              -- than a shape.
              OR NOT EXISTS (
                SELECT 1 FROM unnest({tokens}::text[]) AS tok
                WHERE NOT (
                  p."partNumber" ILIKE '%' || tok || '%'
                  OR p."name" ILIKE '%' || tok || '%'
                  OR COALESCE(p."description", '') ILIKE '%' || tok || '%'
                  OR m."name" ILIKE '%' || tok || '%'
                  OR EXISTS (
                    SELECT 1 FROM "Interchange" i
                    WHERE i."sourceId" = p."id" AND i."targetPartNo" ILIKE '%' || tok || '%'
                  )
                )
              )
            )
            AND ({variant}::text IS NULL OR EXISTS (
              SELECT 1 FROM "Fitment" fit
              WHERE fit."productId" = p."id" AND fit."variantId" = {variant}
            ))
            AND ({supplier}::text IS NULL OR s."slug" = {supplier})
            -- A supplier who is switched off is not selling, so their parts
            -- leave the catalogue entirely. That is what lets one sign up,
            -- load a whole range and price it, and have none of it on sale
            -- until an admin approves them. Parts with no supplier at all are
            -- the catalogue's own and stay.
            AND (p."supplierId" IS NULL OR s."active")
            -- The row filters. Each cancels itself when its parameter is null,
            -- so every combination is the same statement with the same holes.
            AND ({system}::text IS NULL OR v."slug" = {system})
            AND ({manufacturer}::text IS NULL OR lower(m."name") = lower({manufacturer}))
            -- COALESCE rather than a bare comparison: an unrated supplier is
            -- NULL, and NULL >= 4 is null, which drops the row for a reason
            -- nobody reading it could name. Written this way the rule is
            -- legible — unrated counts as zero, so no minimum includes it.
            AND ({minRating}::int IS NULL OR COALESCE(s."rating", 0) >= {minRating})
            AND ({reliability}::text IS NULL OR s."reliability" = {reliability})
            -- Only an explicit yes. A supplier whose return terms are
            -- unrecorded is not evidence that they accept them, and IS TRUE
            -- says so where a plain equality would leave a null to argue over.
            AND ({returnsOnly}::bool IS NOT TRUE OR s."acceptsReturns" IS TRUE)
            AND ({partType}::text[] IS NULL OR p."partType" = ANY({partType}::text[]))
            LIMIT {limit}
            """).ToListAsync(ct);

        return new SearchPage(
            counted.Select(c => c.ToRow()).ToList(),
            // No rows means no total to read one off. The window function has
            // nothing to attach to, which is not the same as it saying zero.
            counted.Count > 0 ? counted[0].Total : 0);
    }

    /// <summary>
    /// Every facet, in one round trip.
    /// </summary>
    /// <remarks>
    /// Six tallies over two populations: the systems describe everything the
    /// query matched, and the rest describe what is in the chosen system. So
    /// the query is two CTEs and a UNION rather than six statements — each of
    /// which would have re-run the match, and the match is the expensive half.
    ///
    /// Deliberately NOT narrowed by the brand, rating, reliability, returns or
    /// type filters. Each of those facets has to say what picking it would
    /// leave, and a count already narrowed by its own filter cannot: it would
    /// report "BOSCH 9" when BOSCH is selected and 9 whatever else is true.
    /// </remarks>
    public async Task<List<FacetCount>> FacetsAsync(
        IReadOnlyList<string> queryTokens,
        IReadOnlyList<string> normalisedIds,
        string? variant,
        string? supplier,
        string? system,
        CancellationToken ct = default)
    {
        var hasQuery = queryTokens.Count > 0 || normalisedIds.Count > 0;
        var tokens = queryTokens.Count > 0 ? queryTokens.ToArray() : NeverMatches;
        var ids = normalisedIds.ToArray();

        return await db.Database.SqlQuery<FacetCount>($"""
            WITH matched AS (
              SELECT p."id", p."partType",
                     m."name" AS "manufacturerName",
                     v."slug" AS "systemSlug", v."name" AS "systemName",
                     s."rating" AS "supplierRating",
                     s."reliability" AS "supplierReliability",
                     s."acceptsReturns" AS "supplierAcceptsReturns"
              FROM "Product" p
              JOIN "Manufacturer" m ON m."id" = p."manufacturerId"
              JOIN "VehicleSystem" v ON v."id" = p."vehicleSystemId"
              LEFT JOIN "Supplier" s ON s."id" = p."supplierId"
              WHERE (
                {hasQuery}::bool IS NOT TRUE
                OR p."id" = ANY({ids}::text[])
                OR NOT EXISTS (
                  SELECT 1 FROM unnest({tokens}::text[]) AS tok
                  WHERE NOT (
                    p."partNumber" ILIKE '%' || tok || '%'
                    OR p."name" ILIKE '%' || tok || '%'
                    OR COALESCE(p."description", '') ILIKE '%' || tok || '%'
                    OR m."name" ILIKE '%' || tok || '%'
                    OR EXISTS (
                      SELECT 1 FROM "Interchange" i
                      WHERE i."sourceId" = p."id" AND i."targetPartNo" ILIKE '%' || tok || '%'
                    )
                  )
                )
              )
              AND ({variant}::text IS NULL OR EXISTS (
                SELECT 1 FROM "Fitment" fit
                WHERE fit."productId" = p."id" AND fit."variantId" = {variant}
              ))
              AND ({supplier}::text IS NULL OR s."slug" = {supplier})
              AND (p."supplierId" IS NULL OR s."active")
            ),
            in_system AS (
              SELECT * FROM matched WHERE ({system}::text IS NULL OR "systemSlug" = {system})
            )
            SELECT 'system' AS "Kind", "systemSlug" AS "Key", "systemName" AS "Label",
                   COUNT(*)::int AS "Count"
            FROM matched GROUP BY "systemSlug", "systemName"
            UNION ALL
            SELECT 'brand', "manufacturerName", NULL, COUNT(*)::int FROM in_system
            GROUP BY "manufacturerName"
            UNION ALL
            -- Per exact rating rather than per threshold, so a caller can build
            -- whichever thresholds it offers by summing downwards. Key 0 is
            -- unrated, kept visible so the gap is obvious rather than dropped.
            SELECT 'rating', COALESCE("supplierRating", 0)::text, NULL, COUNT(*)::int FROM in_system
            GROUP BY COALESCE("supplierRating", 0)
            UNION ALL
            SELECT 'reliability', "supplierReliability", NULL, COUNT(*)::int FROM in_system
            WHERE "supplierReliability" IS NOT NULL GROUP BY "supplierReliability"
            UNION ALL
            -- Counted only among parts that have a supplier at all, matching
            -- the reliability tally beside it: a part with nobody behind it is
            -- not evidence either way about returns.
            SELECT 'returns', 'yes', NULL, COUNT(*)::int FROM in_system
            WHERE "supplierReliability" IS NOT NULL AND "supplierAcceptsReturns" IS TRUE
            UNION ALL
            SELECT 'partType', "partType", NULL, COUNT(*)::int FROM in_system GROUP BY "partType"
            """).ToListAsync(ct);
    }

    /// <summary>The same rows, by id — how the fuzzy fallback re-reads its matches.</summary>
    public async Task<List<SearchRow>> ByIdsAsync(
        IReadOnlyList<string> ids, string? variant, string? supplier, CancellationToken ct = default)
    {
        if (ids.Count == 0) return [];
        var array = ids.ToArray();

        return await db.Database.SqlQuery<SearchRow>($"""
            SELECT p."id" AS "Id", p."partNumber" AS "PartNumber", p."name" AS "Name",
                   p."description" AS "Description", p."stockDays" AS "StockDays",
                   p."basePrice" AS "BasePrice", p."supplierId" AS "SupplierId",
                   p."partType" AS "PartType",
                   p."packagingUnit" AS "PackagingUnit",
                   p."quantityPerPackage" AS "QuantityPerPackage",
                   p."goodsCategoryId" AS "GoodsCategoryId",
                   p."weightGrams" AS "WeightGrams",
                   m."name" AS "ManufacturerName",
                   v."name" AS "SystemName", v."slug" AS "SystemSlug",
                   pli."price" AS "ListPrice",
                   img."url" AS "ImageUrl", img."alt" AS "ImageAlt",
                   st."available" AS "Available",
                   s."slug" AS "SupplierSlug", s."name" AS "SupplierName", s."rating" AS "SupplierRating",
                   s."reliability" AS "SupplierReliability", s."acceptsReturns" AS "SupplierAcceptsReturns"
            FROM "Product" p
            JOIN "Manufacturer" m ON m."id" = p."manufacturerId"
            JOIN "VehicleSystem" v ON v."id" = p."vehicleSystemId"
            LEFT JOIN "Supplier" s ON s."id" = p."supplierId"
            LEFT JOIN "PriceListItem" pli
              ON pli."productId" = p."id"
             AND pli."priceListId" = (SELECT "id" FROM "PriceList" WHERE "active" LIMIT 1)
            LEFT JOIN LATERAL (
              SELECT pi."url", pi."alt"
              FROM "ProductImage" pi
              WHERE pi."productId" = p."id"
              ORDER BY pi."sortOrder" ASC
              LIMIT 1
            ) img ON true
            LEFT JOIN LATERAL (
              SELECT SUM(sl."quantity" - sl."reserved")::int AS "available"
              FROM "StockLevel" sl
              JOIN "Warehouse" w ON w."id" = sl."warehouseId"
              WHERE sl."productId" = p."id" AND w."active" = true
            ) st ON true
            WHERE p."id" = ANY({array}::text[])
            AND ({variant}::text IS NULL OR EXISTS (
              SELECT 1 FROM "Fitment" fit
              WHERE fit."productId" = p."id" AND fit."variantId" = {variant}
            ))
            AND ({supplier}::text IS NULL OR s."slug" = {supplier})
            -- Same rule as the search above. Reached by id rather than by
            -- matching, which is exactly why it has to be repeated: a fuzzy
            -- match that skipped the check would be a way round it.
            AND (p."supplierId" IS NULL OR s."active")
            """).ToListAsync(ct);
    }

    /// <summary>Cross-references for a set of parts, for the caller to group.</summary>
    public async Task<List<InterchangeRow>> InterchangesForAsync(
        IReadOnlyList<string> productIds, CancellationToken ct = default)
    {
        if (productIds.Count == 0) return [];
        var array = productIds.ToArray();

        return await db.Database.SqlQuery<InterchangeRow>($"""
            SELECT "sourceId" AS "SourceId", "targetPartNo" AS "TargetPartNo",
                   "targetManufacturer" AS "TargetManufacturer", "isOEM" AS "IsOem"
            FROM "Interchange"
            WHERE "sourceId" = ANY({array}::text[])
            """).ToListAsync(ct);
    }

    /// <summary>
    /// Product ids whose part number, or one of their cross-references,
    /// matches the query once separators are ignored.
    /// </summary>
    /// <remarks>
    /// Done in SQL because the normalised form is not stored. That means a
    /// scan: fine for a catalogue this size, but if it grows this wants a
    /// normalised column with an index on it rather than regexp_replace per
    /// row.
    /// </remarks>
    public async Task<List<string>> IdsMatchingNormalisedPartNumberAsync(
        string q, CancellationToken ct = default)
    {
        var needle = PartNumbers.Normalise(q);
        if (needle.Length < 3) return [];
        var pattern = $"%{needle}%";

        return await db.Database.SqlQuery<string>($"""
            SELECT DISTINCT p."id" AS "Value"
            FROM "Product" p
            LEFT JOIN "Interchange" i ON i."sourceId" = p."id"
            WHERE regexp_replace(upper(p."partNumber"), '[^A-Z0-9]', '', 'g') LIKE {pattern}
               OR regexp_replace(upper(i."targetPartNo"), '[^A-Z0-9]', '', 'g') LIKE {pattern}
            """).ToListAsync(ct);
    }

    /// <summary>Below this a trigram match is more noise than help — tuned
    /// against the catalogue, where a genuine typo scores about 0.6 and up.</summary>
    private const double FuzzyThreshold = 0.45;

    /// <summary>
    /// Closest products to a query that matched nothing exactly, ordered by how
    /// close they are.
    /// </summary>
    /// <remarks>
    /// <c>word_similarity</c> compares the query against the best-matching run
    /// of words in the target rather than the whole string, so "brak pad"
    /// still scores against "Brake pad set, front" without the rest of the
    /// name dragging it down. Needs pg_trgm — see the trigram migration.
    /// </remarks>
    public async Task<List<string>> IdsByFuzzyMatchAsync(string q, CancellationToken ct = default)
    {
        var needle = q.Trim().ToLowerInvariant();
        if (needle.Length < 3) return [];

        return await db.Database.SqlQuery<string>($"""
            SELECT p."id" AS "Value",
                   GREATEST(
                     word_similarity({needle}, lower(p."name")),
                     word_similarity({needle}, lower(m."name")),
                     similarity(lower(p."partNumber"), {needle})
                   ) AS score
            FROM "Product" p
            JOIN "Manufacturer" m ON m."id" = p."manufacturerId"
            WHERE GREATEST(
                    word_similarity({needle}, lower(p."name")),
                    word_similarity({needle}, lower(m."name")),
                    similarity(lower(p."partNumber"), {needle})
                  ) >= {FuzzyThreshold}
            -- Part number breaks the remaining tie. Two parts can score the
            -- same AND be called the same thing — this catalogue has two
            -- "Brake pad set, front" that tie at 0.5 on "brak pd" — and
            -- without this they come back in whatever order the heap holds
            -- them, which changes when unrelated rows are rewritten. The
            -- ranked search already breaks ties this way; the fuzzy fallback
            -- was missed.
            ORDER BY score DESC, p."name" ASC, p."partNumber" ASC
            LIMIT 25
            """).ToListAsync(ct);
    }

    public Task<string?> SystemNameBySlugAsync(string slug, CancellationToken ct = default) =>
        db.VehicleSystems.Where(v => v.Slug == slug).Select(v => v.Name).FirstOrDefaultAsync(ct);

    public Task<string?> SupplierNameBySlugAsync(string slug, CancellationToken ct = default) =>
        db.Suppliers.Where(s => s.Slug == slug).Select(s => s.Name).FirstOrDefaultAsync(ct);

    /// <summary>"BMW 3 Series (E90) 320d 2.0" — what the vehicle filter shows it is doing.</summary>
    public async Task<string?> VariantLabelAsync(string variantId, CancellationToken ct = default) =>
        (await db.Database.SqlQuery<string>($"""
            SELECT mk."name" || ' ' || mo."name" || ' ' || vv."name" AS "Value"
            FROM "VehicleVariant" vv
            JOIN "VehicleModel" mo ON mo."id" = vv."modelId"
            JOIN "VehicleMake" mk ON mk."id" = mo."makeId"
            WHERE vv."id" = {variantId}
            """).ToListAsync(ct)).FirstOrDefault();

}

/// <summary>A search hit, flat — the join returns columns, so the row carries columns.</summary>
/// <param name="Available">
/// Units sellable across active warehouses, or null where nobody has counted
/// the part in. SUM over no rows is null, which is exactly the distinction the
/// catalogue already draws between untracked and empty.
/// </param>
public record SearchRow(
    string Id,
    string PartNumber,
    string Name,
    string? Description,
    int StockDays,
    double BasePrice,
    string? SupplierId,
    /// <summary>oem | aftermarket | substitute — what the customer would be buying.</summary>
    string PartType,
    /// <summary>What one package is called: piece, pair, set, box, pack, litre, metre, kit.</summary>
    string PackagingUnit,
    /// <summary>The step an order moves in. One means no constraint.</summary>
    int QuantityPerPackage,
    /// <summary>The commercial category the part is priced through, or null.</summary>
    string? GoodsCategoryId,
    /// <summary>What one piece weighs, in grams. Null where nobody has weighed it.</summary>
    int? WeightGrams,
    string ManufacturerName,
    string SystemName,
    string SystemSlug,
    double? ListPrice,
    string? ImageUrl,
    string? ImageAlt,
    int? Available,
    string? SupplierSlug,
    string? SupplierName,
    int? SupplierRating,
    string? SupplierReliability,
    bool? SupplierAcceptsReturns) : IPriceable;

public record InterchangeRow(string SourceId, string TargetPartNo, string TargetManufacturer, bool IsOem);

/// <summary>
/// The filters that narrow which parts are considered at all.
/// </summary>
/// <remarks>
/// Split out rather than added to the search's own parameters because the
/// facet query needs the two halves separately: the system counts describe
/// everything the query matched, and the rest describe what is in the chosen
/// system. One flat parameter list would have to be taken apart at every call
/// site to say which half was meant.
/// </remarks>
/// <param name="MinRating">
/// Lowest supplier rating that counts. A part whose supplier is unrated, or
/// which has no supplier, falls outside every minimum on purpose: "at least
/// four stars" is a claim about known performance, and theirs is not known.
/// </param>
/// <param name="ReturnsOnly">
/// Only suppliers known to take stock back — an explicit yes, never a null.
/// </param>
/// <param name="PartType">Empty means every kind.</param>
public record RowFilters(
    string? System = null,
    string? Manufacturer = null,
    int? MinRating = null,
    string? Reliability = null,
    bool ReturnsOnly = false,
    string[]? PartType = null);

/// <summary>Rows plus how many there would have been without the limit.</summary>
/// <param name="Total">
/// The exact number of parts the filters match, whatever the limit was.
/// <c>COUNT(*) OVER ()</c> on the same pass rather than a second query: a
/// separate count runs the whole match again, and between the two the answer
/// can change.
/// </param>
public record SearchPage(List<SearchRow> Rows, int Total);

/// <summary>A search row carrying the total of the set it came from.</summary>
/// <remarks>
/// Its own type rather than a nullable field on <see cref="SearchRow"/>:
/// EF requires every property of the queried type to be present in the result
/// set, so a <c>Total</c> on SearchRow would break <c>ByIdsAsync</c>, which
/// does not count. The columns are therefore written out once more here, for
/// the same reason the SELECT is — see the note on the class.
/// </remarks>
public record CountedSearchRow(
    int Total,
    string Id,
    string PartNumber,
    string Name,
    string? Description,
    int StockDays,
    double BasePrice,
    string? SupplierId,
    string PartType,
    string PackagingUnit,
    int QuantityPerPackage,
    string? GoodsCategoryId,
    int? WeightGrams,
    string ManufacturerName,
    string SystemName,
    string SystemSlug,
    double? ListPrice,
    string? ImageUrl,
    string? ImageAlt,
    int? Available,
    string? SupplierSlug,
    string? SupplierName,
    int? SupplierRating,
    string? SupplierReliability,
    bool? SupplierAcceptsReturns)
{
    public SearchRow ToRow() => new(
        Id, PartNumber, Name, Description, StockDays, BasePrice, SupplierId, PartType,
        PackagingUnit, QuantityPerPackage, GoodsCategoryId, WeightGrams,
        ManufacturerName, SystemName, SystemSlug, ListPrice, ImageUrl, ImageAlt, Available,
        SupplierSlug, SupplierName, SupplierRating, SupplierReliability, SupplierAcceptsReturns);
}

/// <summary>One facet tally. <c>Label</c> carries the system's name; nothing else needs one.</summary>
public record FacetCount(string Kind, string Key, string? Label, int Count);

/// <summary>
/// Comparing part numbers.
/// </summary>
/// <remarks>
/// Part numbers are printed with whatever separators the brand favours —
/// <c>0 986 424 815</c>, <c>09.9772.11</c>, <c>W 712/75</c> — and nobody types
/// them back the same way. Comparing on this form means the separators stop
/// mattering.
/// </remarks>
public static class PartNumbers
{
    public static string Normalise(string value) =>
        new(value.Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}

/// <summary>
/// How a supplier is classified, strongest relationship first.
/// </summary>
/// <remarks>
/// The order is meaningful — the search facet is presented in it rather than
/// by count, because it is a ranking.
/// </remarks>
public static class Reliabilities
{
    public static readonly string[] All = ["official", "dealer", "reliable", "standard"];

    public static bool IsKnown(string value) => All.Contains(value);
}
