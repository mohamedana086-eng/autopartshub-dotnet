using System;
using System.Collections.Generic;

namespace AutoPartsHub.Api.Data.Entities;

public partial class VehicleModel
{
    public string Id { get; set; } = null!;

    public string MakeId { get; set; } = null!;

    public string Name { get; set; } = null!;

    public int YearFrom { get; set; }

    public int? YearTo { get; set; }

    public int? TecDocId { get; set; }

    public virtual VehicleMake Make { get; set; } = null!;

    public virtual ICollection<VehicleVariant> VehicleVariants { get; set; } = new List<VehicleVariant>();
}
