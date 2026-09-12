namespace AutoPartsHub.Api.Data.Entities;

/// <summary>How far one salesperson's reach goes. One row each; absent means own.</summary>
public partial class ManagerAccess
{
    public string Id { get; set; } = null!;

    public string ManagerId { get; set; } = null!;

    /// <summary>own | selected | all — see <c>ManagerReach</c>.</summary>
    public string Reach { get; set; } = null!;

    public string? GrantedById { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
