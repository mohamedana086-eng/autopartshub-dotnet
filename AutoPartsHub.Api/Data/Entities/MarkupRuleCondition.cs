using System;
using System.Collections.Generic;

namespace AutoPartsHub.Api.Data.Entities;

/// <summary>
/// One acceptable answer on one dimension of one markup rule.
/// </summary>
/// <remarks>
/// A child table rather than a column per filter, so a rule can say "any of
/// these three suppliers" — one statement with three answers, not three
/// statements. See AutoPartsHub.Api.Pricing.MarkupDimensions.
/// </remarks>
public partial class MarkupRuleCondition
{
    public string Id { get; set; } = null!;

    public string RuleId { get; set; } = null!;

    /// <summary>Which dimension this narrows on — supplier, city, partType.</summary>
    public string Dimension { get; set; } = null!;

    /// <summary>
    /// The value it accepts. Plain text with no foreign key behind it, which
    /// is what lets one table hold every kind of reference — and why deleting
    /// the row it names has to sweep the condition by hand.
    /// </summary>
    public string Value { get; set; } = null!;

    /// <summary>
    /// Turns the condition inside out: "anything EXCEPT these". A whole
    /// dimension points one way or the other; the write path enforces that,
    /// since a CHECK cannot.
    /// </summary>
    public bool Negated { get; set; }

    public virtual MarkupRule Rule { get; set; } = null!;
}
