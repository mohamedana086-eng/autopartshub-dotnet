using System;
using System.Collections.Generic;

namespace AutoPartsHub.Api.Data.Entities;

/// <summary>
/// The offer that wins for each part. A view, not a table.
/// </summary>
/// <remarks>
/// Which supplier a part is bought from used to be whichever row the database
/// returned first, which made a part's price depend on the physical order of a
/// table. This settles it in one place: highest supplier priority, then lowest
/// purchase price, then supplier code as a tie-break that never changes.
///
/// A view rather than a column on Product, because "who wins" is a function of
/// rows that change independently — an offer switched off, a supplier stopped,
/// a price loaded — and a stored answer would be stale between whichever of
/// those happened last and whoever remembered to recompute it.
///
/// Keyless: EF must not try to track or update it.
/// </remarks>
public partial class BestOffer
{
    public string ProductId { get; set; } = null!;

    public string SupplierId { get; set; } = null!;

    public double PurchasePrice { get; set; }

    public int? StockDays { get; set; }

    public string SupplierCode { get; set; } = null!;

    public string SupplierName { get; set; } = null!;
}
