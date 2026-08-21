using System;
using System.Collections.Generic;

namespace AutoPartsHub.Api.Data.Entities;

public partial class RetailOutlet
{
    public string Id { get; set; } = null!;

    public string Code { get; set; } = null!;

    public string Name { get; set; } = null!;

    public string? City { get; set; }

    public string? Address { get; set; }

    public string? Phone { get; set; }

    public string? WarehouseId { get; set; }

    public bool Active { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual Warehouse? Warehouse { get; set; }
}
