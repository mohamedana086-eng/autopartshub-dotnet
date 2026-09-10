using System;
using System.Collections.Generic;

namespace AutoPartsHub.Api.Data.Entities;

/// <summary>A group of parts priced together.</summary>
public partial class GoodsCategory
{
    public string Id { get; set; } = null!;

    public string Name { get; set; } = null!;

    /// <summary>Stable across a rename, so a saved filter keeps working.</summary>
    public string Slug { get; set; } = null!;

    public string? Description { get; set; }

    /// <summary>
    /// The category's own markup, and the reason this table exists.
    /// </summary>
    /// <remarks>
    /// Nullable as a pair with <see cref="MarkupValue"/>: a category can be
    /// purely organisational and hold no opinion about price. Half a markup is
    /// not a smaller markup, it is an incomplete one, and a check constraint
    /// refuses it.
    /// </remarks>
    public string? MarkupType { get; set; }

    public double? MarkupValue { get; set; }

    /// <summary>
    /// The floor under a percentage markup, when the type is PERCENT_MIN.
    /// </summary>
    /// <remarks>
    /// Set exactly when the type is PERCENT_MIN and never otherwise — a floor
    /// against a fixed amount is not a smaller floor, it is a contradiction, and
    /// a check constraint refuses it.
    /// </remarks>
    public double? MarkupMinAmount { get; set; }

    public int SortOrder { get; set; }

    /// <summary>Switched off rather than deleted, so past orders keep the name that priced them.</summary>
    public bool Active { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual ICollection<Product> Products { get; set; } = new List<Product>();
}
