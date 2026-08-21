using System;
using System.Collections.Generic;

namespace AutoPartsHub.Api.Data.Entities;

public partial class Notification
{
    public string Id { get; set; } = null!;

    public string ClientId { get; set; } = null!;

    public string Type { get; set; } = null!;

    public string Title { get; set; } = null!;

    public string? Body { get; set; }

    public string? Link { get; set; }

    public DateTime? ReadAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual Client Client { get; set; } = null!;
}
