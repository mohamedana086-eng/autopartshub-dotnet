using System;
using System.Collections.Generic;

namespace AutoPartsHub.Api.Data.Entities;

public partial class Fitment
{
    public string Id { get; set; } = null!;

    public string ProductId { get; set; } = null!;

    public string VariantId { get; set; } = null!;

    public string? Note { get; set; }

    public virtual Product Product { get; set; } = null!;

    public virtual VehicleVariant Variant { get; set; } = null!;
}
