using System;
using System.Collections.Generic;

namespace AutoPartsHub.Api.Data.Entities;

public partial class Manufacturer
{
    public string Id { get; set; } = null!;

    public string Name { get; set; } = null!;

    public bool IsOem { get; set; }

    public virtual ICollection<Product> Products { get; set; } = new List<Product>();
}
