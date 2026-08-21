using System;
using System.Collections.Generic;

namespace AutoPartsHub.Api.Data.Entities;

public partial class VehicleMake
{
    public string Id { get; set; } = null!;

    public string Name { get; set; } = null!;

    public List<string>? WmiCodes { get; set; }

    public int? TecDocId { get; set; }

    public virtual ICollection<VehicleModel> VehicleModels { get; set; } = new List<VehicleModel>();
}
