using System;
using System.Collections.Generic;

namespace AutoPartsHub.Api.Data.Entities;

public partial class CartItem
{
    public string Id { get; set; } = null!;

    public string CartId { get; set; } = null!;

    public string ProductId { get; set; } = null!;

    public int Quantity { get; set; }

    public DateTime AddedAt { get; set; }

    public virtual Cart Cart { get; set; } = null!;

    public virtual Product Product { get; set; } = null!;
}
