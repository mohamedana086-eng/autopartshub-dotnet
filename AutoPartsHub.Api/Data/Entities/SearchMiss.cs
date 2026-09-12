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

    /// <summary>When somebody crossed it off, or null while it is open.</summary>
    /// <remarks>
    /// Set with <see cref="ResolvedById"/> and cleared with it — a date with
    /// no decider is half a record of a decision, and the database says so.
    ///
    /// Nothing reopens itself: a resolved term searched again keeps climbing
    /// and stays resolved. Resolving is a judgement, and a machine undoing one
    /// because the case recurred would make "we are not going to sell this"
    /// impossible to say once. The screen shows both dates instead.
    /// </remarks>
    public DateTime? ResolvedAt { get; set; }

    public string? ResolvedById { get; set; }
}
