using System;
using System.Collections.Generic;

namespace AutoPartsHub.Api.Data.Entities;

public partial class VehicleVariant
{
    public string Id { get; set; } = null!;

    public string ModelId { get; set; } = null!;

    public string Name { get; set; } = null!;

    public string? EngineCode { get; set; }

    public int? PowerKw { get; set; }

    public string Fuel { get; set; } = null!;

    public int YearFrom { get; set; }

    public int? YearTo { get; set; }

    public int? TecDocId { get; set; }

    public string? BodyType { get; set; }

    public string? Region { get; set; }

    public string? SteeringSide { get; set; }

    public string? Transmission { get; set; }

    public virtual ICollection<Fitment> Fitments { get; set; } = new List<Fitment>();

    public virtual VehicleModel Model { get; set; } = null!;
}
