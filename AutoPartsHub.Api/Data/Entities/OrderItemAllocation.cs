using System;
using System.Collections.Generic;

namespace AutoPartsHub.Api.Data.Entities;

public partial class OrderItemAllocation
{
    public string Id { get; set; } = null!;

    public string OrderItemId { get; set; } = null!;

    public string WarehouseId { get; set; } = null!;

    public int Quantity { get; set; }

    public virtual OrderItem OrderItem { get; set; } = null!;

    public virtual Warehouse Warehouse { get; set; } = null!;
}
