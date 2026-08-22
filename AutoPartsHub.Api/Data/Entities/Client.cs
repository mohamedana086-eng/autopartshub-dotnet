using System;
using System.Collections.Generic;

namespace AutoPartsHub.Api.Data.Entities;

public partial class Client
{
    public string Id { get; set; } = null!;

    public string Name { get; set; } = null!;

    public string Email { get; set; } = null!;

    public string? PasswordHash { get; set; }

    public string Role { get; set; } = null!;

    public string? City { get; set; }

    public string? CategoryId { get; set; }

    public DateTime CreatedAt { get; set; }

    public double DiscountPercent { get; set; }

    public string? CurrencyId { get; set; }

    public string? SalesManagerId { get; set; }

    public string? SupplierId { get; set; }

    public virtual Cart? Cart { get; set; }

    public virtual ClientCategory? Category { get; set; }

    public virtual Currency? Currency { get; set; }

    public virtual ICollection<Client> InverseSalesManager { get; set; } = new List<Client>();

    public virtual ICollection<Notification> Notifications { get; set; } = new List<Notification>();

    public virtual ICollection<Order> Orders { get; set; } = new List<Order>();

    public virtual Client? SalesManager { get; set; }

    public virtual Supplier? Supplier { get; set; }
}
