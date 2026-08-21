using System;
using System.Collections.Generic;

namespace AutoPartsHub.Api.Data.Entities;

public partial class ProductImage
{
    public string Id { get; set; } = null!;

    public string ProductId { get; set; } = null!;

    public string Url { get; set; } = null!;

    public string? Alt { get; set; }

    public int SortOrder { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual Product Product { get; set; } = null!;
}
