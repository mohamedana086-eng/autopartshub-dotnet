using System;
using System.Collections.Generic;

namespace AutoPartsHub.Api.Data.Entities;

/// <summary>One technical specification — "Diameter: 256 mm", "Axle: Front axle".</summary>
public partial class ProductSpec
{
    public string Id { get; set; } = null!;

    public string ProductId { get; set; } = null!;

    /// <summary>What is being measured: "Diameter", "Axle", "Charge type".</summary>
    public string Label { get; set; } = null!;

    /// <summary>
    /// Always text, never a number.
    /// </summary>
    /// <remarks>
    /// "155", "M14x1.5", "Front axle" and "yes" are all values a catalogue
    /// gives, and a numeric column would have to refuse three of them.
    /// Nothing here is arithmetic.
    /// </remarks>
    public string Value { get; set; } = null!;

    /// <summary>Kept apart from the value, so a display can decide whether to show it.</summary>
    public string? Unit { get; set; }

    /// <summary>Lowest first. The first three are what fits on a search result row.</summary>
    public int SortOrder { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual Product Product { get; set; } = null!;
}
