using System.Text.Json;
using AutoPartsHub.Api.Catalogue;
using AutoPartsHub.Api.Data;
using AutoPartsHub.Api.Pricing;
using AutoPartsHub.Api.Endpoints;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsHub.Api.Admin;

/// <summary>
/// Reading, checking and writing markup rules and their conditions.
/// </summary>
/// <remarks>
/// One module because the read and the two writes have to agree: a rule that
/// can be created but not edited back into the same shape is a rule the admin
/// can get stuck with, and a rule listed in one order but ranked in another is
/// a screen that cannot be used to work out which rule wins.
///
/// The mirror of lib/pricing-admin.ts and lib/markup-rule-input.ts.
/// </remarks>
public static class MarkupRules
{
    /* ------------------------------------------------------------ reading --- */

    /// <summary>
    /// Rules in the order the engine ranks them, each carrying its conditions.
    /// </summary>
    /// <remarks>
    /// Most specific first and priority breaking ties, which is exactly how
    /// <see cref="PricingEngine"/> picks a winner — so the list reads top to
    /// bottom as "this is the one that applies, unless it does not match, in
    /// which case this one".
    ///
    /// Two queries rather than a join: a join repeats every rule once per
    /// condition and has to be folded back together in memory anyway.
    /// </remarks>
    public static async Task<List<object>> Read(
        AutoPartsContext db, string? id, CancellationToken ct)
    {
        var rules = await db.Database.SqlQuery<MarkupRuleHeadRow>($"""
            SELECT r."id" AS "Id", r."label" AS "Label", r."priority" AS "Priority",
                   r."specificity" AS "Specificity",
                   r."purchasePriceFrom" AS "PurchasePriceFrom",
                   r."purchasePriceTo" AS "PurchasePriceTo",
                   r."type" AS "Type", r."value" AS "Value", r."active" AS "Active",
                   r."minAmount" AS "MinAmount",
                   r."startsAt" AS "StartsAt", r."endsAt" AS "EndsAt"
            FROM "MarkupRule" r
            WHERE ({id}::text IS NULL OR r."id" = {id})
            ORDER BY r."specificity" DESC, r."priority" DESC, r."id" ASC
            """).ToListAsync(ct);

        // The joins are what turns `supplier = cms1a32bs…` into `IB16`. One per
        // dimension that points at a table, each guarded by the dimension name
        // so only one can fire, and the raw value as the last fallback for the
        // dimensions that are text in the first place — a part number prefix
        // has no name to look up, it IS the name.
        var conditions = await db.Database.SqlQuery<ConditionRow>($"""
            SELECT c."ruleId" AS "RuleId", c."dimension" AS "Dimension", c."value" AS "Value",
                   c."negated" AS "Negated",
                   COALESCE(cc."name", s."name", g."name", vs."name", cl."name", sm."name",
                            pl."name", cu."code", c."value") AS "Label"
            FROM "MarkupRuleCondition" c
            LEFT JOIN "ClientCategory" cc ON c."dimension" = 'clientCategory' AND cc."id" = c."value"
            LEFT JOIN "Supplier" s        ON c."dimension" = 'supplier'       AND s."id" = c."value"
            LEFT JOIN "GoodsCategory" g   ON c."dimension" = 'goodsCategory'  AND g."id" = c."value"
            LEFT JOIN "VehicleSystem" vs  ON c."dimension" = 'vehicleSystem'  AND vs."slug" = c."value"
            LEFT JOIN "Client" cl         ON c."dimension" = 'client'         AND cl."id" = c."value"
            LEFT JOIN "Client" sm         ON c."dimension" = 'salesManager'   AND sm."id" = c."value"
            LEFT JOIN "PriceList" pl      ON c."dimension" = 'priceList'      AND pl."id" = c."value"
            LEFT JOIN "Currency" cu       ON c."dimension" = 'currency'       AND cu."code" = c."value"
            WHERE ({id}::text IS NULL OR c."ruleId" = {id})
            ORDER BY c."ruleId" ASC, c."dimension" ASC, c."negated" ASC, c."value" ASC
            """).ToListAsync(ct);

        var byRule = conditions
            .GroupBy(c => c.RuleId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        return [.. rules.Select(r => (object)new
        {
            id = r.Id,
            label = r.Label,
            priority = r.Priority,
            conditions = Group(byRule.GetValueOrDefault(r.Id, [])),
            specificity = r.Specificity,
            purchasePriceFrom = r.PurchasePriceFrom,
            purchasePriceTo = r.PurchasePriceTo,
            type = r.Type,
            value = r.Value,
            minAmount = r.MinAmount,
            startsAt = r.StartsAt,
            endsAt = r.EndsAt,
            active = r.Active,
        })];
    }

    public static async Task<object?> ById(AutoPartsContext db, string id, CancellationToken ct) =>
        (await Read(db, id, ct)).FirstOrDefault();

    /// <summary>
    /// The stored flat rows, grouped back into the shape the API hands out.
    /// </summary>
    /// <remarks>
    /// In vocabulary order, so two rules list their conditions the same way and
    /// the form reads top to bottom the way it is laid out.
    /// </remarks>
    private static List<object> Group(List<ConditionRow> rows)
    {
        var byDimension = rows
            .GroupBy(r => r.Dimension, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        return [.. MarkupDimensions.All
            .Where(d => byDimension.ContainsKey(d.Name))
            .Select(d => (object)new
            {
                dimension = d.Name,
                label = d.Label,
                // The write path keeps a dimension pointing one way, so the
                // first row speaks for the group.
                exclude = byDimension[d.Name][0].Negated,
                values = byDimension[d.Name]
                    .Select(r => new { value = r.Value, label = r.Label })
                    .ToList(),
            })];
    }

    /* --------------------------------------------------------- validation --- */

    /// <summary>A rule as submitted, once it has been checked.</summary>
    public record RuleWrite(
        string Label, int Priority, List<RuleCondition> Conditions,
        double? PurchasePriceFrom, double? PurchasePriceTo, string Type, double Value,
        double? MinAmount, DateTime? StartsAt, DateTime? EndsAt)
    {
        /// <summary>Whether it narrows on purchase price at all — either bound counts.</summary>
        public bool HasRange => PurchasePriceFrom is not null || PurchasePriceTo is not null;
    }

    /// <summary>
    /// Reads a submitted rule, or says why it cannot be read.
    /// </summary>
    /// <remarks>
    /// Duplicate conditions are merged rather than refused — the same dimension
    /// sent twice, or the same value twice within one — because the unique
    /// index would refuse the write anyway, and a form that repeats itself is
    /// not a request anybody needs told off for. An unknown dimension IS
    /// refused: it would be stored, match nothing, and quietly make the rule
    /// dead.
    /// </remarks>
    public static (RuleWrite? Rule, string? Error) ParseBody(JsonElement body)
    {
        var label = JsonValues.AsString(JsonValues.Get(body, "label")).Trim();
        if (label.Length == 0) return (null, "Label is required.");

        var type = JsonValues.Get(body, "type") is { } t ? JsonValues.AsString(t) : "PERCENT";
        if (type.Length == 0) type = "PERCENT";
        // Read from the shared vocabulary rather than written out here. This
        // list said PERCENT, AMOUNT, FIXED while the floor rule below already
        // handled PERCENT_MIN — so the endpoint refused the type before the
        // code for it could run, the other API accepted it, and the refusal
        // named three types where the other API named four. Two APIs refusing
        // the same thing in different words are two products; one refusing what
        // the other accepts is worse.
        if (!MarkupTypes.All.Contains(type))
        {
            return (null, $"Adjustment type must be {string.Join(", ", MarkupTypes.All)}.");
        }

        var value = JsonValues.AsNumber(JsonValues.Get(body, "value")) ?? double.NaN;
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return (null, "Value must be a number.");
        }

        double? OptNum(string key)
        {
            var raw = JsonValues.Get(body, key);
            if (raw is null || raw.Value.ValueKind is JsonValueKind.Null
                || (raw.Value.ValueKind == JsonValueKind.String && raw.Value.GetString()!.Length == 0))
            {
                return null;
            }
            var n = JsonValues.AsNumber(raw);
            return n is null || double.IsNaN(n.Value) || double.IsInfinity(n.Value) ? null : n;
        }

        var from = OptNum("purchasePriceFrom");
        var to = OptNum("purchasePriceTo");
        if (from is not null && to is not null && from > to)
        {
            return (null, "Price band starts above where it ends.");
        }

        // The floor belongs to exactly one type. Set on any other it would do
        // nothing, which is worse than being refused; missing on this one, the
        // type would be a plain percentage wearing a different name. The
        // database enforces the same pair, both directions.
        var minAmount = OptNum("minAmount");
        if (type == "PERCENT_MIN")
        {
            if (minAmount is null)
            {
                return (null, "A percentage with a floor needs the floor. Set a minimum amount.");
            }
            if (minAmount < 0)
            {
                return (null, "A floor below nothing is a floor that never applies.");
            }
        }
        else if (minAmount is not null)
        {
            return (null, "A minimum amount only applies to a percentage with a floor.");
        }

        var window = MarkupWindow.Read(
            JsonValues.AsString(JsonValues.Get(body, "startsAt")),
            JsonValues.AsString(JsonValues.Get(body, "endsAt")));
        if (!window.Ok) return (null, window.Error);

        var (conditions, error) = ParseConditions(JsonValues.Get(body, "conditions"));
        if (error is not null) return (null, error);

        return (new RuleWrite(
            label, (int)(OptNum("priority") ?? 0), conditions!, from, to, type, value,
            minAmount, window.StartsAt, window.EndsAt), null);
    }

    private static (List<RuleCondition>? Conditions, string? Error) ParseConditions(JsonElement? raw)
    {
        if (raw is null || raw.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return ([], null);
        }
        if (raw.Value.ValueKind != JsonValueKind.Array) return (null, "Conditions must be a list.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var direction = new Dictionary<string, bool>(StringComparer.Ordinal);
        var conditions = new List<RuleCondition>();

        foreach (var entry in raw.Value.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                return (null, "Each condition needs a dimension and a list of values.");
            }

            var name = JsonValues.AsString(JsonValues.Get(entry, "dimension")).Trim();
            if (!MarkupDimensions.IsDimension(name))
            {
                return (null, $"Unknown condition \"{name}\".");
            }

            // A dimension points one way or the other. "Any of these three
            // suppliers, except this one" is not a sentence the engine can
            // answer, and it is far more likely to be a form bug than
            // something somebody meant.
            var exclude = JsonValues.Get(entry, "exclude") is { ValueKind: JsonValueKind.True };
            if (direction.TryGetValue(name, out var already) && already != exclude)
            {
                return (null,
                    $"\"{name}\" is listed as both a match and an exclusion. " +
                    "It can be one or the other.");
            }
            direction[name] = exclude;

            var values = JsonValues.Get(entry, "values");
            if (values is not { ValueKind: JsonValueKind.Array })
            {
                return (null, $"\"{name}\" needs a list of values.");
            }

            var count = values.Value.GetArrayLength();
            if (count == 0)
            {
                return (null, $"\"{name}\" names no values. Leave it out to mean \"any\".");
            }
            if (count > MarkupDimensions.MaxConditionValues)
            {
                return (null,
                    $"\"{name}\" names more than {MarkupDimensions.MaxConditionValues} values.");
            }

            foreach (var each in values.Value.EnumerateArray())
            {
                var value = JsonValues.AsString(each).Trim();
                if (value.Length == 0) return (null, $"\"{name}\" has a blank value.");
                if (value.Length > MarkupDimensions.MaxConditionLength)
                {
                    return (null, $"A value on \"{name}\" is longer than " +
                        $"{MarkupDimensions.MaxConditionLength} characters.");
                }

                if (!seen.Add($"{name} {value}")) continue;
                conditions.Add(new RuleCondition(name, value, exclude));
            }
        }

        return (conditions, null);
    }

    /* ------------------------------------------------------------ writing --- */

    /// <summary>
    /// Creates a rule and its conditions in one transaction.
    /// </summary>
    /// <remarks>
    /// Specificity is computed here rather than trusted from the caller,
    /// because it is what decides which rule wins, and a number the client
    /// could set is a number the client could use to jump the queue.
    /// </remarks>
    public static async Task<string> Create(
        AutoPartsContext db, RuleWrite input, CancellationToken ct)
    {
        var id = Ids.New();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "MarkupRule" ("id", "label", "priority", "specificity",
                                      "purchasePriceFrom", "purchasePriceTo", "type", "value",
                                      "minAmount", "startsAt", "endsAt")
            VALUES ({id}, {input.Label}, {input.Priority},
                    {MarkupDimensions.SpecificityOf(input.Conditions, input.HasRange)},
                    {input.PurchasePriceFrom}, {input.PurchasePriceTo},
                    {input.Type}, {input.Value},
                    {input.MinAmount}, {input.StartsAt}, {input.EndsAt})
            """, ct);

        await ReplaceConditions(db, id, input.Conditions, ct);
        await transaction.CommitAsync(ct);

        return id;
    }

    /// <summary>Writes a whole ladder of bands, or none of them.</summary>
    /// <remarks>
    /// One transaction rather than a loop over <see cref="Create"/>: half a
    /// ladder prices half the catalogue from bands somebody chose and the other
    /// half from the tier default, which is the failure the ladder validation
    /// exists to prevent — and it would be a failure nobody could see on the
    /// rules list, because every band that landed looks right on its own.
    /// </remarks>
    public static async Task<List<string>> CreateLadder(
        AutoPartsContext db, IReadOnlyList<RuleWrite> inputs, CancellationToken ct)
    {
        var ids = inputs.Select(_ => Ids.New()).ToList();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];

            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "MarkupRule" ("id", "label", "priority", "specificity",
                                          "purchasePriceFrom", "purchasePriceTo", "type", "value",
                                          "minAmount", "startsAt", "endsAt")
                VALUES ({ids[i]}, {input.Label}, {input.Priority},
                        {MarkupDimensions.SpecificityOf(input.Conditions, input.HasRange)},
                        {input.PurchasePriceFrom}, {input.PurchasePriceTo},
                        {input.Type}, {input.Value},
                        {input.MinAmount}, {input.StartsAt}, {input.EndsAt})
                """, ct);

            await ReplaceConditions(db, ids[i], input.Conditions, ct);
        }

        await transaction.CommitAsync(ct);

        return ids;
    }

    /// <summary>Rewrites a rule, conditions and all, and recomputes what it ranks by.</summary>
    public static async Task Update(
        AutoPartsContext db, string id, RuleWrite input, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        await db.Database.ExecuteSqlAsync($"""
            UPDATE "MarkupRule"
               SET "label" = {input.Label},
                   "priority" = {input.Priority},
                   "specificity" = {MarkupDimensions.SpecificityOf(input.Conditions, input.HasRange)},
                   "purchasePriceFrom" = {input.PurchasePriceFrom},
                   "purchasePriceTo" = {input.PurchasePriceTo},
                   "type" = {input.Type},
                   "value" = {input.Value},
                   "minAmount" = {input.MinAmount},
                   "startsAt" = {input.StartsAt},
                   "endsAt" = {input.EndsAt}
             WHERE "id" = {id}
            """, ct);

        await ReplaceConditions(db, id, input.Conditions, ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// Writes a rule's conditions, replacing whatever it had.
    /// </summary>
    /// <remarks>
    /// Wholesale rather than a diff: a rule holds a handful of rows, and
    /// working out which to add and which to drop costs more than writing them
    /// again.
    /// </remarks>
    private static async Task ReplaceConditions(
        AutoPartsContext db, string ruleId, List<RuleCondition> conditions, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync(
            $"""DELETE FROM "MarkupRuleCondition" WHERE "ruleId" = {ruleId}""", ct);

        if (conditions.Count == 0) return;

        var ids = conditions.Select(_ => Ids.New()).ToArray();
        var ruleIds = conditions.Select(_ => ruleId).ToArray();
        var dimensions = conditions.Select(c => c.Dimension).ToArray();
        var values = conditions.Select(c => c.Value).ToArray();
        var negated = conditions.Select(c => c.Negated).ToArray();

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "MarkupRuleCondition" ("id", "ruleId", "dimension", "value", "negated")
            SELECT * FROM unnest({ids}::text[], {ruleIds}::text[],
                                 {dimensions}::text[], {values}::text[], {negated}::boolean[])
            """, ct);
    }

    /// <summary>
    /// Forgets every condition naming a value that has ceased to exist, and
    /// re-ranks the rules that held one.
    /// </summary>
    /// <remarks>
    /// A condition value is plain text with no foreign key behind it, which is
    /// what lets one table hold fourteen kinds of reference. The cost is that
    /// deleting the thing it names cannot cascade: left alone, the rule would
    /// go on asking for a goods category nothing is in, match nothing, and be
    /// dead without looking dead. Dropping the condition instead WIDENS the
    /// rule on that dimension, which is the right reading — the rule said "and
    /// in this category", the category is gone, so that clause is gone too.
    ///
    /// Returns how many rules were widened, so a delete can say so.
    /// </remarks>
    public static async Task<int> ForgetValue(
        AutoPartsContext db, string dimension, string value, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var gone = await db.Database.SqlQuery<string>($"""
            DELETE FROM "MarkupRuleCondition"
             WHERE "dimension" = {dimension} AND "value" = {value}
            RETURNING "ruleId" AS "Value"
            """).ToListAsync(ct);

        var ids = gone.Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0)
        {
            await transaction.CommitAsync(ct);
            return 0;
        }

        // What each of those rules narrows on now. LEFT JOIN because a rule
        // whose only condition was the one just dropped still needs its score
        // set — to zero, or to one if it has a price band.
        var remaining = await db.Database.SqlQuery<RemainingRow>($"""
            SELECT r."id" AS "Id", r."purchasePriceFrom" AS "PurchasePriceFrom",
                   r."purchasePriceTo" AS "PurchasePriceTo", c."dimension" AS "Dimension"
            FROM "MarkupRule" r
            LEFT JOIN "MarkupRuleCondition" c ON c."ruleId" = r."id"
            WHERE r."id" = ANY({ids}::text[])
            """).ToListAsync(ct);

        foreach (var group in remaining.GroupBy(r => r.Id, StringComparer.Ordinal))
        {
            var conditions = group
                .Where(r => r.Dimension is not null)
                .Select(r => new RuleCondition(r.Dimension!, ""))
                .ToList();
            var head = group.First();
            var score = MarkupDimensions.SpecificityOf(
                conditions, head.PurchasePriceFrom is not null || head.PurchasePriceTo is not null);

            await db.Database.ExecuteSqlAsync(
                $"""UPDATE "MarkupRule" SET "specificity" = {score} WHERE "id" = {group.Key}""", ct);
        }

        await transaction.CommitAsync(ct);
        return ids.Length;
    }

    /* ------------------------------------------------------------ options --- */

    /// <summary>
    /// The dimensions the builder offers, and what each of them can be set to.
    /// </summary>
    /// <remarks>
    /// A dimension missing from <c>values</c> is one with no list to choose
    /// from — a part number prefix, a word in a name — and the form gives it a
    /// text box.
    /// </remarks>
    public static async Task<object> Options(AutoPartsContext db, CancellationToken ct)
    {
        var categories = await Named(db, """
            SELECT "id" AS "Value", "name" AS "Label" FROM "ClientCategory"
            ORDER BY "markupPercent" ASC
            """, ct);
        var suppliers = await Named(db, """
            SELECT "id" AS "Value", "name" AS "Label" FROM "Supplier" ORDER BY "name" ASC
            """, ct);
        var systems = await Named(db, """
            SELECT "slug" AS "Value", "name" AS "Label" FROM "VehicleSystem" ORDER BY "order" ASC
            """, ct);
        // Active only, for the same reason the product form offers only active
        // ones: a rule aimed at a retired category is a rule that never fires.
        var goodsCategories = await Named(db, """
            SELECT "id" AS "Value", "name" AS "Label" FROM "GoodsCategory" WHERE "active"
            ORDER BY "sortOrder" ASC, "name" ASC
            """, ct);
        // Matched on the brand NAME, not its id: that is what a part carries
        // into the pricing context, and what somebody writing a rule would type.
        var manufacturers = await Named(db, """
            SELECT "name" AS "Value", "name" AS "Label" FROM "Manufacturer" ORDER BY "name" ASC
            """, ct);
        // Accounts that buy. Staff are offered under "sales manager" instead,
        // and a rule aimed at an admin account would price nothing.
        var clients = await Named(db, """
            SELECT "id" AS "Value", "name" || ' (' || "email" || ')' AS "Label" FROM "Client"
            WHERE "role" IN ('RETAIL', 'B2B') ORDER BY "name" ASC
            """, ct);
        var salesManagers = await Named(db, """
            SELECT "id" AS "Value", "name" AS "Label" FROM "Client"
            WHERE "role" = 'SALES' ORDER BY "name" ASC
            """, ct);
        // Whatever customers have actually written, rather than a list of every
        // city in the country. A rule for a city nobody is in prices nothing.
        var cities = await Named(db, """
            SELECT DISTINCT "city" AS "Value", "city" AS "Label" FROM "Client"
            WHERE "city" IS NOT NULL AND "city" <> '' ORDER BY 1 ASC
            """, ct);
        // Matched on code, not id — that is what the engine compares, because a
        // context knows what it is quoting in without looking the row up.
        var currencies = await Named(db, """
            SELECT "code" AS "Value", "code" || ' — ' || "name" AS "Label" FROM "Currency"
            ORDER BY "isBase" DESC, "code" ASC
            """, ct);
        var priceLists = await Named(db, """
            SELECT "id" AS "Value", "name" AS "Label" FROM "PriceList"
            ORDER BY "active" DESC, "createdAt" DESC
            """, ct);

        return new
        {
            // Projected rather than serialised straight, so `match` reads as a
            // word on both APIs instead of an enum's ordinal on one of them.
            dimensions = MarkupDimensions.All.Select(d => new
            {
                name = d.Name,
                label = d.Label,
                match = MatchName(d.Match),
                side = d.Side,
                hint = d.Hint,
            }),
            values = new Dictionary<string, List<ConditionValue>>(StringComparer.Ordinal)
            {
                ["clientCategory"] = categories,
                ["supplier"] = suppliers,
                ["vehicleSystem"] = systems,
                ["goodsCategory"] = goodsCategories,
                ["manufacturer"] = manufacturers,
                ["partType"] = [.. PartTypes.Select(t => new ConditionValue(t, t))],
                ["client"] = clients,
                ["clientRole"] = [.. Roles.Select(r => new ConditionValue(r, r))],
                ["salesManager"] = salesManagers,
                ["city"] = cities,
                ["currency"] = currencies,
                ["priceList"] = priceLists,
            },
        };
    }

    /// <summary>What a part can be. The mirror of PART_TYPES in lib/admin-products.ts.</summary>
    private static readonly string[] PartTypes = ["oem", "aftermarket", "substitute"];

    /// <summary>The mirror of ROLES in lib/auth.ts.</summary>
    private static readonly string[] Roles = ["ADMIN", "SALES", "SUPPLIER", "B2B", "RETAIL"];

    private static string MatchName(MatchKind kind) => kind switch
    {
        MatchKind.Exact => "exact",
        MatchKind.Insensitive => "insensitive",
        MatchKind.Prefix => "prefix",
        MatchKind.Contains => "contains",
        _ => "exact",
    };

    private static async Task<List<ConditionValue>> Named(
        AutoPartsContext db, string sql, CancellationToken ct) =>
        await db.Database.SqlQueryRaw<ConditionValue>(sql).ToListAsync(ct);

    /// <summary>One acceptable answer, with something a person can read instead of an id.</summary>
    public record ConditionValue(string Value, string Label);

    private record MarkupRuleHeadRow(
        string Id, string Label, int Priority, int Specificity,
        double? PurchasePriceFrom, double? PurchasePriceTo,
        string Type, double Value, bool Active,
        double? MinAmount, DateTime? StartsAt, DateTime? EndsAt);

    private record ConditionRow(
        string RuleId, string Dimension, string Value, bool Negated, string Label);

    private record RemainingRow(
        string Id, double? PurchasePriceFrom, double? PurchasePriceTo, string? Dimension);
}
