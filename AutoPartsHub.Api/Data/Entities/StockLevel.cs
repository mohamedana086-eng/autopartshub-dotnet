using System;
using System.Collections.Generic;

namespace AutoPartsHub.Api.Data.Entities;

public partial class StockLevel
{
    public string Id { get; set; } = null!;

    public string ProductId { get; set; } = null!;

    public string WarehouseId { get; set; } = null!;

    public int Quantity { get; set; }

    public int Reserved { get; set; }

    public string? BinLocation { get; set; }

    public DateTime UpdatedAt { get; set; }

    public virtual Product Product { get; set; } = null!;

    public virtual Warehouse Warehouse { get; set; } = null!;
}
