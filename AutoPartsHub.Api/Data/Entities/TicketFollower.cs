namespace AutoPartsHub.Api.Data.Entities;

/// <summary>Somebody who asked to hear about a ticket.</summary>
public partial class TicketFollower
{
    public string Id { get; set; } = null!;

    public string TicketId { get; set; } = null!;

    /// <summary>Staff or customer alike.</summary>
    public string ClientId { get; set; } = null!;

    public DateTime CreatedAt { get; set; }
}
