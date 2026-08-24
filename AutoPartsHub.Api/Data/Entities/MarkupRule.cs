using System;
using System.Collections.Generic;

namespace AutoPartsHub.Api.Data.Entities;

public partial class MarkupRule
{
    public string Id { get; set; } = null!;

    public string Label { get; set; } = null!;

    public int Priority { get; set; }

    /// <summary>
    /// How many dimensions this rule narrows on, counted once each.
    ///
    /// Recomputed on every save rather than derived per request, so the engine
    /// can order by it and so the number a rule was ranked by is readable
    /// rather than inferred.
    /// </summary>
    public int Specificity { get; set; }

    public double? PurchasePriceFrom { get; set; }

    public double? PurchasePriceTo { get; set; }

    public string Type { get; set; } = null!;

    public double Value { get; set; }

    /// <summary>The floor under PERCENT_MIN, and null on every other type.</summary>
    public double? MinAmount { get; set; }

    /// <summary>
    /// When the rule is in force. Outside its window it is skipped entirely,
    /// which is not the same as being switched off.
    /// </summary>
    public DateTime? StartsAt { get; set; }

    public DateTime? EndsAt { get; set; }

    public bool Active { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// What this rule narrows on. All the conditions sharing a dimension are
    /// one statement with several acceptable answers.
    /// </summary>
    public virtual ICollection<MarkupRuleCondition> Conditions { get; set; } =
        new List<MarkupRuleCondition>();
}
