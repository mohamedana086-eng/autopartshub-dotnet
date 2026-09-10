using System;
using System.Collections.Generic;

namespace AutoPartsHub.Api.Data.Entities;

/// <summary>One message on a ticket.</summary>
public partial class TicketMessage
{
    public string Id { get; set; } = null!;

    public string TicketId { get; set; } = null!;

    /// <summary>Null where the account has since gone.</summary>
    public string? AuthorId { get; set; }

    /// <summary>Kept as text so a deleted account does not blank a conversation.</summary>
    public string AuthorName { get; set; } = null!;

    public bool FromStaff { get; set; }

    /// <summary>
    /// A note for the desk, not for the customer.
    /// </summary>
    /// <remarks>
    /// The one field on this table that must never reach the wrong reader. The
    /// customer-facing queries filter on it; <c>TicketTests</c> is what says
    /// they still do.
    /// </remarks>
    public bool Internal { get; set; }

    public string Body { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public virtual Ticket Ticket { get; set; } = null!;
}
