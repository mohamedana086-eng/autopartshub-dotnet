using System;
using System.Collections.Generic;

namespace AutoPartsHub.Api.Data.Entities;

public partial class PriceListItem
{
    public string Id { get; set; } = null!;

    public string PriceListId { get; set; } = null!;

    public string ProductId { get; set; } = null!;

    public double Price { get; set; }

    public double? SourcePrice { get; set; }

    public string? SourceCurrency { get; set; }

    public double? MarkupPercent { get; set; }

    public string? SourcePartNumber { get; set; }

    public virtual PriceList PriceList { get; set; } = null!;

    public virtual Product Product { get; set; } = null!;
}
