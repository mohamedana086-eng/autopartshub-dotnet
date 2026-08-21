using System;
using System.Collections.Generic;

namespace AutoPartsHub.Api.Data.Entities;

public partial class PriceList
{
    public string Id { get; set; } = null!;

    public string Name { get; set; } = null!;

    public string? Description { get; set; }

    public bool Active { get; set; }

    public string? SourceName { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public virtual ICollection<PriceListItem> PriceListItems { get; set; } = new List<PriceListItem>();
}
