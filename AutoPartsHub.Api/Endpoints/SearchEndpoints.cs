using AutoPartsHub.Api.Catalogue;
using AutoPartsHub.Api.Data;
using AutoPartsHub.Api.Pricing;
using AutoPartsHub.Domain.Catalogue;

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

    /// <summary>
    /// How deep a search reaches, and therefore how far paging can go.
    /// </summary>
    /// <remarks>
    /// Everything after the query — the system, brand, rating, reliability,
    /// returns and part-type filters, the ranking, and the price bounds that
    /// depend on the caller's own tier — happens over the rows this fetches.
    /// So this is not a page size, it is the depth to which the answer is
    /// exact: <c>count</c> is the true total when fewer than this many rows
    /// matched, and a floor when exactly this many did. <c>truncated</c> in
    /// the response says which.
    ///
    /// A catalogue of millions needs the filters and the facet counts pushed
    /// into SQL so that paging is the database's job; this number is what
    /// makes the current shape honest rather than what makes it scale.
    /// </remarks>
    private const int MaxMatches = 1000;

    // GET /api/catalog/search?q=&system=&manufacturer=&sort=&limit=
    // Prices come from the caller's own session tier — see PricingContextLoader.
    public static void MapSearchEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/catalog/search", async (
            HttpContext http, SearchQueries queries, SpecQueries specQueries,
            PricingContextLoader pricing, AutoPartsContext db, CancellationToken ct) =>
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

            // How many rows to return, and which page of them.
            //
            // `limit` is the name this had before there were pages and still
            // works: it says how many rows, which is what a page size is. The
            // search-as-you-type suggestions ask for six and keep working.
            //
            // Both are clamped rather than refused. A page size of 100000 is a
            // mistake or an attempt, and neither is worth an error message the
            // customer would see; a page below one is a rounding error
            // somewhere. The response echoes what was actually used.
            //
            // The arithmetic is in Paging, where it can be tested without an
            // HTTP round trip — including the fractional and out-of-range
            // cases, which are the ones that differ between languages.
            var pageSize = Paging.ReadPageSize(query["pageSize"], query["limit"]);
            var page = Paging.ReadPage(query["page"]);

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

            // Everything a column can decide now happens in the query,
            // including the facet counts. The endpoint used to filter the rows
            // the query had already returned, which answers a different
            // question the moment there are more parts than the window: one
            // system out of a million is a thousand arbitrary rows narrowed
            // afterwards, not the first thousand of that system.
            var rowFilters = new RowFilters(system, manufacturer, minRating, reliability, returnsOnly, partType);
            var found = await queries.SearchAsync(tokens, normalisedIds, variant, supplier, rowFilters, MaxMatches, ct);
            // Its own round trip because it counts over different populations
            // from the one the rows come from — the systems over everything
            // matched, the rest over the chosen system, and neither narrowed
            // by the facet's own filter.
            var facetRows = await queries.FacetsAsync(tokens, normalisedIds, variant, supplier, system, ct);
            var matches = found.Rows;
            // What the filters match in the database, whatever the window returned.
            var matchedTotal = found.Total;
            var systemName = system is null ? null : await queries.SystemNameBySlugAsync(system, ct);
            var variantName = variant is null ? null : await queries.VariantLabelAsync(variant, ct);
            var supplierNames = supplier is null ? null : await queries.SupplierNamesBySlugAsync(supplier, ct);
            var ctx = await pricing.LoadAsync(http, ct);
            // Who may see a supplier's real name. Taken from the account the
            // pricing was loaded for rather than from the cookie, so a role
            // changed since the last sign-in takes effect on this request.
            var naming = SupplierNaming.For(ctx.ClientRole);
            // The label the supplier filter shows. Anonymised like every other
            // supplier reference: a customer who filters by a code must not be
            // told back whose code it was.
            var supplierName = supplierNames is null
                ? null
                : naming.Of(supplierNames.Name, supplierNames.Code);

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
                        // The facets have to describe these rows rather than
                        // the exact match that found nothing, which is what
                        // the database just counted. Recomputed here rather
                        // than by a second facet query because there are at
                        // most twenty-five of them: a round trip to group a
                        // list this small costs more than the loop.
                        //
                        // Taken before the row filters narrow anything,
                        // exactly as the query takes them.
                        facetRows = FacetsFromRows(close, system);
                        // The row filters have to be applied here too. These
                        // rows arrived by id, through a query that knows
                        // nothing about the filters, so without this a
                        // misspelling answers as though the customer had
                        // selected nothing.
                        matches = ApplyRowFilters(close, rowFilters);
                        // The fallback is capped at twenty-five ids by the
                        // scoring itself, so what came back IS all of it — the
                        // total is what survived the filters, not whatever the
                        // exact-match query counted (which was zero). Anything
                        // else would report a complete answer as truncated.
                        matchedTotal = matches.Count;
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

            // The facet tallies, as the database counted them. Grouped by kind
            // here rather than shaped in SQL: one flat result set is one round
            // trip, and turning it into six lists is a loop rather than six
            // queries.
            List<FacetCount> FacetsOf(string kind) => facetRows.Where(f => f.Kind == kind).ToList();
            Dictionary<string, int> Tally(string kind) =>
                FacetsOf(kind).ToDictionary(f => f.Key, f => f.Count);

            var brandCounts = Tally("brand");
            var reliabilityCounts = Tally("reliability");
            var partTypeCounts = Tally("partType");
            var ratingCounts = FacetsOf("rating").ToDictionary(f => int.Parse(f.Key), f => f.Count);
            var returnsCount = FacetsOf("returns").FirstOrDefault()?.Count ?? 0;

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

            // Already filtered by the query: the system, brand, rating,
            // reliability, returns and part-type predicates all ran in SQL.
            // What is left to do per row is rank it and price it.
            var scored = matches
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
                        // How the part is packed, on the row rather than only
                        // on its page: the quantity control steps by this, and
                        // a customer who reaches the part expecting to buy one
                        // of something sold in pairs has already been misled.
                        p.PackagingUnit, p.QuantityPerPackage,
                        // On the row so a basket assembled from search results
                        // can total itself without asking again.
                        p.WeightGrams,
                        p.StockDays,
                        priced?.FinalPrice ?? RequestPricing.PurchasePrice(p),
                        priced?.AppliedRule,
                        // Null where nobody has added a picture. The alt falls
                        // back to the part's name where it is rendered.
                        p.ImageUrl is null ? null : new ImageDto(p.ImageUrl, p.ImageAlt),
                        // Null where nobody has counted this part in — not the
                        // same as none left.
                        p.Available,
                        naming.Search(
                            p.SupplierSlug, p.SupplierName, p.SupplierCode, p.SupplierRating,
                            p.SupplierReliability, p.SupplierAcceptsReturns),
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

            // The page, taken once. Everything below either reports on it or
            // describes the whole result set; nothing recomputes it.
            var pageRows = Paging.PageOf(withinPrice, page, pageSize).ToList();
            var pageIds = pageRows.Select(s => s.Product.Id).ToList();

            // Specifications for the rows actually being returned, and not one
            // more.
            //
            // Unlike the cross-references above, these are fetched after paging
            // rather than for every match: interchanges decide the ranking, so
            // all of them are needed before the order is known, while
            // specifications are only ever displayed. Fetching them for a
            // thousand matches to show fifty would be twenty times the rows for
            // the same page.
            //
            // The count comes back separately so a row can say how many it is
            // not showing without fetching them to find out.
            var rowSpecs = await specQueries.ForAsync(pageIds, SpecQueries.RowSpecs, ct);
            var howManySpecs = await specQueries.CountsAsync(pageIds, ct);

            // A term that found nothing is written down, once the answer is
            // assembled.
            //
            // Only when the search itself came up empty — a fuzzy rescue means
            // the customer was served and merely mistyped, so recording it
            // would report a gap in the catalogue that is really a gap in their
            // spelling.
            //
            // `narrowed` says whether anything was selected at the time. It is
            // what keeps the report honest: "we do not sell this" and "we do
            // not sell this from that supplier" are different findings, and a
            // column that could not tell them apart would inflate the first
            // with instances of the second.
            //
            // Awaited, and every failure inside it swallowed — a counter is
            // not worth an error page. See Catalogue/SearchMisses.cs.
            if (q.Length > 0 && matches.Count == 0)
            {
                // `partType` and `matchIn` are arrays and are never null — an
                // empty one means nothing was selected, so they are counted by
                // length. The other API says the same thing the same way.
                await SearchMisses.RecordAsync(db, q,
                    narrowed: system is not null || manufacturer is not null || variant is not null
                        || supplier is not null || reliability is not null || returnsOnly
                        || minRating is not null || minPrice is not null || maxPrice is not null
                        || partType.Length > 0 || matchIn.Length > 0,
                    ct);
            }

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
                // How many results there are, after every filter. Exact unless
                // `truncated`, in which case it is a floor: the query stopped
                // at MaxMatches rows and there may be more behind them.
                count = withinPrice.Count,
                page,
                pageSize,
                // Zero when nothing matched, so "page 1 of 0" reads as the
                // empty result it is.
                pageCount = Paging.PageCount(withinPrice.Count, pageSize),
                // True when the database held more rows than the window
                // returned, which makes `count` a floor and the last page not
                // necessarily the last of anything.
                //
                // Exact rather than inferred: the query reports how many rows
                // the filters matched whatever the limit was, so this compares
                // the two instead of guessing from a full window. A search
                // matching exactly MaxMatches rows used to report itself
                // truncated when it was complete.
                //
                // Reported rather than hidden: a total that is quietly a lower
                // bound is worse than no total, because it reads as a fact and
                // every page number computed from it inherits the error.
                truncated = matchedTotal > matches.Count,
                priceRange,
                facets = new
                {
                    systems = FacetsOf("system")
                        .Select(f => new { slug = f.Key, name = f.Label ?? f.Key, count = f.Count })
                        .OrderByDescending(s => s.count).ThenBy(s => s.name, StringComparer.InvariantCulture),
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
                // A page past the end is an empty list rather than a clamp to
                // the last one: the caller asked for something that is not
                // there, and answering with a different page while echoing the
                // number they asked for would be the response disagreeing with
                // itself.
                products = pageRows.Select(s => new SearchProductWithSpecsDto(
                    s.Product,
                    // The first three specifications, in the order the part
                    // lists them. Three because that is what fits beside the
                    // price and the delivery time, and because three is enough
                    // to tell two parts of the same name apart — two "Brake pad
                    // set, front" differ by width, height and thickness. The
                    // rest are on the part's own page.
                    rowSpecs.GetValueOrDefault(s.Product.Id) ?? [],
                    // How many the part has in total, so a row can say what it
                    // is holding back.
                    howManySpecs.GetValueOrDefault(s.Product.Id))),
            });
        });
    }

    /// <summary>
    /// The same six tallies the facet query produces, over rows already in hand.
    /// </summary>
    /// <remarks>
    /// Only the fuzzy fallback needs this. That path replaces the results
    /// after the database has already counted the exact match — which found
    /// nothing, since that is why the fallback ran — so the counts have to be
    /// redone over what it actually returned. It is capped at twenty-five rows
    /// by the scoring, so a loop is cheaper than a second round trip.
    ///
    /// The populations match the query's: systems over everything, the rest
    /// over what is in the chosen system.
    /// </remarks>
    private static List<FacetCount> FacetsFromRows(List<SearchRow> rows, string? system)
    {
        var outp = new List<FacetCount>();
        var inSystem = system is null ? rows : rows.Where(p => p.SystemSlug == system).ToList();

        var systems = new Dictionary<string, (string Name, int Count)>();
        foreach (var p in rows)
        {
            systems[p.SystemSlug] = systems.TryGetValue(p.SystemSlug, out var e)
                ? (e.Name, e.Count + 1)
                : (p.SystemName, 1);
        }
        foreach (var (slug, s) in systems) outp.Add(new FacetCount("system", slug, s.Name, s.Count));

        void Tally(string kind, IEnumerable<string> keys)
        {
            var counts = new Dictionary<string, int>();
            foreach (var k in keys) counts[k] = counts.GetValueOrDefault(k) + 1;
            foreach (var (k, n) in counts) outp.Add(new FacetCount(kind, k, null, n));
        }

        Tally("brand", inSystem.Select(p => p.ManufacturerName));
        Tally("rating", inSystem.Select(p => (p.SupplierRating ?? 0).ToString()));

        var withSupplier = inSystem.Where(p => p.SupplierReliability is not null).ToList();
        Tally("reliability", withSupplier.Select(p => p.SupplierReliability!));
        var returns = withSupplier.Count(p => p.SupplierAcceptsReturns == true);
        if (returns > 0) outp.Add(new FacetCount("returns", "yes", null, returns));

        Tally("partType", inSystem.Select(p => p.PartType));

        return outp;
    }

    /// <summary>
    /// The row filters, applied in memory.
    /// </summary>
    /// <remarks>
    /// The same predicates the search query runs in SQL, over rows already in
    /// hand. Only the fuzzy fallback needs this: that path reaches its rows by
    /// id, through a query that deliberately knows nothing about the filters —
    /// it is asked "which parts are these", not "which parts match". Leaving
    /// them unfiltered made a misspelling ignore the system, brand, rating,
    /// reliability, returns and type the customer had picked.
    ///
    /// Applied after <see cref="FacetsFromRows"/>, because the facets describe
    /// what the near-misses offer BEFORE these narrow them — the same ordering
    /// the query uses, for the same reason.
    /// </remarks>
    private static List<SearchRow> ApplyRowFilters(List<SearchRow> rows, RowFilters r) =>
        rows.Where(p =>
                (r.System is null || p.SystemSlug == r.System)
                && (r.Manufacturer is null
                    || p.ManufacturerName.Equals(r.Manufacturer, StringComparison.OrdinalIgnoreCase))
                && (r.MinRating is null || (p.SupplierRating ?? 0) >= r.MinRating)
                && (r.Reliability is null || p.SupplierReliability == r.Reliability)
                && (!r.ReturnsOnly || p.SupplierAcceptsReturns == true)
                && (r.PartType is not { Length: > 0 } || r.PartType.Contains(p.PartType)))
            .ToList();

    private record Scored(int Rank, Dictionary<string, bool> Hits, SearchProductDto Product);
}

public record ImageDto(string Url, string? Alt);

public record SearchSupplierDto(
    string Slug, string Name, int? Rating, string Reliability, bool? AcceptsReturns);

/// <summary>
/// A search result with its specifications on it.
/// </summary>
/// <remarks>
/// Composed rather than added to <see cref="SearchProductDto"/> because the
/// specifications are not part of the row the ranking and pricing work over —
/// they are fetched after paging, for the page alone, and only to be shown.
/// <c>[JsonExtensionData]</c>-style flattening is not available on a record, so
/// the properties are restated; the compiler holds the two in step because the
/// constructor takes the DTO whole.
/// </remarks>
public record SearchProductWithSpecsDto(
    string Id,
    string PartNumber,
    string Name,
    string Manufacturer,
    string System,
    string SystemSlug,
    string PartType,
    string PackagingUnit,
    int QuantityPerPackage,
    int? WeightGrams,
    int StockDays,
    double Price,
    string? AppliedRule,
    ImageDto? Image,
    int? Available,
    SearchSupplierDto? Supplier,
    string MatchedOn,
    string? MatchedVia,
    string? MatchedViaManufacturer,
    IReadOnlyList<Spec> Specs,
    int SpecCount)
{
    public SearchProductWithSpecsDto(SearchProductDto p, IReadOnlyList<Spec> specs, int specCount)
        : this(p.Id, p.PartNumber, p.Name, p.Manufacturer, p.System, p.SystemSlug, p.PartType,
               p.PackagingUnit, p.QuantityPerPackage, p.WeightGrams,
               p.StockDays, p.Price, p.AppliedRule, p.Image, p.Available, p.Supplier,
               p.MatchedOn, p.MatchedVia, p.MatchedViaManufacturer, specs, specCount)
    {
    }
}

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
    /// <summary>What one package is called.</summary>
    string PackagingUnit,
    /// <summary>The step an order moves in. One means no constraint.</summary>
    int QuantityPerPackage,
    /// <summary>What one piece weighs, in grams. Null where nobody has weighed it.</summary>
    int? WeightGrams,
    int StockDays,
    double Price,
    string? AppliedRule,
    ImageDto? Image,
    int? Available,
    SearchSupplierDto? Supplier,
    string MatchedOn,
    string? MatchedVia,
    string? MatchedViaManufacturer);
