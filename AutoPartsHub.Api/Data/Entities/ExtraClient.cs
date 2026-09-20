namespace AutoPartsHub.Api.Data.Entities;

/// <summary>An account granted to a salesperson one at a time, read under
/// <c>selected</c>.</summary>
public partial class ExtraClient
{
    public string Id { get; set; } = null!;

    public string ManagerId { get; set; } = null!;

    public string ClientId { get; set; } = null!;

    public string? GrantedById { get; set; }

    public DateTime CreatedAt { get; set; }
}
