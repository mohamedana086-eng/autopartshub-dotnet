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
/// <code>AND ({supplier} IS NULL OR s."slug" = {supplier})</code>
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
public sealed class SearchQueries(AutoPartsContext db, FullTextSearch fullText)
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
            SELECT TOP {limit} COUNT(*) OVER ()  AS "Total",
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
                   pli."markupPercent" AS "ListRowMarkupPercent",
                   bo."purchasePrice" AS "OfferPrice", bo."supplierId" AS "OfferSupplierId",
                   img."url" AS "ImageUrl", img."alt" AS "ImageAlt",
                   st."available" AS "Available",
                   s."slug" AS "SupplierSlug", s."name" AS "SupplierName",
                   -- Both names travel; SupplierNaming picks one. Reading the
                   -- code here costs nothing — it is on a row already joined —
                   -- and the alternative is a second lookup per response.
                   s."code" AS "SupplierCode", s."rating" AS "SupplierRating",
                   s."reliability" AS "SupplierReliability", s."acceptsReturns" AS "SupplierAcceptsReturns"
            FROM "Product" p
            JOIN "Manufacturer" m ON m."id" = p."manufacturerId"
            JOIN "VehicleSystem" v ON v."id" = p."vehicleSystemId"
            -- The supplier whose offer WON, falling back to the column the part was
            -- first sourced from. Joined on the same value the pricing chain uses, so
            -- a row cannot name one supplier while being priced from another's offer.
            LEFT JOIN "BestOffer" bo ON bo."productId" = p."id"
            LEFT JOIN "Supplier" s ON s."id" = COALESCE(bo."supplierId", p."supplierId")
            LEFT JOIN "PriceListItem" pli
              ON pli."productId" = p."id"
             AND pli."priceListId" = (SELECT TOP 1 "id" FROM "PriceList" WHERE "active" = 1)
            OUTER APPLY (
              SELECT pi."url", pi."alt"
              FROM "ProductImage" pi
              WHERE pi."productId" = p."id"
              ORDER BY pi."sortOrder" ASC
              OFFSET 0 ROWS FETCH NEXT 1 ROWS ONLY
            ) img
            OUTER APPLY (
              SELECT SUM(sl."quantity" - sl."reserved") AS "available"
              FROM "StockLevel" sl
              JOIN "Warehouse" w ON w."id" = sl."warehouseId"
              WHERE sl."productId" = p."id" AND w."active" = 1
            ) st
            WHERE (
              -- No query: everything is reachable, and the filters below do the work.
              ({hasQuery} IS NULL OR {hasQuery} = 0)
              OR p."id" IN (SELECT value COLLATE DATABASE_DEFAULT FROM OPENJSON({SqlList.Of(ids)}))
              -- Every token has to land somewhere, but not all in the same
              -- column — which is what lets "bosch brake pad" work, with the
              -- brand on one and the rest on another. Written as "no token
              -- fails to match", so the number of tokens is a value rather
              -- than a shape.
              OR NOT EXISTS (
                SELECT 1 FROM (SELECT value COLLATE DATABASE_DEFAULT AS tok FROM OPENJSON({SqlList.Of(tokens)})) AS tok_rows
                WHERE NOT (
                  p."partNumber" LIKE '%' + tok + '%'
                  OR p."name" LIKE '%' + tok + '%'
                  OR COALESCE(p."description", '') LIKE '%' + tok + '%'
                  OR m."name" LIKE '%' + tok + '%'
                  OR EXISTS (
                    SELECT 1 FROM "Interchange" i
                    WHERE i."sourceId" = p."id" AND i."targetPartNo" LIKE '%' + tok + '%'
                  )
                )
              )
            )
            AND ({variant} IS NULL OR EXISTS (
              SELECT 1 FROM "Fitment" fit
              WHERE fit."productId" = p."id" AND fit."variantId" = {variant}
            ))
            -- "Their parts" means the parts they OFFER, not the ones their offer
            -- happens to win. A supplier page listing only what we currently buy from
            -- them would hide half their range the day somebody undercut them.
            AND ({supplier} IS NULL OR EXISTS (
              SELECT 1 FROM "SupplierOffer" so
              JOIN "Supplier" ss ON ss."id" = so."supplierId"
              WHERE so."productId" = p."id" AND so."active" = 1 AND ss."active" = 1
                AND ss."slug" = {supplier}
            ))
            -- A supplier who is switched off is not selling, so their parts
            -- leave the catalogue entirely. That is what lets one sign up,
            -- load a whole range and price it, and have none of it on sale
            -- until an admin approves them. Parts with no supplier at all are
            -- the catalogue's own and stay.
            -- A live offer from a live supplier, or no supplier relationship at all.
            AND (
              bo."productId" IS NOT NULL
              OR (p."supplierId" IS NULL AND NOT EXISTS (
                SELECT 1 FROM "SupplierOffer" so WHERE so."productId" = p."id"
              ))
            )
            -- The row filters. Each cancels itself when its parameter is null,
            -- so every combination is the same statement with the same holes.
            AND ({system} IS NULL OR v."slug" = {system})
            AND ({manufacturer} IS NULL OR lower(m."name") = lower({manufacturer}))
            -- COALESCE rather than a bare comparison: an unrated supplier is
            -- NULL, and NULL >= 4 is null, which drops the row for a reason
            -- nobody reading it could name. Written this way the rule is
            -- legible — unrated counts as zero, so no minimum includes it.
            AND ({minRating} IS NULL OR COALESCE(s."rating", 0) >= {minRating})
            AND ({reliability} IS NULL OR s."reliability" = {reliability})
            -- Only an explicit yes. A supplier whose return terms are
            -- unrecorded is not evidence that they accept them, so the
            -- unrecorded case has to read as "no" rather than as a null that
            -- argues with everything it is compared to. PostgreSQL said that
            -- with IS TRUE; SQL Server has no such test, so the null is
            -- handled where it arises.
            AND (({returnsOnly} IS NULL OR {returnsOnly} = 0) OR (s."acceptsReturns" = 1))
            AND ({SqlList.Of(partType)} IS NULL OR p."partType" IN (SELECT value COLLATE DATABASE_DEFAULT FROM OPENJSON({SqlList.Of(partType)})))
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
              -- The supplier whose offer WON, falling back to the column the part was
              -- first sourced from. Joined on the same value the pricing chain uses, so
              -- a row cannot name one supplier while being priced from another's offer.
              LEFT JOIN "BestOffer" bo ON bo."productId" = p."id"
              LEFT JOIN "Supplier" s ON s."id" = COALESCE(bo."supplierId", p."supplierId")
              WHERE (
                ({hasQuery} IS NULL OR {hasQuery} = 0)
                OR p."id" IN (SELECT value COLLATE DATABASE_DEFAULT FROM OPENJSON({SqlList.Of(ids)}))
                OR NOT EXISTS (
                  SELECT 1 FROM (SELECT value COLLATE DATABASE_DEFAULT AS tok FROM OPENJSON({SqlList.Of(tokens)})) AS tok_rows
                  WHERE NOT (
                    p."partNumber" LIKE '%' + tok + '%'
                    OR p."name" LIKE '%' + tok + '%'
                    OR COALESCE(p."description", '') LIKE '%' + tok + '%'
                    OR m."name" LIKE '%' + tok + '%'
                    OR EXISTS (
                      SELECT 1 FROM "Interchange" i
                      WHERE i."sourceId" = p."id" AND i."targetPartNo" LIKE '%' + tok + '%'
                    )
                  )
                )
              )
              AND ({variant} IS NULL OR EXISTS (
                SELECT 1 FROM "Fitment" fit
                WHERE fit."productId" = p."id" AND fit."variantId" = {variant}
              ))
              -- "Their parts" means the parts they OFFER, not the ones their offer
              -- happens to win. A supplier page listing only what we currently buy from
              -- them would hide half their range the day somebody undercut them.
              AND ({supplier} IS NULL OR EXISTS (
                SELECT 1 FROM "SupplierOffer" so
                JOIN "Supplier" ss ON ss."id" = so."supplierId"
                WHERE so."productId" = p."id" AND so."active" = 1 AND ss."active" = 1
                  AND ss."slug" = {supplier}
              ))
              -- A live offer from a live supplier, or no supplier relationship at all.
              AND (
                bo."productId" IS NOT NULL
                OR (p."supplierId" IS NULL AND NOT EXISTS (
                  SELECT 1 FROM "SupplierOffer" so WHERE so."productId" = p."id"
                ))
              )
            ),
            in_system AS (
              SELECT * FROM matched WHERE ({system} IS NULL OR "systemSlug" = {system})
            )
            SELECT 'system' AS "Kind", "systemSlug" AS "Key", "systemName" AS "Label",
                   COUNT(*) AS "Count"
            FROM matched GROUP BY "systemSlug", "systemName"
            UNION ALL
            SELECT 'brand', "manufacturerName", NULL, COUNT(*) FROM in_system
            GROUP BY "manufacturerName"
            UNION ALL
            -- Per exact rating rather than per threshold, so a caller can build
            -- whichever thresholds it offers by summing downwards. Key 0 is
            -- unrated, kept visible so the gap is obvious rather than dropped.
            SELECT 'rating', COALESCE("supplierRating", 0), NULL, COUNT(*) FROM in_system
            GROUP BY COALESCE("supplierRating", 0)
            UNION ALL
            SELECT 'reliability', "supplierReliability", NULL, COUNT(*) FROM in_system
            WHERE "supplierReliability" IS NOT NULL GROUP BY "supplierReliability"
            UNION ALL
            -- Counted only among parts that have a supplier at all, matching
            -- the reliability tally beside it: a part with nobody behind it is
            -- not evidence either way about returns.
            SELECT 'returns', 'yes', NULL, COUNT(*) FROM in_system
            WHERE "supplierReliability" IS NOT NULL AND ("supplierAcceptsReturns" = 1)
            UNION ALL
            SELECT 'partType', "partType", NULL, COUNT(*) FROM in_system GROUP BY "partType"
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
                   pli."markupPercent" AS "ListRowMarkupPercent",
                   bo."purchasePrice" AS "OfferPrice", bo."supplierId" AS "OfferSupplierId",
                   img."url" AS "ImageUrl", img."alt" AS "ImageAlt",
                   st."available" AS "Available",
                   s."slug" AS "SupplierSlug", s."name" AS "SupplierName",
                   -- Both names travel; SupplierNaming picks one. Reading the
                   -- code here costs nothing — it is on a row already joined —
                   -- and the alternative is a second lookup per response.
                   s."code" AS "SupplierCode", s."rating" AS "SupplierRating",
                   s."reliability" AS "SupplierReliability", s."acceptsReturns" AS "SupplierAcceptsReturns"
            FROM "Product" p
            JOIN "Manufacturer" m ON m."id" = p."manufacturerId"
            JOIN "VehicleSystem" v ON v."id" = p."vehicleSystemId"
            -- The supplier whose offer WON, falling back to the column the part was
            -- first sourced from. Joined on the same value the pricing chain uses, so
            -- a row cannot name one supplier while being priced from another's offer.
            LEFT JOIN "BestOffer" bo ON bo."productId" = p."id"
            LEFT JOIN "Supplier" s ON s."id" = COALESCE(bo."supplierId", p."supplierId")
            LEFT JOIN "PriceListItem" pli
              ON pli."productId" = p."id"
             AND pli."priceListId" = (SELECT TOP 1 "id" FROM "PriceList" WHERE "active" = 1)
            OUTER APPLY (
              SELECT pi."url", pi."alt"
              FROM "ProductImage" pi
              WHERE pi."productId" = p."id"
              ORDER BY pi."sortOrder" ASC
              OFFSET 0 ROWS FETCH NEXT 1 ROWS ONLY
            ) img
            OUTER APPLY (
              SELECT SUM(sl."quantity" - sl."reserved") AS "available"
              FROM "StockLevel" sl
              JOIN "Warehouse" w ON w."id" = sl."warehouseId"
              WHERE sl."productId" = p."id" AND w."active" = 1
            ) st
            WHERE p."id" IN (SELECT value COLLATE DATABASE_DEFAULT FROM OPENJSON({SqlList.Of(array)}))
            AND ({variant} IS NULL OR EXISTS (
              SELECT 1 FROM "Fitment" fit
              WHERE fit."productId" = p."id" AND fit."variantId" = {variant}
            ))
            -- "Their parts" means the parts they OFFER, not the ones their offer
            -- happens to win. A supplier page listing only what we currently buy from
            -- them would hide half their range the day somebody undercut them.
            AND ({supplier} IS NULL OR EXISTS (
              SELECT 1 FROM "SupplierOffer" so
              JOIN "Supplier" ss ON ss."id" = so."supplierId"
              WHERE so."productId" = p."id" AND so."active" = 1 AND ss."active" = 1
                AND ss."slug" = {supplier}
            ))
            -- Same rule as the search above. Reached by id rather than by
            -- matching, which is exactly why it has to be repeated: a fuzzy
            -- match that skipped the check would be a way round it.
            -- A live offer from a live supplier, or no supplier relationship at all.
            AND (
              bo."productId" IS NOT NULL
              OR (p."supplierId" IS NULL AND NOT EXISTS (
                SELECT 1 FROM "SupplierOffer" so WHERE so."productId" = p."id"
              ))
            )
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
            WHERE "sourceId" IN (SELECT value COLLATE DATABASE_DEFAULT FROM OPENJSON({SqlList.Of(array)}))
            """).ToListAsync(ct);
    }

    /// <summary>
    /// Product ids whose part number, or one of their cross-references,
    /// matches the query once separators are ignored.
    /// </summary>
    /// <remarks>
    /// The normalised form is a stored column now — see
    /// <c>AutoPartsContext.SearchIndexSql</c> — so this compares a column
    /// rather than recomputing one per row. It is still a scan, because the
    /// contract here is "contains" and no index answers a leading wildcard;
    /// what changed is that the scan reads a value instead of running a
    /// regular expression to derive one. The near-miss search below, which is
    /// allowed to be a prefix, seeks.
    /// </remarks>
    public async Task<List<string>> IdsMatchingNormalisedPartNumberAsync(
        string q, CancellationToken ct = default)
    {
        var needle = PartNumbers.Normalise(q);
        if (needle.Length < ShortestNeedle) return [];
        var pattern = $"%{needle}%";

        return await db.Database.SqlQuery<string>($"""
            SELECT DISTINCT p."id" AS "Value"
            FROM "Product" p
            LEFT JOIN "Interchange" i ON i."sourceId" = p."id"
            WHERE p."partNumberNormalised" LIKE {pattern}
               OR i."targetPartNoNormalised" LIKE {pattern}
            """).ToListAsync(ct);
    }

    /// <summary>
    /// Below this a near miss is more noise than help: two characters of a
    /// part number reach most of the catalogue.
    /// </summary>
    private const int ShortestNeedle = 3;

    /// <summary>
    /// How many near misses are worth offering.
    /// </summary>
    /// <remarks>
    /// Small on purpose, and not only for the page: it is the limit each lane
    /// is capped at, so "how much work can an empty search cause" has an
    /// answer that does not depend on the size of the catalogue. Twenty-five
    /// is what the similarity scoring returned, kept so the page that presents
    /// them is unchanged.
    /// </remarks>
    private const int NearMisses = 25;

    /// <summary>
    /// Closest products to a query that matched nothing exactly, ordered by
    /// how close they are.
    /// </summary>
    /// <remarks>
    /// This used to be pg_trgm: <c>word_similarity</c> against every product
    /// name, every manufacturer name and every part number, scored, filtered
    /// at 0.45 and sorted. It read the whole table on every search that came
    /// up empty — which is precisely the search a customer repeats, having
    /// typed it wrong once. SQL Server has no equivalent function, and T-067
    /// does not ask for one: two seeks, no leading wildcard, a small limit.
    ///
    /// TWO LANES
    /// ---------
    /// A part number and a name are wrong in different ways and are reached by
    /// different indexes, so they are asked separately rather than scored
    /// together. Numbers first: three normalised characters of a part number
    /// agreeing is a deliberate act, while three characters of a name agreeing
    /// is a coincidence the catalogue is full of.
    ///
    /// WHAT WAS LOST
    /// -------------
    /// Trigram similarity matched a typo in the MIDDLE of a word — "brkae pad"
    /// scored against "Brake pad set, front". Neither lane here does: a prefix
    /// seek and a full-text prefix term both need the start to be right. That
    /// is the trade the task makes, and it is the same property as the scan
    /// going away, seen from the other side. The shapes that still work are
    /// truncation ("brake pa", "0 986 42") and one wrong word among right
    /// ones, which is most of what a search box receives.
    /// </remarks>
    public async Task<List<string>> IdsByFuzzyMatchAsync(string q, CancellationToken ct = default)
    {
        var found = new List<string>();

        found.AddRange(await IdsByPartNumberPrefixAsync(q, ct));
        found.AddRange(await IdsByNameAsync(q, ct));

        return found.Distinct().Take(NearMisses).ToList();
    }

    /// <summary>Products whose part number, or a cross-reference to one,
    /// starts with what was typed.</summary>
    /// <remarks>
    /// Both sides seek their normalised column. The grouping is not
    /// decoration: a product can be reached by its own number and by a
    /// cross-reference to it in the same query, and the caller reads this list
    /// as a ranking, so the same id arriving twice would spend two of the
    /// twenty-five places on one product.
    /// </remarks>
    private async Task<List<string>> IdsByPartNumberPrefixAsync(string q, CancellationToken ct)
    {
        var needle = PartNumbers.Normalise(q);
        if (needle.Length < ShortestNeedle) return [];
        var prefix = $"{needle}%";

        return await db.Database.SqlQuery<string>($"""
            SELECT TOP ({NearMisses}) "Value"
            FROM (
                SELECT p."id" AS "Value", p."partNumberNormalised" AS "Sort",
                       p."partNumber" AS "Tie"
                FROM "Product" p
                WHERE p."partNumberNormalised" LIKE {prefix}
                UNION ALL
                SELECT i."sourceId", i."targetPartNoNormalised", i."targetPartNo"
                FROM "Interchange" i
                WHERE i."targetPartNoNormalised" LIKE {prefix}
            ) hit
            GROUP BY "Value"
            -- Shortest completion first, so the number that is nearly the one
            -- typed outranks the one that merely begins the same way. Part
            -- number breaks the tie for the same reason it does in the ranked
            -- search: two products can carry the same normalised number.
            ORDER BY MIN("Sort") ASC, MIN("Tie") ASC
            """).ToListAsync(ct);
    }

    /// <summary>Products whose name, or whose manufacturer's name, is close to
    /// what was typed.</summary>
    /// <remarks>
    /// Which lane runs depends on the deployment rather than on the query —
    /// see <see cref="FullTextSearch"/>. Both seek; full text reaches a word
    /// anywhere in a name, and its absence reaches only the start of one.
    /// </remarks>
    private async Task<List<string>> IdsByNameAsync(string q, CancellationToken ct)
    {
        var needle = q.Trim();
        if (needle.Length < ShortestNeedle) return [];

        var prefix = $"{needle}%";
        var terms = FullTextSearch.TermsFor(needle);

        return terms is not null && await fullText.IsIndexedAsync(ct)
            ? await IdsByIndexedNameAsync(terms, prefix, ct)
            : await IdsByNamePrefixAsync(prefix, ct);
    }

    /// <summary>The name lane on an engine with no full-text index.</summary>
    /// <remarks>
    /// The manufacturer half is a seek whether or not full text exists —
    /// Manufacturer is small and its name is uniquely indexed — so it is the
    /// same statement in both lanes.
    /// </remarks>
    private Task<List<string>> IdsByNamePrefixAsync(string prefix, CancellationToken ct) =>
        db.Database.SqlQuery<string>($"""
            SELECT TOP ({NearMisses}) "Value"
            FROM (
                SELECT p."id" AS "Value", p."name" AS "Name", p."partNumber" AS "PartNumber"
                FROM "Product" p
                WHERE p."name" LIKE {prefix}
                UNION ALL
                SELECT p."id", p."name", p."partNumber"
                FROM "Product" p
                JOIN "Manufacturer" m ON m."id" = p."manufacturerId"
                WHERE m."name" LIKE {prefix}
            ) hit
            GROUP BY "Value"
            ORDER BY MIN("Name") ASC, MIN("PartNumber") ASC
            """).ToListAsync(ct);

    /// <summary>The name lane where the engine has a full-text index.</summary>
    /// <remarks>
    /// <c>CONTAINSTABLE</c> rather than a <c>CONTAINS</c> predicate, because
    /// it returns a RANK and the caller reads this list as an ordering. It
    /// carries the same limit of twenty-five, so the index is asked for a page
    /// rather than for everything that matched.
    ///
    /// READ, NOT RUN. LocalDB — which every test in this repository runs
    /// against — cannot host Full-Text Search:
    /// <c>SERVERPROPERTY('IsFullTextInstalled')</c> answers 0 and no edition
    /// of it answers otherwise. So this statement is the one part of the
    /// search that has not been executed against an engine. The lane beside it
    /// has, <see cref="FullTextSearch.IsIndexedAsync"/> is what decides which
    /// one a deployment gets, and the migration that would create the index
    /// declines to on an instance without the component. When the hosting
    /// decision lands (BLK-003) on an instance that has it, this is the
    /// statement to exercise first.
    /// </remarks>
    private Task<List<string>> IdsByIndexedNameAsync(string terms, string prefix, CancellationToken ct) =>
        db.Database.SqlQuery<string>($"""
            SELECT TOP ({NearMisses}) "Value"
            FROM (
                SELECT p."id" AS "Value", ft."RANK" AS "Rank", p."name" AS "Name",
                       p."partNumber" AS "PartNumber"
                FROM CONTAINSTABLE("Product", ("name"), {terms}, {NearMisses}) ft
                JOIN "Product" p ON p."id" = ft."KEY"
                UNION ALL
                SELECT p."id", 0, p."name", p."partNumber"
                FROM "Product" p
                JOIN "Manufacturer" m ON m."id" = p."manufacturerId"
                WHERE m."name" LIKE {prefix}
            ) hit
            GROUP BY "Value"
            ORDER BY MAX("Rank") DESC, MIN("Name") ASC, MIN("PartNumber") ASC
            """).ToListAsync(ct);

    public Task<string?> SystemNameBySlugAsync(string slug, CancellationToken ct = default) =>
        db.VehicleSystems.Where(v => v.Slug == slug).Select(v => v.Name).FirstOrDefaultAsync(ct);

    /// <summary>
    /// What the supplier filter shows it is doing — both names, so the caller
    /// can publish whichever <see cref="SupplierNaming"/> allows.
    /// </summary>
    /// <remarks>
    /// Returns the pair rather than a resolved name because the label is
    /// built where the role is known, and a query that took the role would be
    /// a second place the anonymity rule lived.
    /// </remarks>
    public Task<SupplierNames?> SupplierNamesBySlugAsync(string slug, CancellationToken ct = default) =>
        db.Suppliers.Where(s => s.Slug == slug)
            .Select(s => new SupplierNames(s.Name, s.Code))
            .FirstOrDefaultAsync(ct);

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
    /// <summary>That line's own margin — the narrowest rung. See IPriceable.</summary>
    double? ListRowMarkupPercent,
    /// <summary>The best offer's price and whose it was — see IPriceable.</summary>
    double? OfferPrice,
    string? OfferSupplierId,
    string? ImageUrl,
    string? ImageAlt,
    int? Available,
    string? SupplierSlug,
    string? SupplierName,
    /// <summary>The opaque handle a customer sees instead of the name.</summary>
    string? SupplierCode,
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
    /// <summary>That line's own margin — the narrowest rung. See IPriceable.</summary>
    double? ListRowMarkupPercent,
    /// <summary>The best offer's price and whose it was — see IPriceable.</summary>
    double? OfferPrice,
    string? OfferSupplierId,
    string? ImageUrl,
    string? ImageAlt,
    int? Available,
    string? SupplierSlug,
    string? SupplierName,
    /// <summary>The opaque handle a customer sees instead of the name.</summary>
    string? SupplierCode,
    int? SupplierRating,
    string? SupplierReliability,
    bool? SupplierAcceptsReturns)
{
    public SearchRow ToRow() => new(
        Id, PartNumber, Name, Description, StockDays, BasePrice, SupplierId, PartType,
        PackagingUnit, QuantityPerPackage, GoodsCategoryId, WeightGrams,
        ManufacturerName, SystemName, SystemSlug, ListPrice, ListRowMarkupPercent,
        OfferPrice, OfferSupplierId,
        ImageUrl, ImageAlt, Available,
        SupplierSlug, SupplierName, SupplierCode, SupplierRating, SupplierReliability,
        SupplierAcceptsReturns);
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
