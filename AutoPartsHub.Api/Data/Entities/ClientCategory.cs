using System;
using System.Collections.Generic;

namespace AutoPartsHub.Api.Data.Entities;

public partial class ClientCategory
{
    public string Id { get; set; } = null!;

    public string Name { get; set; } = null!;

    public double MarkupPercent { get; set; }

    public double MinOrderAmount { get; set; }

    public bool RequiresApproval { get; set; }

    public int ShelfLifeDays { get; set; }

    public virtual ICollection<Client> Clients { get; set; } = new List<Client>();

}
