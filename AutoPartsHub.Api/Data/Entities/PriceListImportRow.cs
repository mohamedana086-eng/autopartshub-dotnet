using System;
using System.Collections.Generic;

namespace AutoPartsHub.Api.Data.Entities;

/// <summary>One row a load refused, and the sentence saying why.</summary>
public partial class PriceListImportRow
{
    public string Id { get; set; } = null!;

    public string ImportId { get; set; } = null!;

    /// <summary>Which line of the file, so it can be found and fixed.</summary>
    public int Line { get; set; }

    public string PartNumber { get; set; } = null!;

    /// <summary>
    /// As it arrived, not as a number.
    /// </summary>
    /// <remarks>
    /// The whole point of keeping it: the rows in here are the ones that could
    /// not be read, and "12,50" or "on request" is the evidence of why. Parsing
    /// it into a numeric column would discard the thing being reported.
    /// </remarks>
    public string Price { get; set; } = null!;

    public string? Currency { get; set; }

    public string Reason { get; set; } = null!;

    public virtual PriceListImport Import { get; set; } = null!;
}
