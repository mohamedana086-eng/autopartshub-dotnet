using System;
using System.Collections.Generic;

namespace AutoPartsHub.Api.Data.Entities;

public partial class Product
{
    public string Id { get; set; } = null!;

    public string PartNumber { get; set; } = null!;

    public string Name { get; set; } = null!;

    public string? Description { get; set; }

    public string ManufacturerId { get; set; } = null!;

    public string VehicleSystemId { get; set; } = null!;

    public double BasePrice { get; set; }

    public string Currency { get; set; } = null!;

    public int StockDays { get; set; }

    public DateTime CreatedAt { get; set; }

    public string? SupplierId { get; set; }

    public int? TecDocId { get; set; }

    public virtual ICollection<CartItem> CartItems { get; set; } = new List<CartItem>();

    public virtual ICollection<Fitment> Fitments { get; set; } = new List<Fitment>();

    public virtual ICollection<Interchange> Interchanges { get; set; } = new List<Interchange>();

    public virtual Manufacturer Manufacturer { get; set; } = null!;

    public virtual ICollection<OrderItem> OrderItems { get; set; } = new List<OrderItem>();

    public virtual ICollection<PriceListItem> PriceListItems { get; set; } = new List<PriceListItem>();

    public virtual ICollection<ProductImage> ProductImages { get; set; } = new List<ProductImage>();

    public virtual ICollection<StockLevel> StockLevels { get; set; } = new List<StockLevel>();

    public virtual Supplier? Supplier { get; set; }

    public virtual VehicleSystem VehicleSystem { get; set; } = null!;
}
