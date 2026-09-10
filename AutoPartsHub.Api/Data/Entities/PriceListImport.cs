using System;
using System.Collections.Generic;

namespace AutoPartsHub.Api.Data.Entities;

/// <summary>One attempt to load a price list, and how it went.</summary>
public partial class PriceListImport
{
    public string Id { get; set; } = null!;

    /// <summary>Null once the list it loaded has been deleted.</summary>
    public string? PriceListId { get; set; }

    /// <summary>The list's name as it was at the time, so the log survives a rename.</summary>
    public string ListName { get; set; } = null!;

    public string? SourceName { get; set; }

    public string? UploadedById { get; set; }

    public string UploadedByName { get; set; } = null!;

    public string Outcome { get; set; } = null!;

    public int RowsSent { get; set; }

    public int Accepted { get; set; }

    public int Rejected { get; set; }

    /// <summary>
    /// How many of the rejections were kept.
    /// </summary>
    /// <remarks>
    /// Not the same as <see cref="Rejected"/>: a load that refuses fifty
    /// thousand rows stores a bounded number of them, and the difference
    /// between the two is what says the detail is a sample.
    /// </remarks>
    public int RejectedStored { get; set; }

    /// <summary>Why the whole load failed, where it did.</summary>
    public string? Error { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual PriceList? PriceList { get; set; }

    public virtual ICollection<PriceListImportRow> Rows { get; set; } = new List<PriceListImportRow>();
}
