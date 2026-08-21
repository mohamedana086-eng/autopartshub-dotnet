using System;
using System.Collections.Generic;

namespace AutoPartsHub.Api.Data.Entities;

public partial class VehicleSystem
{
    public string Id { get; set; } = null!;

    public string Name { get; set; } = null!;

    public string Slug { get; set; } = null!;

    public string Icon { get; set; } = null!;

    public int Order { get; set; }

    public virtual ICollection<Product> Products { get; set; } = new List<Product>();
}
