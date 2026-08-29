using System.Text.Json;
using AutoPartsHub.Api.Catalogue;

namespace AutoPartsHub.Api.Admin;

/// <summary>One row as the uploader sends it, before it is matched to anything.</summary>
/// <param name="PartNumber">What the file called it. <c>ProductId</c> is what it turned out to be.</param>
/// <param name="Price">In the base currency — what gets stored and priced from.</param>
/// <param name="SourcePrice">What the file said, kept only when a conversion actually happened.</param>
/// <param name="SourcePartNumber">
/// The number to store as the supplier's own, set only where the row was
/// matched through the cross-reference rather than on our number. Null reads
/// the same way as a null <paramref name="SourcePrice"/>: nothing was
/// translated, so there is nothing to record.
/// </param>
public record PricedRow(
    string ProductId, string PartNumber, double Price, double? SourcePrice, string? SourceCurrency,
    string? SourcePartNumber);

/// <summary>A line that did not make it, and enough of the file to find it again.</summary>
/// <param name="Line">Where it sat in what was uploaded, 1-based, blank lines counted.</param>
/// <param name="Price">
/// Verbatim, as text. A row is often rejected BECAUSE its price is not a
/// number, and storing that as a number would throw away the only evidence of
/// what the file actually said.
/// </param>
public record RejectedRow(int Line, string PartNumber, string Price, string? Currency, string Reason);

public record ReadResult(List<PricedRow> Rows, List<RejectedRow> Rejected);

/// <summary>
/// What reading a file came to.
/// </summary>
/// <remarks>
/// Its own type rather than <c>Validated&lt;ReadResult&gt;</c> because a
/// refusal has to carry its rejected lines as well as its message. They are
/// the same rows a successful read reports, and a file that failed completely
/// is exactly the one whose lines somebody needs to read — and it produces no
/// price list for them to hang off, so the refusal carries them or they are
/// lost with the response.
/// </remarks>
public record ReadOutcome(bool Ok, ReadResult? Value, string? Error, List<RejectedRow> Rejected);

/// <summary>A price already taken for a part, kept only long enough to be overtaken.</summary>
/// <param name="At">Its slot in the accepted rows, so a later line overwrites in place.</param>
internal record Superseded(int At, int Line, string PartNumber, string Price, string? Currency);

/// <summary>One price on the move, named in the movement summary.</summary>
public record Move(string PartNumber, double From, double To);

/// <summary>What an upload would do to the prices already in force.</summary>
/// <param name="Compared">Rows we have a current cost for. The rest are new parts and cannot move.</param>
/// <param name="Wild">Moved by more than <see cref="PriceLists.WildFold"/>, either way.</param>
public record Movement(
    int Compared, int Rose, int Fell, int Unchanged, int Wild,
    Move? SteepestRise, Move? SteepestFall);

/// <summary>Enough of a Currency row to convert with.</summary>
/// <param name="Rate">Units of this currency per one unit of the base. 1 on the base row.</param>
public record ConversionRate(string Code, double Rate);

public record ListDetails(string Name, string? Description, string? SourceName);

/// <summary>What one upload attempt is written down as.</summary>
/// <param name="PriceListId">Null when the file was refused and no list came of it.</param>
/// <param name="Outcome">STORED or REFUSED — the migration holds it to those two.</param>
/// <param name="Error">The refusal message, and null on a stored import. The pair is checked.</param>
public record ImportWrite(
    string? PriceListId,
    string ListName,
    string? SourceName,
    string? UploadedById,
    string UploadedByName,
    string Outcome,
    int RowsSent,
    int Accepted,
    int Rejected,
    string? Error);

/// <summary>
/// Reading an uploaded purchase-price list.
/// </summary>
/// <remarks>
/// The file is parsed in the browser — the storefront already does that for
/// the bulk lookup — and arrives here as rows. What is left is the part that
/// must not be got wrong: matching each row to a part, and converting what the
/// supplier quoted into the currency the markup engine multiplies up.
/// </remarks>
public static class PriceLists
{
    private const int MaxRows = 50_000;

    /// <summary>
    /// How many rejected lines one upload writes down.
    /// </summary>
    /// <remarks>
    /// A file may carry fifty thousand rows and fail on every one of them. The
    /// first few thousand say what is wrong with it; the next forty-five
    /// thousand say the same thing again, and storing them would let a single
    /// mis-mapped column put a row in that table for every part a supplier
    /// sells. The count of what was rejected is always exact — it is the
    /// transcript that stops.
    /// </remarks>
    public const int StoredRejections = 5_000;

    /// <summary>
    /// Converts a quoted price into the base currency.
    /// </summary>
    /// <remarks>
    /// <paramref name="rate"/> is defined as units of that currency per one
    /// unit of the base — the definition the Currency model states and the
    /// markup engine relies on — so going the other way DIVIDES. The engine
    /// multiplies because it converts base into the customer's currency; this
    /// converts a supplier's currency into the base, which is the same rate
    /// read backwards. Getting it upside down would not throw, it would just
    /// quietly misprice a whole catalogue, which is why it is one named
    /// function rather than an expression at a call site.
    /// </remarks>
    public static double ToBaseCurrency(double price, double rate) => price / rate;

    private static double Round(double value) =>
        Math.Round(value * 100, MidpointRounding.AwayFromZero) / 100;

    /// <summary>What <c>String(value)</c> would give the same cell on the other side.</summary>
    /// <remarks>
    /// Not <see cref="JsonValues.AsString"/>: that renders a number with
    /// <c>GetRawText</c>, which is the file's own spelling — <c>10.50</c> stays
    /// <c>10.50</c>. <c>JSON.parse</c> has thrown that away long before the
    /// other API sees it, so <c>10.50</c> reaches <c>String()</c> as the double
    /// 10.5 and comes out <c>10.5</c>. Both ports write this text into the same
    /// column, so they have to agree, and the JavaScript one is the reference.
    ///
    /// .NET formats a double shortest-round-trip, as JavaScript does, so the
    /// two agree across the whole ordinary range. They part company only where
    /// JavaScript switches to exponent form — at 1e21, and below 1e-6 — which
    /// no price reaches, and where a value that did would have been refused as
    /// a price before anything wrote it down.
    /// </remarks>
    private static string RawText(JsonElement? element) => element switch
    {
        null => "",
        { ValueKind: JsonValueKind.Number } e =>
            e.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => JsonValues.AsString(element),
    };

    /// <summary>
    /// Matches rows to parts and converts them.
    /// </summary>
    /// <remarks>
    /// Part numbers are compared on their normalised form, the same way search
    /// and the bulk lookup compare them, so a list that writes
    /// <c>0 986 424 815</c> still lands on the part stored as
    /// <c>0986424815</c>.
    ///
    /// A number that matches no part of ours falls through to the
    /// cross-reference, because suppliers quote their own numbering and it is
    /// the only numbering they have. Two rules keep that from becoming a way
    /// to misprice quietly: only an EXACT interchange counts — a close or
    /// partial substitute is a different part and its cost is not this part's
    /// cost — and an ambiguous one is refused rather than guessed at, since
    /// picking one of two parts to price would be a coin toss nobody can see
    /// happening.
    ///
    /// Nothing is rejected silently: every row that finds no part, names a
    /// currency nobody maintains, or carries a price that is not a number
    /// comes back in <c>Rejected</c> with a reason, so the admin sees what a
    /// file failed to cover rather than discovering it as a wrong price later.
    /// </remarks>
    /// <param name="productIdsByNormalisedInterchange">
    /// Normalised cross-reference number to the parts it is exactly equivalent
    /// to. A list rather than a single id because a number can be equivalent
    /// to several of ours, and that case has to be visible to be refused.
    /// </param>
    public static ReadOutcome ReadPriceRows(
        JsonElement? raw,
        Dictionary<string, string> productIdByNormalisedPartNumber,
        Dictionary<string, List<string>> productIdsByNormalisedInterchange,
        Dictionary<string, ConversionRate> ratesByCode)
    {
        if (raw is not { ValueKind: JsonValueKind.Array } list)
        {
            return Refuse("Expected a list of rows.");
        }

        var length = list.GetArrayLength();
        if (length == 0) return Refuse("That file has no rows in it.");
        if (length > MaxRows)
        {
            return Refuse(
                $"A price list can carry at most {MaxRows.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} rows.");
        }

        var rows = new List<PricedRow>();
        var rejected = new List<RejectedRow>();

        // Where each part's price came from, so a repeat can name the line it
        // supersedes. The last one in the file still wins — but the line it
        // beat is the one worth reporting, because that is the figure the admin
        // will go looking for when the price on screen is not the one they
        // remember sending.
        var placed = new Dictionary<string, Superseded>();

        var line = 0;

        foreach (var entry in list.EnumerateArray())
        {
            line++;
            // `typeof null === 'object'` on the other side, but the guard there
            // is `!entry || typeof entry !== 'object'`, so null fails it too.
            // An array passes it there and would here as well; a spreadsheet
            // exporter emitting arrays of cells has no partNumber key either
            // way, so it lands on the same blank-line branch below.
            if (entry.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            {
                return Refuse("Every row must be an object.");
            }

            var partNumber = JsonValues.AsString(JsonValues.Get(entry, "partNumber")).Trim();
            if (partNumber.Length == 0) continue; // a blank line in a spreadsheet is not an error

            var priceField = JsonValues.Get(entry, "price");
            var rawPrice = RawText(priceField);
            var rawCurrencyText = JsonValues.AsString(JsonValues.Get(entry, "currency")).Trim();
            var rawCurrency = rawCurrencyText.Length > 0 ? rawCurrencyText : null;

            var at = line;
            void Reject(string reason) =>
                rejected.Add(new RejectedRow(at, partNumber, rawPrice, rawCurrency, reason));

            // Number(row.price) with no fallback: an absent price is NaN, not
            // zero, and a free part is not what a missing column means.
            var price = priceField is null ? null : JsonValues.AsNumber(priceField);
            if (price is not { } quoted || double.IsNaN(quoted) || double.IsInfinity(quoted) || quoted < 0)
            {
                Reject("Price is not a number of zero or more.");
                continue;
            }

            var normalised = PartNumbers.Normalise(partNumber);
            string? sourcePartNumber = null;

            if (!productIdByNormalisedPartNumber.TryGetValue(normalised, out var productId))
            {
                // Our own numbering does not know it. The cross-reference
                // might, and a supplier quoting their own numbers is the
                // ordinary case rather than the exception.
                var equivalents = productIdsByNormalisedInterchange.TryGetValue(normalised, out var found)
                    ? found
                    : [];

                if (equivalents.Count > 1)
                {
                    // Two of our parts claim the same equivalent. Picking one
                    // would be a coin toss that mispriced a part with nothing
                    // on screen to show it happened, so the row goes back to
                    // be priced by our own number.
                    Reject($"That number is interchangeable with {equivalents.Count} of our parts; "
                         + "price it by our own number instead.");
                    continue;
                }

                if (equivalents.Count == 0)
                {
                    Reject("No part in the catalogue matches that number.");
                    continue;
                }

                productId = equivalents[0];
                sourcePartNumber = partNumber;
            }

            var code = (rawCurrency ?? "").ToUpperInvariant();
            var basePrice = quoted;
            double? sourcePrice = null;
            string? sourceCurrency = null;

            if (code.Length > 0)
            {
                if (!ratesByCode.TryGetValue(code, out var currency))
                {
                    Reject($"No currency called {code} is set up.");
                    continue;
                }
                if (!(currency.Rate > 0))
                {
                    Reject($"{code} has no usable rate.");
                    continue;
                }
                // Only recorded as converted when it actually was: a list
                // already in the base currency should not carry a "source"
                // that says the same number.
                if (currency.Rate != 1)
                {
                    basePrice = ToBaseCurrency(quoted, currency.Rate);
                    sourcePrice = quoted;
                    sourceCurrency = code;
                }
            }

            var priced = new PricedRow(
                productId, partNumber, Round(basePrice), sourcePrice, sourceCurrency, sourcePartNumber);

            if (placed.TryGetValue(productId, out var previous))
            {
                // The earlier line is the one that lost, so the earlier line is
                // the one reported — carrying the price IT quoted, not the
                // price that beat it.
                rejected.Add(new RejectedRow(
                    previous.Line, previous.PartNumber, previous.Price, previous.Currency,
                    $"That part appears more than once; line {line} was used instead."));
                rows[previous.At] = priced;
                placed[productId] = new Superseded(previous.At, line, partNumber, rawPrice, rawCurrency);
                continue;
            }

            placed[productId] = new Superseded(rows.Count, line, partNumber, rawPrice, rawCurrency);
            rows.Add(priced);
        }

        if (rows.Count == 0)
        {
            // Nothing survived. Say which reason accounted for most of it
            // rather than assuming the part numbers were wrong: a file that
            // matched every part and named a currency nobody has set up fails
            // just as completely, and sending the admin to check part numbers
            // would be sending them to the wrong place.
            //
            // The rejections travel with the refusal. This is the case where
            // they are worth most — a file that failed entirely is the one
            // somebody will want to read line by line — and there is no list
            // for them to hang off, so the refusal has to carry them or they
            // are lost.
            return Refuse($"Not one row could be used. {CommonestReason(rejected)}", rejected);
        }

        return new ReadOutcome(true, new ReadResult(rows, rejected), null, rejected);
    }

    private static ReadOutcome Refuse(string error, List<RejectedRow>? rejected = null) =>
        new(false, null, error, rejected ?? []);

    /* ---------------------------------- what a file does to prices --- */

    /// <summary>
    /// How far one price may move before the move counts as wild: five-fold,
    /// in either direction.
    /// </summary>
    /// <remarks>
    /// Chosen to sit above every legitimate reason a purchase price changes
    /// and below every way a column can be read wrong. A supplier's annual
    /// rise, a currency sliding by half, even a price doubling — all ordinary,
    /// all under five. A price column that is really the description column,
    /// or a list quoted in a currency fifty times the base with the currency
    /// column left off, is nowhere near it.
    /// </remarks>
    public const double WildFold = 5;

    /// <summary>How much of a file may move wildly before the file is refused.</summary>
    public const double WildShare = 0.5;

    /// <summary>
    /// How many comparable rows a file needs before this judges it at all.
    /// </summary>
    /// <remarks>
    /// A file of three deliberate corrections is not evidence of anything, and
    /// refusing it would make the guard an obstacle rather than a warning. The
    /// signature being caught is a whole column read wrong, and a whole column
    /// is never three rows.
    /// </remarks>
    public const int EnoughToJudge = 20;

    /// <summary>
    /// What an upload would do to the prices already in force.
    /// </summary>
    /// <remarks>
    /// The counts matter more than any single row. A real price update moves
    /// most parts a little and a few of them a lot; a column read wrong moves
    /// nearly everything by an absurd multiple, and that difference is visible
    /// in the shape of the file long before anyone reads a line of it.
    ///
    /// A part with no current cost is not counted. Its price is not moving, it
    /// is arriving, and counting arrivals as moves of infinity would make
    /// every first upload look like a disaster.
    /// </remarks>
    public static Movement ReadPriceMovement(
        IReadOnlyList<PricedRow> rows, Dictionary<string, double> costNowByProductId)
    {
        var compared = 0;
        var rose = 0;
        var fell = 0;
        var unchanged = 0;
        var wild = 0;
        Move? steepestRise = null;
        Move? steepestFall = null;

        foreach (var row in rows)
        {
            // Zero is a real price and not a missing one — but nothing can be
            // a multiple of zero, so it is left out of the comparison rather
            // than treated as an infinite rise.
            if (!costNowByProductId.TryGetValue(row.ProductId, out var from) || !(from > 0)) continue;

            compared++;

            var to = row.Price;
            if (to == from)
            {
                unchanged++;
                continue;
            }

            var move = new Move(row.PartNumber, from, to);

            if (to > from)
            {
                rose++;
                if (to / from > (steepestRise is null ? 0 : steepestRise.To / steepestRise.From))
                {
                    steepestRise = move;
                }
                if (to / from > WildFold) wild++;
            }
            else
            {
                fell++;
                // Written as a division rather than as a special case for
                // zero: a double divided by zero is +Infinity here as it is in
                // JavaScript, so a fall to zero beats any finite fall and ties
                // with an earlier one, which is what the other API does.
                if (from / to > (steepestFall is null ? 0 : steepestFall.From / steepestFall.To))
                {
                    steepestFall = move;
                }
                // A fall to zero is a fall past any multiple, so it is wild by
                // definition rather than by division.
                if (to == 0 || from / to > WildFold) wild++;
            }
        }

        return new Movement(compared, rose, fell, unchanged, wild, steepestRise, steepestFall);
    }

    /// <summary>
    /// Whether a file has the shape of a column read wrong.
    /// </summary>
    /// <remarks>
    /// Deliberately about the file and not about a row. One part whose cost
    /// has gone up tenfold is a supplier being difficult; two thousand of them
    /// is a spreadsheet whose price column is somewhere else, and the second
    /// is the one worth stopping. Under <see cref="EnoughToJudge"/> comparable
    /// rows it never fires.
    /// </remarks>
    public static bool MovementIsAlarming(Movement movement) =>
        movement.Compared >= EnoughToJudge && movement.Wild > movement.Compared * WildShare;

    /// <summary>The refusal an alarming file earns, with the numbers that earned it.</summary>
    public static string MovementRefusal(Movement movement)
    {
        var steepest = movement.SteepestRise ?? movement.SteepestFall;
        var example = steepest is null
            ? ""
            : $" {steepest.PartNumber} goes from {Number(steepest.From)} to {Number(steepest.To)}.";

        return $"{movement.Wild} of the {movement.Compared} parts this file already prices move by "
             + $"more than {Number(WildFold)} times.{example} That is the shape of a column read "
             + "from the wrong place. Check which column the prices came from, and which currency "
             + "they are in — or send it again with `confirmLargeChange` if the move is real.";
    }

    /// <summary>A number in a message, spelt the way the other API spells it.</summary>
    private static string Number(double value) =>
        value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The reason that explains most of a wholly-rejected file, for its message.</summary>
    private static string CommonestReason(List<RejectedRow> rejected)
    {
        if (rejected.Count == 0) return "The file had no usable rows.";

        // Grouped in first-seen order and sorted with a stable sort, so a tie
        // between two reasons resolves to whichever the file hit first — twice
        // running, and the same way the other API resolves it.
        var counts = rejected.GroupBy(r => r.Reason)
            .Select(g => (Reason: g.Key, Count: g.Count()))
            .OrderByDescending(x => x.Count)
            .ToList();

        var (reason, count) = counts[0];

        return count == rejected.Count
            ? reason
            : $"Most often: {reason} ({count} of {rejected.Count}).";
    }

    public static Validated<ListDetails> ReadListDetails(JsonElement body)
    {
        var name = JsonValues.AsString(JsonValues.Get(body, "name")).Trim();
        if (name.Length == 0) return Validators.Fail<ListDetails>("Give the list a name.");
        if (name.Length > 120) return Validators.Fail<ListDetails>("Keep the name under 120 characters.");

        var description = JsonValues.AsString(JsonValues.Get(body, "description")).Trim();
        var sourceName = JsonValues.AsString(JsonValues.Get(body, "sourceName")).Trim();

        return Validators.Ok(new ListDetails(
            name,
            description.Length > 0 ? description : null,
            sourceName.Length > 0 ? sourceName : null));
    }
}
