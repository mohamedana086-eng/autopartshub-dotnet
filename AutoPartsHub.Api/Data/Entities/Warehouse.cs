using System;
using System.Collections.Generic;

namespace AutoPartsHub.Api.Data.Entities;

public partial class Warehouse
{
    public string Id { get; set; } = null!;

    public string Code { get; set; } = null!;

    public string Name { get; set; } = null!;

    public string? City { get; set; }

    public string? Address { get; set; }

    public bool Active { get; set; }

    public int Priority { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual ICollection<OrderItemAllocation> OrderItemAllocations { get; set; } = new List<OrderItemAllocation>();

    public virtual ICollection<RetailOutlet> RetailOutlets { get; set; } = new List<RetailOutlet>();

    public virtual ICollection<StockLevel> StockLevels { get; set; } = new List<StockLevel>();
}
