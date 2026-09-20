using System;
using System.Collections.Generic;

namespace AutoPartsHub.Api.Data.Entities;

/// <summary>What a VIN told us, one row per KIND of vehicle rather than per lookup.</summary>
/// <remarks>
/// Two customers with the same model produce the same pattern, and storing it
/// twice would be keeping more data to learn the same thing. The counter gives
/// the traffic-weighted rate; the row count gives the distinct one.
/// </remarks>
public partial class VinLookup
{
    public string Id { get; set; } = null!;

    /// <summary>Positions 1-8, then '*' for the check digit, then position 10.</summary>
    public string Pattern { get; set; } = null!;

    /// <summary>Positions 1-3, carried separately so a report can group by manufacturer.</summary>
    public string Wmi { get; set; } = null!;

    /// <summary>Decoded from position 10. Null when the character is not a year code.</summary>
    public int? ModelYear { get; set; }

    public string? MakeName { get; set; }

    /// <summary>
    /// How many variants we ended up offering.
    /// </summary>
    /// <remarks>
    /// The problem being measured: a customer choosing from two is served, and
    /// one choosing from ninety is not.
    /// </remarks>
    public int CandidateCount { get; set; }

    public int Lookups { get; set; }

    public DateTime FirstSeenAt { get; set; }

    public DateTime LastSeenAt { get; set; }

    /// <summary>
    /// What the free decoder made of it, filled in later by a batch job.
    /// </summary>
    /// <remarks>
    /// Never filled on the customer's request: an external service must not be
    /// able to slow down or fail a lookup somebody is waiting on. Null means
    /// "not asked yet", which is a different state from asked and unanswerable
    /// — that is <see cref="Decoded"/> false.
    /// </remarks>
    public DateTime? DecodedAt { get; set; }

    public bool? Decoded { get; set; }

    public string? DecodedModel { get; set; }

    public string? DecodedTrim { get; set; }

    public string? DecodedEngine { get; set; }

    /// <summary>The decoder's own status, kept verbatim so a failure can be read.</summary>
    public string? DecoderStatus { get; set; }
}
