using System;
using System.Collections.Generic;

namespace AutoPartsHub.Api.Data.Entities;

public partial class Interchange
{
    public string Id { get; set; } = null!;

    public string SourceId { get; set; } = null!;

    public string TargetPartNo { get; set; } = null!;

    public string TargetManufacturer { get; set; } = null!;

    public bool ExactMatch { get; set; }

    public bool IsOem { get; set; }

    public virtual Product Source { get; set; } = null!;
}
