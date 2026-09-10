using System;
using System.Collections.Generic;

namespace AutoPartsHub.Api.Data.Entities;

/// <summary>A search that found nothing, and how often.</summary>
public partial class SearchMiss
{
    public string Id { get; set; } = null!;

    public string Term { get; set; } = null!;

    /// <summary>
    /// Whether anything was selected at the time.
    /// </summary>
    /// <remarks>
    /// What keeps the report honest: "we do not sell this" and "we do not sell
    /// this from that supplier" are different findings, and a column that could
    /// not tell them apart would inflate the first with instances of the second.
    /// </remarks>
    public bool Narrowed { get; set; }

    public int Searches { get; set; }

    public DateTime FirstSeenAt { get; set; }

    public DateTime LastSeenAt { get; set; }
}
