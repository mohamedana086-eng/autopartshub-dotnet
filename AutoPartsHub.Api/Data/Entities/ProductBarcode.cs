using System;
using System.Collections.Generic;

namespace AutoPartsHub.Api.Data.Entities;

/// <summary>A code this part answers to when it is scanned.</summary>
public partial class ProductBarcode
{
    public string Id { get; set; } = null!;

    public string ProductId { get; set; } = null!;

    /// <summary>
    /// Text, never a number.
    /// </summary>
    /// <remarks>
    /// A barcode can lead with zeros — 036000291452 read as an integer is a
    /// different code, and one that scans as nothing.
    /// </remarks>
    public string Code { get; set; } = null!;

    /// <summary>ean13 | ean8 | upca | itf14 | other, derived from the code's shape.</summary>
    public string Kind { get; set; } = null!;

    /// <summary>Lowest first. The one a part leads with is the one worth printing.</summary>
    public int SortOrder { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual Product Product { get; set; } = null!;
}
