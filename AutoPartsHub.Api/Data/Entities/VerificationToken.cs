using System;
using System.Collections.Generic;

namespace AutoPartsHub.Api.Data.Entities;

public partial class VerificationToken
{
    public string Id { get; set; } = null!;

    public string ClientId { get; set; } = null!;

    public string Purpose { get; set; } = null!;

    public string TokenHash { get; set; } = null!;

    public DateTime ExpiresAt { get; set; }

    public DateTime? UsedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual Client Client { get; set; } = null!;
}
