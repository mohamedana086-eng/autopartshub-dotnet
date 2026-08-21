using System;
using System.Collections.Generic;

namespace AutoPartsHub.Api.Data.Entities;

public partial class OrderItem
{
    public string Id { get; set; } = null!;

    public string OrderId { get; set; } = null!;

    public string ProductId { get; set; } = null!;

    public int Quantity { get; set; }

    public double UnitPrice { get; set; }

    public virtual Order Order { get; set; } = null!;

    public virtual ICollection<OrderItemAllocation> OrderItemAllocations { get; set; } = new List<OrderItemAllocation>();

    public virtual Product Product { get; set; } = null!;
}
