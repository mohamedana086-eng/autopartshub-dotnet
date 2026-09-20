using System;
using System.Collections.Generic;

namespace AutoPartsHub.Api.Data.Entities;

/// <summary>What one supplier will sell a part for.</summary>
public partial class SupplierOffer
{
    public string Id { get; set; } = null!;

    public string ProductId { get; set; } = null!;

    public string SupplierId { get; set; } = null!;

    public double PurchasePrice { get; set; }

    public int? StockDays { get; set; }

    public string? SupplierPartNumber { get; set; }

    public bool Active { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public virtual Product Product { get; set; } = null!;

    public virtual Supplier Supplier { get; set; } = null!;
}
