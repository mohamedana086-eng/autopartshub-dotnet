using AutoPartsHub.Api.Catalogue;
using AutoPartsHub.Api.Pricing;

namespace AutoPartsHub.Api.Endpoints;

public static class SearchEndpoints
{
    private static readonly string[] Sorts = ["relevance", "price-asc", "price-desc", "delivery"];

    /// <summary>
    /// Which kind of number a result was found by.
    /// </summary>
    /// <remarks>
    /// A customer holding a number off a dealer invoice and one holding a
    /// competitor's catalogue are asking different questions, and the answer
    /// to both used to be the same list.
    /// </remarks>
    private static readonly string[] MatchIns = ["part-number", "oem", "aftermarket"];

    /// <summary>
    /// What the part IS, which is a different question from which number found it.
    /// </summary>
    /// <remarks>
    /// <c>MatchIns</c> above asks where the search looked. This asks what the
    /// customer would be buying. Searching an OE number normally returns
    /// aftermarket parts — that is the entire purpose of cross-references — so
    /// a single filter answering both would answer neither.
    ///
    /// Ordered maker-first, which is how a customer ranks them and how the UI
    /// lists them.
    /// </remarks>
    private static readonly string[] PartTypes = ["oem", "aftermarket", "substitute"];

    private const int MaxResults = 200;

    // GET /api/catalog/search?q=&system=&manufacturer=&sort=&limit=
    // Prices come from the caller's own session tier — see PricingContextLoader.
    public static void MapSearchEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/catalog/search", async (
            HttpContext http, SearchQueries queries, PricingContextLoader pricing, CancellationToken ct) =>
        {
            var query = http.Request.Query;
            string? Param(string key) =>
                query[key].ToString() is { Length: > 0 } v && v.Trim() is { Length: > 0 } t ? t : null;

            var q = query["q"].ToString().Trim();
            var system = Param("system");
            var manufacturer = Param("manufacturer");
            var variant = Param("variant");
            var supplier = Param("supplier");

            // Minimum supplier rating, 1–5. A part whose supplier is unrated,
            // or which has no supplier at all, falls outside every minimum on
            // purpose: "at least four stars" is a claim about known
            // performance, and theirs is not known.
            int? minRating = int.TryParse(query["minRating"], out var r) && r is >= 1 and <= 5 ? r : null;

            // `reliability` is what the trading relationship is; `returns` is
            // whether they take stock back. Independent of each other and of
            // the rating, so all three compose.
            var reliability = Param("reliability") is { } rel && Reliabilities.IsKnown(rel) ? rel : null;

            // Only an explicit yes matches. A supplier whose return terms have
            // not been recorded is not evidence that they accept them.
            var returnsOnly = query["returns"].ToString() == "true";

            // Which kinds of number to look in. Empty means look everywhere,
            // including names and brands — not the same as selecting all
            // three, which restricts to those reachable by some number and
            // drops the ones that only matched on a name. Ordered by MatchIns
            // rather than by however the caller listed them, so the same
            // selection always echoes back the same way.
            var requested = query["matchIn"].ToString().Split(',')
                .Select(v => v.Trim()).Where(v => v.Length > 0).ToHashSet();
            var matchIn = q.Length > 0 ? MatchIns.Where(requested.Contains).ToArray() : [];

            // Which kinds of part to show. Unlike matchIn this is meaningful
            // with nothing typed — "show me the genuine ones in this system" is
            // a question a customer asks while browsing. Empty means all three,
            // because a filter nobody has touched should not be quietly
            // removing anything. Ordered by PartTypes so the same selection
            // always echoes back the same way.
            var requestedTypes = query["partType"].ToString().Split(',')
                .Select(v => v.Trim()).Where(v => v.Length > 0).ToHashSet();
            var partType = PartTypes.Where(requestedTypes.Contains).ToArray();

            var sort = Sorts.Contains(query["sort"].ToString()) ? query["sort"].ToString() : "relevance";

            var limit = int.TryParse(query["limit"], out var l) && l > 0 ? Math.Min(l, MaxResults) : MaxResults;

            double? PriceBound(string key) =>
                double.TryParse(query[key], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : null;
            var minPrice = PriceBound("minPrice");
            var maxPrice = PriceBound("maxPrice");

            // Every word has to land somewhere, but not all in the same field.
            var tokens = q.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            // Separated part numbers and cross-references need a scan, so they
            // resolve to ids first. Its own branch, so `0 986 424 815` still
            // lands on the part directly rather than depending on each
            // fragment matching.
            var normalisedIds = q.Length > 0
                ? await queries.IdsMatchingNormalisedPartNumberAsync(q, ct)
                : [];

            // Vehicle and supplier narrow the catalogue itself, so they run in
            // the query. System and brand are applied further down, after
            // their facet counts have been taken — a count already narrowed by
            // its own filter tells the customer nothing about what else they
            // could pick.
            var matches = await queries.SearchAsync(tokens, normalisedIds, variant, supplier, MaxResults, ct);
            var systemName = system is null ? null : await queries.SystemNameBySlugAsync(system, ct);
            var variantName = variant is null ? null : await queries.VariantLabelAsync(variant, ct);
            var supplierName = supplier is null ? null : await queries.SupplierNameBySlugAsync(supplier, ct);
            var ctx = await pricing.LoadAsync(http, ct);

            // Nothing matched as typed — try again allowing for a misspelling,
            // and say so in the response so the UI does not present guesses as
            // exact hits.
            var fuzzy = false;
            if (q.Length > 0 && matches.Count == 0)
            {
                var closeIds = await queries.IdsByFuzzyMatchAsync(q, ct);
                if (closeIds.Count > 0)
                {
                    var close = await queries.ByIdsAsync(closeIds, variant, supplier, ct);
                    // Keep the order the similarity scoring produced.
                    var rankById = closeIds.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
                    close.Sort((a, b) => rankById.GetValueOrDefault(a.Id).CompareTo(rankById.GetValueOrDefault(b.Id)));
                    // Only claim a near-miss if one survived. The scoring runs
                    // over the whole table, so every id it liked can belong to
                    // a switched-off supplier and be filtered out on the way
                    // back — and "did you mean" above an empty list is the UI
                    // saying it found something it did not.
                    if (close.Count > 0)
                    {
                        matches = close;
                        fuzzy = true;
                    }
                }
            }

            // Cross-references for whatever came back, in one query. Joined
            // onto the rows above they would multiply every product by its
            // references and have to be folded back together anyway.
            var crossRefs = (await queries.InterchangesForAsync(matches.Select(m => m.Id).ToList(), ct))
                .GroupBy(i => i.SourceId)
                .ToDictionary(g => g.Key, g => g.ToList());
            List<InterchangeRow> InterchangesOf(SearchRow p) => crossRefs.GetValueOrDefault(p.Id) ?? [];

            var systemCounts = new Dictionary<string, (string Slug, string Name, int Count)>();
            foreach (var p in matches)
            {
                systemCounts[p.SystemSlug] = systemCounts.TryGetValue(p.SystemSlug, out var e)
                    ? (e.Slug, e.Name, e.Count + 1)
                    : (p.SystemSlug, p.SystemName, 1);
            }

            var inSystem = system is null ? matches : matches.Where(p => p.SystemSlug == system).ToList();

            var brandCounts = new Dictionary<string, int>();
            foreach (var p in inSystem)
            {
                brandCounts[p.ManufacturerName] = brandCounts.GetValueOrDefault(p.ManufacturerName) + 1;
            }

            // Counted per exact rating rather than per threshold, so the UI can
            // build whichever thresholds it offers by summing downwards. Key 0
            // stands for unrated, kept visible so the gap is obvious rather
            // than silently dropped. Like the brand facet, this describes the
            // current system and is not narrowed by the rating filter itself.
            var ratingCounts = new Dictionary<int, int>();
            foreach (var p in inSystem)
            {
                var key = p.SupplierRating ?? 0;
                ratingCounts[key] = ratingCounts.GetValueOrDefault(key) + 1;
            }

            // Counted on inSystem alongside the brand and rating facets, and so
            // before the part-type filter narrows anything: a count already
            // narrowed by its own filter says "3 genuine" when genuine is the
            // only thing selected, which tells the customer nothing about what
            // unticking it would show.
            //
            // Every part has exactly one type, so unlike the matchIn counts
            // these sum to the result count rather than overlapping.
            var partTypeCounts = new Dictionary<string, int>();
            foreach (var p in inSystem)
            {
                partTypeCounts[p.PartType] = partTypeCounts.GetValueOrDefault(p.PartType) + 1;
            }

            var reliabilityCounts = new Dictionary<string, int>();
            var returnsCount = 0;
            foreach (var p in inSystem)
            {
                if (p.SupplierReliability is null) continue;
                reliabilityCounts[p.SupplierReliability] = reliabilityCounts.GetValueOrDefault(p.SupplierReliability) + 1;
                if (p.SupplierAcceptsReturns == true) returnsCount++;
            }

            var needle = PartNumbers.Normalise(q);
            var lower = q.ToLowerInvariant();
            var lowerTokens = tokens.Select(t => t.ToLowerInvariant()).ToArray();
            bool ContainsEvery(string haystack) => lowerTokens.All(haystack.Contains);

            /* Whether a number contains the query, ignoring separators either way. */
            bool NumberHit(string value) =>
                value.ToLowerInvariant().Contains(lower)
                || (needle.Length > 0 && PartNumbers.Normalise(value).Contains(needle));

            // Which kinds of number this product could be found by.
            // Independent of each other and of matchedOn: a part can be
            // reachable by its own number and by an OE reference at once, and
            // both are true. This is what the search options act on — the
            // question is "where should the search look", not "which single
            // field won the ranking".
            Dictionary<string, bool> HitsFor(SearchRow p) => new()
            {
                ["part-number"] = q.Length > 0 && NumberHit(p.PartNumber),
                ["oem"] = q.Length > 0 && InterchangesOf(p).Any(i => i.IsOem && NumberHit(i.TargetPartNo)),
                ["aftermarket"] = q.Length > 0 && InterchangesOf(p).Any(i => !i.IsOem && NumberHit(i.TargetPartNo)),
            };

            var scored = inSystem
                .Where(p => manufacturer is null
                            || p.ManufacturerName.Equals(manufacturer, StringComparison.OrdinalIgnoreCase))
                .Where(p => minRating is null || (p.SupplierRating ?? 0) >= minRating)
                .Where(p => reliability is null || p.SupplierReliability == reliability)
                .Where(p => !returnsOnly || p.SupplierAcceptsReturns == true)
                // Any of the selected kinds is enough. They are alternatives a
                // customer is willing to accept — "genuine or aftermarket, but
                // not a substitute" — and no part could satisfy two at once.
                .Where(p => partType.Length == 0 || partType.Contains(p.PartType))
                .Select((p, index) =>
                {
                    var normalisedPart = PartNumbers.Normalise(p.PartNumber);
                    var name = p.Name.ToLowerInvariant();
                    var brand = p.ManufacturerName.ToLowerInvariant();
                    var description = (p.Description ?? "").ToLowerInvariant();

                    var rank = 8;
                    var matchedOn = "description";
                    string? matchedVia = null;
                    string? matchedViaManufacturer = null;

                    if (fuzzy)
                    {
                        // Nothing matched literally, so the only meaningful
                        // order is how close the database scored each row.
                        rank = index;
                        matchedOn = "name";
                    }
                    else if (q.Length == 0) rank = 0;
                    else if (needle.Length > 0 && normalisedPart == needle) { rank = 0; matchedOn = "part-number"; }
                    else if (needle.Length > 0 && normalisedPart.StartsWith(needle)) { rank = 1; matchedOn = "part-number"; }
                    else if (needle.Length > 0 && normalisedPart.Contains(needle)) { rank = 2; matchedOn = "part-number"; }
                    else if (ContainsEvery(name)) { rank = 3; matchedOn = "name"; }
                    else if (ContainsEvery($"{brand} {name}")) { rank = 4; matchedOn = name.Contains(lower) ? "name" : "manufacturer"; }
                    else if (ContainsEvery($"{brand} {name} {description}")) { rank = 5; matchedOn = "description"; }
                    else
                    {
                        var crosses = InterchangesOf(p).Where(i =>
                            i.TargetPartNo.ToLowerInvariant().Contains(lower)
                            || (needle.Length > 0 && PartNumbers.Normalise(i.TargetPartNo).Contains(needle)))
                            .ToList();

                        // The maker's own number wins when both kinds match.
                        // Someone typing a number that is both an OE reference
                        // and a competitor's is holding a dealer invoice far
                        // more often than a rival catalogue.
                        var cross = crosses.FirstOrDefault(i => i.IsOem) ?? crosses.FirstOrDefault();
                        if (cross is not null)
                        {
                            rank = cross.IsOem ? 6 : 7;
                            matchedOn = cross.IsOem ? "interchange-oem" : "interchange-aftermarket";
                            matchedVia = cross.TargetPartNo;
                            matchedViaManufacturer = cross.TargetManufacturer;
                        }
                    }

                    var priced = ctx.Price(p);

                    return new Scored(rank, HitsFor(p), new SearchProductDto(
                        p.Id, p.PartNumber, p.Name, p.ManufacturerName, p.SystemName, p.SystemSlug,
                        p.PartType,
                        p.StockDays,
                        priced?.FinalPrice ?? RequestPricing.PurchasePrice(p),
                        priced?.AppliedRule,
                        // Null where nobody has added a picture. The alt falls
                        // back to the part's name where it is rendered.
                        p.ImageUrl is null ? null : new ImageDto(p.ImageUrl, p.ImageAlt),
                        // Null where nobody has counted this part in — not the
                        // same as none left.
                        p.Available,
                        p.SupplierSlug is null ? null : new SearchSupplierDto(
                            p.SupplierSlug, p.SupplierName!, p.SupplierRating,
                            p.SupplierReliability!, p.SupplierAcceptsReturns),
                        matchedOn, matchedVia, matchedViaManufacturer));
                })
                .ToList();

            // Counted before the option narrows anything, so each one shows
            // what it would leave. A result counts under every kind that can
            // reach it, not just one.
            var matchCounts = MatchIns.ToDictionary(k => k, k => scored.Count(s => s.Hits[k]));

            // Any of the selected kinds is enough — the options are
            // alternatives a customer is willing to accept, not conditions a
            // part has to satisfy all of at once.
            var byMatch = matchIn.Length > 0
                ? scored.Where(s => matchIn.Any(k => s.Hits[k])).ToList()
                : scored;

            // Bounds describe what is available before the price filter
            // narrows it, so the UI can show the range the inputs sit in.
            var prices = byMatch.Select(s => s.Product.Price).ToList();
            var priceRange = prices.Count > 0
                ? new { min = Math.Floor(prices.Min()), max = Math.Ceiling(prices.Max()) }
                : null;

            var withinPrice = byMatch
                .Where(s => (minPrice is null || s.Product.Price >= minPrice)
                            && (maxPrice is null || s.Product.Price <= maxPrice))
                .ToList();

            // Part number breaks every remaining tie. Without it two parts that
            // agree on the sort key come back in whichever order the rows
            // arrived in, which nothing decides.
            //
            // InvariantCulture, not Ordinal: JavaScript localeCompare treats
            // case as a secondary weight, so "Cabin filter" sorts before "CV
            // joint kit". Ordinal compares code units, puts V before a, and
            // reorders the catalogue. Same set either way, different page.
            withinPrice.Sort((a, b) =>
            {
                int ByPartNumber() => string.Compare(a.Product.PartNumber, b.Product.PartNumber, StringComparison.InvariantCulture);
                int ByName() => string.Compare(a.Product.Name, b.Product.Name, StringComparison.InvariantCulture);

                return sort switch
                {
                    "price-asc" => a.Product.Price.CompareTo(b.Product.Price) is var c and not 0 ? c : ByPartNumber(),
                    "price-desc" => b.Product.Price.CompareTo(a.Product.Price) is var c and not 0 ? c : ByPartNumber(),
                    "delivery" => a.Product.StockDays.CompareTo(b.Product.StockDays) is var c and not 0 ? c
                        : a.Product.Price.CompareTo(b.Product.Price) is var d and not 0 ? d
                        : ByPartNumber(),
                    _ => a.Rank.CompareTo(b.Rank) is var c and not 0 ? c
                        : ByName() is var n and not 0 ? n
                        : ByPartNumber(),
                };
            });

            return Results.Ok(new
            {
                query = q,
                systemName,
                system,
                manufacturer,
                variant,
                variantLabel = variantName,
                supplier,
                supplierName,
                minRating,
                reliability,
                returns = returnsOnly,
                matchIn,
                partType,
                minPrice,
                maxPrice,
                sort,
                /* True when nothing matched as typed and these are close matches. */
                fuzzy,
                tierName = ctx.TierName,
                isLoggedIn = ctx.IsLoggedIn,
                count = withinPrice.Count,
                priceRange,
                facets = new
                {
                    systems = systemCounts.Values
                        .OrderByDescending(s => s.Count).ThenBy(s => s.Name, StringComparer.InvariantCulture)
                        .Select(s => new { slug = s.Slug, name = s.Name, count = s.Count }),
                    manufacturers = brandCounts
                        .OrderByDescending(b => b.Value).ThenBy(b => b.Key, StringComparer.InvariantCulture)
                        .Select(b => new { name = b.Key, count = b.Value }),
                    /* Per exact supplier rating, best first; rating null is unrated. */
                    supplierRatings = ratingCounts
                        .OrderByDescending(x => x.Key)
                        .Select(x => new { rating = x.Key == 0 ? (int?)null : x.Key, count = x.Value }),
                    /* Ordered official -> reliable -> standard, not by count: it
                       is a ranking, and shuffling it by popularity reads as noise. */
                    reliabilities = Reliabilities.All
                        .Select(name => new { name, count = reliabilityCounts.GetValueOrDefault(name) })
                        .Where(x => x.count > 0),
                    /* How many results come from a supplier known to take stock back. */
                    returns = returnsCount,
                    /* Where a search can look, and how many results each place
                       holds. All three are listed alongside a query, zeros
                       included: they are options a customer picks, and "OEM
                       number 0" answers "is this an OE number?" rather than
                       leaving a space. Absent without a query. */
                    matchIn = q.Length > 0
                        ? MatchIns.Select(name => new { name, count = matchCounts[name] })
                        : [],
                    /* What the results are, and how many of each. All three
                       always, zeros included, maker first rather than by count.
                       Like matchIn these are options a customer picks rather
                       than facets that appear when convenient, and "genuine 0"
                       is the answer to "are any of these genuine?" — an empty
                       space is not. Present with or without a query, because
                       this is a property of the parts rather than of the
                       search. */
                    partTypes = PartTypes.Select(
                        name => new { name, count = partTypeCounts.GetValueOrDefault(name) }),
                },
                products = withinPrice.Take(limit).Select(s => s.Product),
            });
        });
    }

    private record Scored(int Rank, Dictionary<string, bool> Hits, SearchProductDto Product);
}

public record ImageDto(string Url, string? Alt);

public record SearchSupplierDto(
    string Slug, string Name, int? Rating, string Reliability, bool? AcceptsReturns);

public record SearchProductDto(
    string Id,
    string PartNumber,
    string Name,
    string Manufacturer,
    string System,
    string SystemSlug,
    /// <summary>
    /// What the customer would be buying, on every row rather than only on the
    /// ones the filter is currently showing — the badge is there to be read
    /// when nothing is filtered at all.
    /// </summary>
    string PartType,
    int StockDays,
    double Price,
    string? AppliedRule,
    ImageDto? Image,
    int? Available,
    SearchSupplierDto? Supplier,
    string MatchedOn,
    string? MatchedVia,
    string? MatchedViaManufacturer);
