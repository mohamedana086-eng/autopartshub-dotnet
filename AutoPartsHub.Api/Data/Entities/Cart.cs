using System;
using System.Collections.Generic;

namespace AutoPartsHub.Api.Data.Entities;

public partial class Cart
{
    public string Id { get; set; } = null!;

    public string ClientId { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public virtual ICollection<CartItem> CartItems { get; set; } = new List<CartItem>();

    public virtual Client Client { get; set; } = null!;
}
