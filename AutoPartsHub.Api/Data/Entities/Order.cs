using System;
using System.Collections.Generic;

namespace AutoPartsHub.Api.Data.Entities;

public partial class Order
{
    public string Id { get; set; } = null!;

    public string Reference { get; set; } = null!;

    public string ClientId { get; set; } = null!;

    public string Status { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public string CurrencyCode { get; set; } = null!;

    public double CurrencyRate { get; set; }

    public virtual Client Client { get; set; } = null!;

    public virtual ICollection<OrderItem> OrderItems { get; set; } = new List<OrderItem>();
}
