using System;
using System.Collections.Generic;

namespace AutoPartsHub.Api.Data.Entities;

public partial class Supplier
{
    public string Id { get; set; } = null!;

    public string Name { get; set; } = null!;

    public string Code { get; set; } = null!;

    public string Reliability { get; set; } = null!;

    public string Slug { get; set; } = null!;

    public string? Description { get; set; }

    public int? Rating { get; set; }

    public bool? AcceptsReturns { get; set; }

    public string? Country { get; set; }

    public int? GuaranteeMonths { get; set; }

    public int? DefaultStockDays { get; set; }

    public string? PurchaseCurrencyId { get; set; }

    public bool Active { get; set; }

    public DateTime? ApprovedAt { get; set; }

    public virtual ICollection<Client> Clients { get; set; } = new List<Client>();

    public virtual ICollection<MarkupRule> MarkupRules { get; set; } = new List<MarkupRule>();

    public virtual ICollection<Product> Products { get; set; } = new List<Product>();

    public virtual Currency? PurchaseCurrency { get; set; }
}
