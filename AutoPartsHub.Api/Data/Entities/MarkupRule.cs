using System;
using System.Collections.Generic;

namespace AutoPartsHub.Api.Data.Entities;

public partial class MarkupRule
{
    public string Id { get; set; } = null!;

    public string Label { get; set; } = null!;

    public int Priority { get; set; }

    public string? ClientCategoryId { get; set; }

    public string? SupplierId { get; set; }

    public string? ManufacturerName { get; set; }

    public string? VehicleSystemSlug { get; set; }

    public string? PartNumberPrefix { get; set; }

    public double? PurchasePriceFrom { get; set; }

    public double? PurchasePriceTo { get; set; }

    public string Type { get; set; } = null!;

    public double Value { get; set; }

    public bool Active { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual ClientCategory? ClientCategory { get; set; }

    public virtual Supplier? Supplier { get; set; }
}
