using System;
using System.Collections.Generic;

namespace AutoPartsHub.Api.Data.Entities;

/// <summary>One support conversation.</summary>
public partial class Ticket
{
    public string Id { get; set; } = null!;

    /// <summary>What a customer quotes on the phone. Unique, and not the id.</summary>
    public string Reference { get; set; } = null!;

    public string ClientId { get; set; } = null!;

    /// <summary>The order it is about, where it is about one.</summary>
    public string? OrderId { get; set; }

    public string Subject { get; set; } = null!;

    public string Status { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    /// <summary>What the queue is ordered by: the one waiting longest is at the top.</summary>
    public DateTime LastMessageAt { get; set; }

    public virtual Client Client { get; set; } = null!;

    public virtual Order? Order { get; set; }

    public virtual ICollection<TicketMessage> Messages { get; set; } = new List<TicketMessage>();
}
